using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Koda.Controls;
using Koda.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SmoothMotion.Windows;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Playback;

namespace Koda.Playback;

internal sealed record SmoothMotionDiagnostics(string Status, bool Active, int RequestedFps,
    double TargetFps, double DisplayHz, double SourceFps, double AudioDelayMs, string Gpu, MotionSample Counters);

/// <summary>UI-thread capture and a dedicated timed presenter share one serialized GPU context.</summary>
internal sealed class SmoothMotionPlayback
{
    private readonly MediaPlayer _player;
    private readonly Action<double> _setAudioDelay;
    private readonly MotionMetrics _metrics = new();
    private OutputClock _outputClock = new(120);
    private MotionEngine? _engine;
    private IDirect3DSurface? _surface;
    private VideoSurface? _target;
    private CancellationTokenSource? _pumpStop;
    private Task? _pump;
    private int _generation, _captureQueued, _revealQueued;
    private bool _starting, _failed, _revealed;
    private string _failureStatus = "", _gpu = "—";
    private double _fps, _targetFps = 120, _displayHz = 60, _lastFrame, _lastCapture, _windowStart, _graceUntil, _startedAt;
    private int _expected, _missed, _requestedFps = 120, _performanceCap = 120;
    private uint _width, _height;
    private volatile bool _running;
    private string _status = "Off";
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public string Status => _status;
    public event Action? Changed;
    public SmoothMotionDiagnostics Diagnostics => new(_status, _revealed && _running, _requestedFps,
        _targetFps, _displayHz, _fps, _engine is null ? 0 : 1000 / _fps, _gpu, _metrics.Sample(Now));

    public SmoothMotionPlayback(MediaPlayer player, Action<double> setAudioDelay)
    {
        _player = player;
        _setAudioDelay = setAudioDelay;
        _metrics.Reset(Now);
    }

    public void Attach(VideoSurface? target)
    {
        if (_target == target) return;
        Stop();
        if (_target is { } old) { old.Unloaded -= OnUnloaded; old.PresenterFailed -= Fail; }
        _target = target;
        if (target is not null) { target.Unloaded += OnUnloaded; target.PresenterFailed += Fail; }
    }
    private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();
    public void NewSource()
    {
        Stop(); _failed = false; _performanceCap = 120; _gpu = "—";
        _metrics.Reset(Now);
    }
    public void Idle(bool enabled)
    {
        Stop(); SetStatus(enabled ? "Ready: play a video at 1080p or lower" : "Off");
    }

    public void Update(bool enabled, bool separateAudio, bool live, bool playing, bool buffering, double speed, double sourceFps, int maxFps)
    {
        _requestedFps = maxFps;
        _displayHz = DisplayRefresh.For(_target);
        var targetFps = OutputClock.TargetRate(Math.Min(maxFps, _performanceCap), _displayHz);
        if (_engine is not null && Math.Abs(targetFps - _targetFps) > 0.5) Stop();
        _targetFps = targetFps;
        string? bypass = !enabled ? "Off"
            : _failed ? _failureStatus
            : Koda.Services.EnergySaver.IsOn ? "Paused: Energy Saver"
            : live ? "Not available for live video"
            : !separateAudio ? "Choose a fixed VP9 quality to use smooth motion"
            : speed != 1 ? "Paused: available at normal speed"
            : sourceFps >= targetFps * 0.95 ? "Not needed: video is already near the output rate"
            : sourceFps < 17 ? "Waiting for video frame rate"
            : null;
        if (!enabled) { _failed = false; _performanceCap = 120; }
        if (bypass is not null) { Stop(); SetStatus(bypass); return; }
        var wasRunning = _running;
        _running = playing && !buffering;
        if (_running && !wasRunning) Reset();
        if (!_running)
        {
            if (wasRunning) Reset();
            SetStatus(buffering ? "Waiting for video" : "Paused"); return;
        }
        var session = _player.PlaybackSession;
        var width = session.NaturalVideoWidth;
        var height = session.NaturalVideoHeight;
        if (width == 0 || height == 0 || _target?.IsLoaded != true) { SetStatus("Waiting for video"); return; }
        if (width > 1920 || height > 1920 || (ulong)width * height > 1920 * 1080)
        { Stop(); SetStatus("Choose 1080p or lower to use smooth motion"); return; }
        if (_engine is not null && (width != _width || height != _height || Math.Abs(sourceFps - _fps) > 0.1)) Stop();
        if (_engine is not null && Now - Math.Max(_startedAt, Volatile.Read(ref _lastCapture)) > 3)
        { Fail(new InvalidOperationException("The frame server stopped delivering pictures.")); return; }
        if (_engine is null && !_starting) _ = StartAsync(width, height, sourceFps);
        else if (_revealed) SetStatus(ActiveStatus());
    }

    private string ActiveStatus() => $"{_fps:0.##} → {_targetFps:0.##} fps" +
        (_performanceCap < _requestedFps ? " · reduced to keep playback smooth" : "");

    private async Task StartAsync(uint width, uint height, double fps)
    {
        var generation = ++_generation;
        _starting = true; SetStatus("Starting smooth motion");
        MotionEngine? created = null;
        try
        {
            created = await Task.Run(() => new MotionEngine(width, height));
            if (generation != _generation || !_running || _target?.IsLoaded != true) { created.Dispose(); return; }
            _engine = created; created = null;
            _width = width; _height = height; _fps = fps; _gpu = _engine.AdapterName;
            _surface = WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(_engine.InputSurface);
            _setAudioDelay(1 / fps);
            _player.VideoFrameAvailable += OnFrame;
            _player.IsVideoFrameServerEnabled = true;
            _outputClock = new OutputClock(_targetFps);
            Reset();
            var engine = _engine;
            var dispatcher = _target.DispatcherQueue;
            var cancellation = _pumpStop = new CancellationTokenSource();
            _pump = Task.Run(() => Pump(engine, dispatcher, generation, cancellation.Token));
            SetStatus("Waiting for video frames");
        }
        catch (Exception error) { created?.Dispose(); if (generation == _generation) Fail(error); }
        finally { if (generation == _generation) _starting = false; }
    }

    private void OnFrame(MediaPlayer sender, object args)
    {
        if (Interlocked.Exchange(ref _captureQueued, 1) != 0) { _metrics.Capture(true); return; }
        var generation = _generation;
        if (_target?.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () =>
        {
            Interlocked.Exchange(ref _captureQueued, 0);
            if (generation != _generation || _engine is not { } engine || _surface is null) return;
            try
            {
                bool presented = false;
                lock (engine.SyncRoot)
                {
                    var now = Now;
                    if (_lastCapture > 0 && now - _lastCapture > 2 / _fps) engine.Reset();
                    _player.CopyFrameToVideoSurface(_surface);
                    engine.SubmitFrame(); _metrics.Capture(false);
                    _lastFrame = _lastCapture = now;
                    if (!_running) { engine.RenderLatest(); presented = engine.Present(); }
                }
                if (presented) Reveal();
            }
            catch (Exception error) { Fail(error); }
        }) != true) Interlocked.Exchange(ref _captureQueued, 0);
    }

    private void Pump(MotionEngine engine, DispatcherQueue dispatcher, int generation, CancellationToken cancellation)
    {
        try
        {
            using var timer = new FrameTimer();
            while (!cancellation.IsCancellationRequested)
            {
                bool reveal = false;
                double wait = 0.01;
                lock (engine.SyncRoot)
                {
                    if (cancellation.IsCancellationRequested) return;
                    var now = Now;
                    if (_running && engine.HasPair && _lastFrame > 0)
                    {
                        if (_outputClock.TryTick(now, out var missed))
                        {
                            var phase = (float)Math.Clamp((now - _lastFrame) * _fps, 0, 1);
                            var began = Now;
                            engine.Render(phase);
                            var accepted = engine.Present();
                            _metrics.Present(accepted, phase > 0.001f && phase < 0.999f, missed, (Now - began) * 1000);
                            if (now >= _graceUntil) { _expected += missed + 1; _missed += missed + (accepted ? 0 : 1); }
                            reveal = accepted;
                            if (now - _windowStart >= 10)
                            {
                                if (_expected > 0 && _missed > _expected * 0.1)
                                    throw new TimeoutException("More than 10% of presentation ticks missed.");
                                _expected = _missed = 0; _windowStart = now;
                            }
                        }
                        wait = Math.Min(0.05, _outputClock.SecondsUntilNextTick(Now));
                    }
                }
                if (reveal && Interlocked.Exchange(ref _revealQueued, 1) == 0)
                    dispatcher.TryEnqueue(() =>
                    {
                        if (generation != _generation) return;
                        try { Reveal(); } catch (Exception error) { Fail(error); }
                    });
                if (!timer.Wait(wait, cancellation)) return;
            }
        }
        catch (Exception error)
        {
            dispatcher.TryEnqueue(() => { if (generation == _generation) Fail(error); });
        }
    }

    public void Reset()
    {
        if (_engine is not { } engine) return;
        lock (engine.SyncRoot)
        {
            engine.Reset(); _startedAt = Now; _lastFrame = _lastCapture = 0;
            _outputClock.Reset(); _graceUntil = Now + 6;
            _expected = _missed = 0; _windowStart = _graceUntil;
        }
    }
    private void Reveal()
    {
        if (_revealed || _engine is null) return;
        _target!.SetPresenter(_engine);
        if (_engine is null) return;
        _revealed = true; SetStatus(_running ? ActiveStatus() : "Paused");
    }
    private void Fail(Exception error)
    {
        VideoTrace.Write($"SmoothMotion disabled: 0x{error.HResult:X8} {error.Message}");
        if (error is TimeoutException && _targetFps > 60)
        {
            Stop(); _performanceCap = 60;
            SetStatus("Reducing output to 60 fps to keep playback smooth"); return;
        }
        _failed = true;
        _failureStatus = error is TimeoutException
            ? "Paused: this PC could not keep up. Turn off and on to retry."
            : "Unavailable: normal video playback restored. Turn off and on to retry.";
        Stop(); SetStatus(_failureStatus);
    }
    private void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status; Changed?.Invoke();
    }
    public void Stop()
    {
        ++_generation; _starting = false;
        var cancellation = _pumpStop; _pumpStop = null;
        cancellation?.Cancel();
        if (cancellation is not null)
        {
            if (_pump is { } pump) _ = pump.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
            else cancellation.Dispose();
        }
        _pump = null;
        _player.VideoFrameAvailable -= OnFrame;
        if (_engine is not { } engine) return;
        try { _player.IsVideoFrameServerEnabled = false; }
        catch (Exception error) { VideoTrace.Write($"SmoothMotion restore: {error.Message}"); }
        try { _target?.SetPresenter(null); }
        catch (Exception error) { VideoTrace.Write($"SmoothMotion detach: {error.Message}"); }
        try { _setAudioDelay(0); }
        catch (Exception error) { VideoTrace.Write($"SmoothMotion audio restore: {error.Message}"); }
        _surface?.Dispose(); _surface = null;
        lock (engine.SyncRoot) engine.Dispose();
        _engine = null; _revealed = false; _revealQueued = 0; _lastFrame = _lastCapture = 0;
    }
}
