using System;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage.Streams;

namespace Koda.Interop;

/// <summary>
/// System Media Transport Controls in MANUAL mode: no MediaPlayer auto-binding,
/// every update owned here, so the system surfaces stay ours alone. The
/// window-bound SMTC comes from Windows.Media.SystemMediaTransportControlsInterop.GetForWindow
/// (OS projection).
/// </summary>
public sealed class SmtcController : IDisposable
{
    private SystemMediaTransportControls? _smtc;
    private Action<SystemMediaTransportControlsButton>? _onButton;
    private DispatcherQueue? _dispatcher;

    public void Bind(nint hwnd, Action<SystemMediaTransportControlsButton> onButton)
    {
        _onButton = onButton;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        var smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc = smtc;
        smtc.ButtonPressed += OnButtonPressed;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        // Queue exists as of Phase 3: next/previous ride the real queue grammar.
        smtc.IsNextEnabled = false;
        smtc.IsPreviousEnabled = false;
        smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
        // No track: the card is silent (Stopped) until the first content arrives.
        smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // Despite the window-bound SMTC being created on the UI thread, the button
        // event can arrive on a COM worker thread. Everything it triggers (playback,
        // taskbar, XAML glyphs) belongs to the dispatcher.
        var button = args.Button;
        if (_dispatcher is { } dispatcher)
        {
            dispatcher.TryEnqueue(() => _onButton?.Invoke(button));
            return;
        }

        _onButton?.Invoke(button);
    }

    /// <summary>Playback state changed - manual, from the PlaybackService.</summary>
    public void UpdatePlaybackState(bool playing) => OnUi(() =>
    {
        if (_smtc is null)
        {
            return;
        }

        _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
    });

    /// <summary>The card's track: title, artist and the artwork the flyout shows.</summary>
    public void UpdateTrack(string title, string artist, string? artworkUrl) => OnUi(() =>
    {
        if (_smtc is null)
        {
            return;
        }

        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = title;
        updater.MusicProperties.Artist = string.IsNullOrWhiteSpace(artist) ? "YouTube Music" : artist;
        if (artworkUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var image))
        {
            updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(image);
        }
        else
        {
            updater.Thumbnail = null;
        }

        updater.Update();
    });

    /// <summary>Nothing playing: the card goes blank rather than stale.</summary>
    public void ClearTrack() => OnUi(() =>
    {
        if (_smtc is null)
        {
            return;
        }

        _smtc.DisplayUpdater.ClearAll();
        _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
    });

    /// <summary>Transport enablement follows the queue: honest buttons on the flyout.</summary>
    public void SetTransport(bool canPrevious, bool canNext) => OnUi(() =>
    {
        if (_smtc is null)
        {
            return;
        }

        _smtc.IsPreviousEnabled = canPrevious;
        _smtc.IsNextEnabled = canNext;
    });

    /// <summary>Timeline so the flyout slider and lock screen match the bar's scrubber.</summary>
    public void SetTimeline(double elapsedSeconds, double durationSeconds) => OnUi(() =>
    {
        if (_smtc is null)
        {
            return;
        }

        if (!double.IsFinite(elapsedSeconds) || !double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            return;
        }

        var timeline = new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(durationSeconds),
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = TimeSpan.FromSeconds(durationSeconds),
            Position = TimeSpan.FromSeconds(Math.Clamp(elapsedSeconds, 0, durationSeconds)),
        };
        _smtc.UpdateTimelineProperties(timeline);
    });

    /// <summary>Playback handed off: card silent, manual button promises held true.</summary>
    public void Reset() => OnUi(() =>
    {
        if (_smtc is null)
        {
            return;
        }

        ClearTrack();
        _smtc.IsPreviousEnabled = false;
        _smtc.IsNextEnabled = false;
    });

    /// <summary>Every SMTC surface is a COM object with thread affinity; the playback events
    /// can arrive on a media worker, so these calls hop to the window's dispatcher.</summary>
    private void OnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    public void Dispose()
    {
        if (_smtc is not null)
        {
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc = null;
        }
    }
}
