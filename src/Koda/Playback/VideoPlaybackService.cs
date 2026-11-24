using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Core.Video;
using Koda.Helpers;
using Koda.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;
using MediaPlayerElement = Koda.Controls.VideoSurface;

namespace Koda.Playback;

/// <summary>
/// The one video player: what's on, its DASH stream on Windows' MediaPlayer, the state its
/// controls draw (time, playing, buffering), its settings (quality, speed, captions), full
/// screen and theater, and keeping it out of the music's way: starting a video pauses the
/// songs, and a song starting pauses the video. It outlives the watch page: leaving the page
/// keeps the video going in the bar, remembers where each video got to, and at the end moves
/// on to Up Next.
///
/// Mac hands YouTube's HLS master to AVPlayer. Windows refuses it, so the stream is the
/// on-demand DASH manifest <see cref="DashManifest"/> builds from the same /player answer;
/// AdaptiveMediaSource switches and seeks natively. A chosen quality is a new source, not a
/// raised bitrate, because Windows ignores a raised DesiredMinBitrate mid-play.
/// </summary>
public sealed class VideoPlaybackService
{
    public static VideoPlaybackService Instance { get; } = new();

    private readonly MediaPlayer _player = new();
    private readonly SmoothMotionPlayback _smooth;
    private double _smoothAudioDelay;
    private double _sourceFps;
    private int? _bufferStatsError;

    /// <summary>The soundtrack, beside a VP9 picture that plays as a file in <see cref="_player"/>.
    /// Idle while the DASH manifest plays both.</summary>
    private readonly MediaPlayer _audio = new();
    private readonly HttpClient _http = new();
    private DispatcherQueueTimer? _tick;
    private DispatcherQueueTimer? _sync;

    /// <summary>
    /// The one clock a VP9 picture and its sound both play to. It is never moved: a
    /// MediaTimelineController given a new Position keeps counting from the old one for seconds,
    /// ignores Pause meanwhile, and the sound's player loses its place (the picture moves in
    /// silence). Nor is it swapped under open players: each then catches up on its own, the
    /// sound seconds behind the picture. So each opened video gets one clock from zero, attached
    /// before its files open, and places are set with <see cref="_offset"/>
    /// (TimelineControllerPositionOffset on both players): the place is the clock's time plus the
    /// offset. A seek changes the offset; the clock only ever starts and pauses.
    /// </summary>
    private MediaTimelineController _clock;

    /// <summary>The clock was paused because the picture or the sound ran dry, and runs again
    /// once both have caught up.</summary>
    private bool _held;
    private DateTimeOffset _heldSince;

    /// <summary>Where the current clock's zero is in the video, in seconds.</summary>
    private double _offset;
    private bool _videoReady;
    private bool _audioReady;
    private double? _waitingFor;
    private DateTimeOffset _waitingSince;
    private bool _wasBuffering;

    /// <summary>The picture is a VP9 file and the sound plays in <see cref="_audio"/>.</summary>
    private bool _direct;

    /// <summary>VP9 failed or stalled on this video: it plays the manifest's H.264 from then on.</summary>
    private bool _directFailed;

    private static readonly Lazy<Task<bool>> Vp9Decodes = new(async () =>
    {
        try
        {
            var decoders = await new CodecQuery().FindAllAsync(CodecKind.Video, CodecCategory.Decoder, CodecSubtypes.VideoFormatVP90);
            return decoders.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>Known once the codec query answers; false until then, so the menu never offers
    /// what can't play.</summary>
    private bool _vp9;
    private DispatcherQueueTimer? _autoplayTimer;
    private DispatcherQueue? _dispatch;
    private MediaPlayerElement? _element;
    private InMemoryRandomAccessStream? _manifestStream;
    private AdaptiveMediaSource? _adaptive;
    private VideoStream? _stream;
    private int _generation;
    private double _pendingPosition;
    private double _duration;
    private double _lastSavedPosition = -1;
    private bool _shouldPlay;
    private bool _recovering;
    private int _recoverAttempts;
    private DateTimeOffset _bufferingSince = DateTimeOffset.MaxValue;
    private DateTimeOffset _lastDownloadFailure = DateTimeOffset.MinValue;
    private bool _musicPausedForVideo;
    private List<(double Start, double End, string Text)> _cues = [];
    private VideoCaptionTrack[] _captionTracks = [];
    private VideoCaptionTrack? _captionTrack;
    private string _caption = "";
    private bool _loadingCaptions;
    private int _autoplaySeconds;
    private static readonly SponsorBlock Sponsors = new();
    private SkipSegment[] _segments = [];
    private double _lastTickPosition;

    private VideoPlaybackService()
    {
        _smooth = new SmoothMotionPlayback(_player, SetSmoothAudioDelay);
        _smooth.Changed += () => Changed?.Invoke();
        _player.CommandManager.IsEnabled = false;
        _player.AudioCategory = MediaPlayerAudioCategory.Movie;
        _player.Volume = Math.Clamp(App.Volume / 100, 0, 1);
        _player.MediaOpened += (_, _) => OnDispatch(OnOpened);
        _player.MediaEnded += (_, _) => OnDispatch(() =>
        {
            // A delayed soundtrack finishes on the shared clock, one source frame later.
            if (!_direct || _smoothAudioDelay <= 0) OnEnded();
        });
        _player.MediaFailed += (_, args) => OnDispatch(() => OnFailed(args));
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => OnDispatch(OnStateChanged);

        _audio.CommandManager.IsEnabled = false;
        _audio.AudioCategory = MediaPlayerAudioCategory.Movie;
        _audio.Volume = _player.Volume;
        _audio.MediaOpened += (_, _) => OnDispatch(OnAudioOpened);
        _audio.MediaFailed += (_, args) => OnDispatch(() =>
        {
            if (_direct) OnFailed(args);
        });
        _audio.PlaybackSession.PlaybackStateChanged += (_, _) => OnDispatch(() => Changed?.Invoke());
        _clock = CreateClock();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ClientProfiles.Default.BrowserUserAgent);
    }

    // MARK: State

    /// <summary>The video loaded, whether or not it is playing.</summary>
    public Video? Current { get; private set; }

    /// <summary>Everything under the player: details, channel, Up Next. Fetched with the video.</summary>
    public WatchPage? Watch { get; private set; }

    public bool HasVideo => Current is not null;

    public bool IsLoading { get; private set; }

    /// <summary>Playing, or meant to be and waiting for the picture: the button shows Pause.</summary>
    public bool IsPlaying => _direct
        ? _shouldPlay && Error is null
        : _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    /// <summary>Waiting for the picture: a start, seek or quality change not yet moving, or a
    /// player (or the shared clock) out of data while it should be playing.</summary>
    public bool IsBuffering => _shouldPlay && Error is null && (_waitingFor is not null || (_direct
        ? _clock.State == MediaTimelineControllerState.Stalled || Waiting(_player) || Waiting(_audio)
        : Waiting(_player)));

    public string? Error { get; private set; }

    /// <summary>Where the picture is, or is going: while a start or seek loads, the place asked
    /// for, so the scrubber holds still and a second skip adds to the first. Otherwise read from
    /// the picture's player in both modes: just after a start the shared clock can still report
    /// the place it started from.</summary>
    public double Position => _waitingFor ?? _player.PlaybackSession.Position.TotalSeconds;

    public double Duration => _duration > 0 ? _duration : _player.PlaybackSession.NaturalDuration.TotalSeconds;

    /// <summary>The Quality menu: VP9 where this PC decodes it, H.264 where it doesn't.</summary>
    public IReadOnlyList<VideoQuality> Qualities => _stream?.Choices(_vp9 && !_directFailed) ?? [];

    /// <summary>0 is Auto; otherwise the height the source was held to.</summary>
    public int Quality { get; private set; }

    /// <summary>What is on screen now: the VP9 file or the H.264 manifest, for the menu's note.</summary>
    public bool IsDirect => _direct;

    public double Speed { get; private set; } = 1;

    public VideoCaptionTrack[] CaptionTracks => _captionTracks;

    public VideoCaptionTrack? CaptionTrack => _captionTrack;

    public bool CaptionsOn { get; private set; }

    public string CurrentCaption => CaptionsOn ? _caption : "";

    /// <summary>The seconds left before Up Next starts by itself; 0 when no countdown is running.</summary>
    public int AutoplaySeconds => _autoplaySeconds;

    public Video? Next => Watch?.Next;

    /// <summary>SponsorBlock's marks for this video, for the scrubber; empty when off or none.</summary>
    public IReadOnlyList<SkipSegment> SkipSegments => App.Settings.SkipsSponsors ? _segments : [];

    /// <summary>The chapter playing now; null for a video without chapters.</summary>
    public VideoChapter? Chapter => Watch?.ChapterAt(Position);

    /// <summary>The player itself, for a page's MediaPlayerElement and the mini window.</summary>
    public MediaPlayer Player => _player;

    public string SmoothMotionStatus => _smooth.Status;

    public void SetVideoStats(bool enabled)
    {
        App.Settings.Update(s => s.ShowsVideoStats = enabled);
        Changed?.Invoke();
    }

    public void SetSmoothMotionMaxFps(int fps)
    {
        App.Settings.Update(s => s.SmoothMotionMaxFps = fps);
        UpdateSmoothMotion();
        Changed?.Invoke();
    }

    internal VideoDiagnostics Diagnostics()
    {
        var session = _player.PlaybackSession;
        var height = (int)Math.Min(session.NaturalVideoWidth, session.NaturalVideoHeight);
        var rung = (_direct ? _stream?.Direct : _stream?.Qualities)?.FirstOrDefault(q => q.Height == height);
        if (!_direct && _adaptive?.CurrentPlaybackBitrate is > 0)
            rung = _stream?.Qualities.FirstOrDefault(q => q.Bandwidth == _adaptive.CurrentPlaybackBitrate) ?? rung;
        return new(Current?.Id ?? "", session.NaturalVideoWidth, session.NaturalVideoHeight,
            _direct ? "VP9 · SDR · separate audio" : "H.264 · DASH · adaptive audio/video",
            rung?.Fps ?? _sourceFps, IsLoading ? "Opening" : IsBuffering ? "Buffering" : IsPlaying ? "Playing" : "Paused",
            Position, Duration, Speed, ReadBufferProgress(session), rung?.Bandwidth ?? 0, _smooth.Diagnostics);
    }

    private double ReadBufferProgress(MediaPlaybackSession session)
    {
        return VideoStatistics.BufferProgress(session, error =>
        {
            if (_bufferStatsError != error.HResult)
            {
                _bufferStatsError = error.HResult;
                VideoTrace.Write($"Buffer statistics unavailable: {error}");
            }
        });
    }

    public void SetSmoothMotion(bool enabled)
    {
        App.Settings.Update(s => s.SmoothMotion = enabled);
        UpdateSmoothMotion();
        Changed?.Invoke();
    }

    private void UpdateSmoothMotion()
    {
        if (!HasVideo) { _smooth.Idle(App.Settings.SmoothMotion); return; }
        _smooth.Update(App.Settings.SmoothMotion, _direct,
            Current?.IsLive == true, IsPlaying, IsLoading || IsBuffering, Speed, _sourceFps, App.Settings.SmoothMotionMaxFps);
    }

    private void SetSmoothAudioDelay(double seconds)
    {
        _smoothAudioDelay = seconds;
        if (_direct)
        {
            _audio.TimelineControllerPositionOffset = TimeSpan.FromSeconds(_offset - seconds);
            if (_duration > _offset) _clock.Duration = TimeSpan.FromSeconds(_duration - _offset + seconds);
        }
    }

    /// <summary>Raised for anything the controls draw: the video, loading, playing, quality,
    /// speed, captions, Up Next and the autoplay countdown.</summary>
    public event Action? Changed;

    /// <summary>Raised about twice a second while a video is loaded, for the scrubber.</summary>
    public event Action? Tick;

    /// <summary>A video finished; the watch page shows its end screen.</summary>
    public event Action? Ended;

    public void Connect(DispatcherQueue dispatch)
    {
        _dispatch = dispatch;
        PlaybackService.Instance.PlayStateChanged += () =>
        {
            // A song starting takes the sound back; the video waits where it is.
            if (PlaybackService.Instance.IsPlaying && IsPlaying)
            {
                PauseCore();
                _shouldPlay = false;
                Changed?.Invoke();
            }
        };
        _ = KnowVp9Async();
    }

    private async Task KnowVp9Async()
    {
        _vp9 = await Vp9Decodes.Value;
        OnDispatch(() => Changed?.Invoke());
    }

    // MARK: Opening

    public async Task PlayAsync(Video video, double? startAt = null)
    {
        if (Current?.Id == video.Id && _player.Source is not null && Error is null)
        {
            if (startAt is { } position) Seek(position);
            _shouldPlay = true;
            PlayCore();
            Changed?.Invoke();
            return;
        }

        Current = video;
        _directFailed = false;
        _segments = [];
        _lastTickPosition = 0;
        _ = LoadSegmentsAsync(video);
        Watch = null;
        Error = null;
        _caption = "";
        _captionTrack = null;
        _captionTracks = [];
        _cues = [];
        CaptionsOn = false;
        _recoverAttempts = 0;
        _lastSavedPosition = -1;
        Quality = App.Settings.VideoQuality;
        CancelAutoplay();
        _musicPausedForVideo |= PlaybackService.Instance.IsPlaying;
        PlaybackService.Instance.Pause();
        Changed?.Invoke();
        await OpenAsync(video, startAt ?? App.Library.ProgressOf(video.Id)?.ResumeAt ?? 0, Quality, record: true);
    }

    private async Task OpenAsync(Video video, double startAt, int quality, bool record)
    {
        var generation = ++_generation;
        VideoTrace.Write($"---- open {video.Id} start at {startAt:0.00} quality={quality} {Snapshot()}");
        IsLoading = true;
        Error = null;
        _pendingPosition = startAt;
        _shouldPlay = true;
        Changed?.Invoke();
        try
        {
            _vp9 = await Vp9Decodes.Value;
            var stream = await EngineHost.Music.VideoStreamAsync(video.Id);
            if (generation != _generation) return;
            _stream = stream;
            var choices = Qualities;
            if (quality != 0 && !choices.Any(q => q.Height == quality))
            {
                // A video offering less plays its best below the chosen height.
                quality = choices.FirstOrDefault(q => q.Height <= quality)?.Height
                    ?? choices.LastOrDefault()?.Height ?? 0;
            }

            if (!await SetSourceAsync(stream, quality, generation)) return;
            if (record) App.Library.RecordWatch(video);

            // Up Next, details and comments: the watch page's own answer, fetched once here so
            // the page, the bar and autoplay all share it.
            _ = LoadWatchAsync(video, generation);
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            IsLoading = false;
            _shouldPlay = false;
            Error = $"{Errors.Title(error)}. {Errors.Describe(error)}";
            Changed?.Invoke();
        }
    }

    private async Task LoadSegmentsAsync(Video video)
    {
        if (!App.Settings.SkipsSponsors) return;
        var segments = await Sponsors.SegmentsAsync(video.Id);
        OnDispatch(() =>
        {
            if (Current?.Id != video.Id) return;
            _segments = segments;
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// Playing into a marked segment skips to its end, with a note saying what was skipped. Only
    /// when play crosses its start: a listener who seeks into one, or back to it, means to watch.
    /// </summary>
    private void SkipSponsor(double position)
    {
        var last = _lastTickPosition;
        _lastTickPosition = position;
        if (!App.Settings.SkipsSponsors || _segments.Length == 0 || !IsPlaying) return;
        if (position < last || position - last > 2) return;
        var segment = _segments.FirstOrDefault(s => last <= s.Start + 0.05 && position >= s.Start && position < s.End - 0.5);
        if (segment is null) return;
        Seek(segment.End);
        _lastTickPosition = segment.End;
        App.Shell?.Note($"Skipped {segment.Label}");
    }

    // MARK: Rating and subscribing

    /// <summary>Like, dislike or clear, on the account. Drawn at once; put back if YouTube says no.</summary>
    public async Task RateAsync(LikeStatus status)
    {
        if (Watch is not { } watch || Current is not { } video) return;
        if (!EngineHost.Account.IsSignedIn)
        {
            App.Shell?.Note("Sign in to YouTube in Settings to rate videos");
            return;
        }

        var before = watch.LikeStatus;
        Watch = watch with { LikeStatus = status };
        Changed?.Invoke();
        try
        {
            await EngineHost.Music.RateVideoAsync(video.Id, status);
        }
        catch (Exception)
        {
            if (Watch is { } now && Current?.Id == video.Id) Watch = now with { LikeStatus = before };
            Changed?.Invoke();
            App.Shell?.Note("YouTube didn't take that rating. Try again.");
        }
    }

    public async Task SetSubscribedAsync(bool subscribed)
    {
        if (Watch is not { } watch || Current is not { } video) return;
        var channel = watch.Video.Channel ?? video.Channel;
        if (channel?.Id is not { } channelId) return;

        // Signed out, the channel is followed on this PC: its uploads make the Subscriptions feed.
        if (!EngineHost.Account.IsSignedIn)
        {
            App.Library.SetSubscribed(channel.Avatar is null && video.Channel?.Avatar is { } avatar ? channel with { Avatar = avatar } : channel, subscribed);
            Watch = watch with { IsSubscribed = subscribed };
            Changed?.Invoke();
            return;
        }

        var before = watch.IsSubscribed;
        Watch = watch with { IsSubscribed = subscribed };
        Changed?.Invoke();
        try
        {
            await EngineHost.Music.SetSubscribedOnYouTubeAsync(channelId, subscribed);
        }
        catch (Exception)
        {
            if (Watch is { } now && Current?.Id == video.Id) Watch = now with { IsSubscribed = before };
            Changed?.Invoke();
            App.Shell?.Note(subscribed ? "Couldn't subscribe. Try again." : "Couldn't unsubscribe. Try again.");
        }
    }

    private async Task LoadWatchAsync(Video video, int generation)
    {
        try
        {
            var watch = await EngineHost.Music.WatchPageAsync(video.Id);
            if (generation != _generation || Current?.Id != video.Id) return;
            Watch = watch;
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // The video still plays; the page shows what it knows.
        }
    }

    /// <summary>
    /// Puts the chosen quality on: a VP9 rung as two files, picture and sound, or the H.264
    /// manifest (Auto, a height with no VP9, or a video whose VP9 already failed). False when
    /// a newer open took over meanwhile.
    /// </summary>
    private async Task<bool> SetSourceAsync(VideoStream stream, int quality, int generation)
    {
        _smooth.NewSource();
        var rung = quality == 0 ? null : Qualities.FirstOrDefault(q => q.Height == quality && q.IsDirect);
        _sourceFps = rung?.Fps ?? 0;
        VideoTrace.Write($"source quality={quality} direct={rung is not null && stream.AudioUrl is not null} "
            + $"pending={_pendingPosition:0.00} clock was {_clock.State}@{_clock.Position.TotalSeconds:0.00}");
        if (rung is not null && stream.AudioUrl is { } audioUrl)
        {
            _adaptive = null;
            _direct = true;
            _videoReady = _audioReady = false;
            // The players open already at the place: a fresh clock at zero, offset to it.
            UseClockAt(Math.Max(0, _pendingPosition), run: false);
            _player.Source = MediaSource.CreateFromUri(new Uri(rung.DirectUrl!));
            _audio.Source = MediaSource.CreateFromUri(new Uri(audioUrl));
            WaitForPicture(_pendingPosition);
            return true;
        }

        var manifest = quality == 0 ? stream.Manifest : stream.ManifestFor(quality);
        var source = await CreateSourceAsync(manifest, stream, quality);
        if (generation != _generation) return false;
        StopAudio();
        _adaptive = source;
        _player.Source = MediaSource.CreateFromAdaptiveMediaSource(source);
        return true;
    }

    private void StopAudio()
    {
        _direct = false;
        _clock.Pause();
        _audio.Source = null;
        _player.TimelineController = null;
        _audio.TimelineController = null;
        _player.TimelineControllerPositionOffset = TimeSpan.Zero;
        _audio.TimelineControllerPositionOffset = TimeSpan.Zero;
        _offset = 0;
    }

    /// <summary>A clock that follows this service's state, the old one's events ignored.</summary>
    private MediaTimelineController CreateClock()
    {
        var clock = new MediaTimelineController();
        clock.StateChanged += (sender, _) => OnDispatch(() =>
        {
            if (ReferenceEquals(sender, _clock)) Changed?.Invoke();
        });
        clock.Ended += (sender, _) => OnDispatch(() =>
        {
            if (ReferenceEquals(sender, _clock) && _direct) OnEnded();
        });
        return clock;
    }

    /// <summary>
    /// A fresh clock from zero for a video about to open (or, as a last resort, a seek the
    /// offset can't make), both players offset to <paramref name="at"/> seconds. Only ever
    /// before the files open, or as that fallback: swapping clocks under open players puts the
    /// sound behind the picture (see <see cref="_clock"/>).
    /// </summary>
    private void UseClockAt(double at, bool run)
    {
        _clock.Pause();
        _held = false;
        _clock = CreateClock();
        try
        {
            _clock.ClockRate = Speed;
        }
        catch (Exception)
        {
            // An older clock without a rate still keeps the two players together.
        }

        SetOffset(at);
        _player.TimelineController = _clock;
        _audio.TimelineController = _clock;
        if (run) _clock.Resume();
        VideoTrace.Write($"new clock at {at:0.00} run={run}");
    }

    /// <summary>Both players to the clock's time plus <paramref name="offset"/>, and the clock's
    /// end to where the video ends. False when Windows refuses the offset.</summary>
    private bool SetOffset(double offset)
    {
        try
        {
            var span = TimeSpan.FromSeconds(offset);
            _player.TimelineControllerPositionOffset = span;
            _audio.TimelineControllerPositionOffset = TimeSpan.FromSeconds(offset - _smoothAudioDelay);
            _offset = offset;
        }
        catch (Exception error)
        {
            VideoTrace.Write($"offset {offset:0.00} refused: 0x{error.HResult:X8} {error.Message}");
            return false;
        }

        try
        {
            if (_duration > offset) _clock.Duration = TimeSpan.FromSeconds(_duration - offset + _smoothAudioDelay);
        }
        catch (Exception)
        {
            // Without an end the players' own end still finishes the video.
        }

        return true;
    }

    /// <summary>A seek with the clock left alone: the offset makes up the difference.</summary>
    private void SeekDirect(double position)
    {
        var offset = position - _clock.Position.TotalSeconds;
        VideoTrace.Write($"seek by offset to {position:0.00} (offset {offset:0.00})");
        if (!SetOffset(offset)) UseClockAt(position, run: _shouldPlay);
    }

    private async Task<AdaptiveMediaSource> CreateSourceAsync(string manifest, VideoStream stream, int quality)
    {
        var memory = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(memory.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(Encoding.UTF8.GetBytes(manifest));
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        memory.Seek(0);
        _manifestStream = memory;
        var creation = await AdaptiveMediaSource.CreateFromStreamAsync(
            memory, new Uri("https://koda.invalid/video.mpd"), "application/dash+xml");
        if (creation.Status != AdaptiveMediaSourceCreationStatus.Success || creation.MediaSource is null)
        {
            throw new InvalidOperationException(creation.Status.ToString());
        }

        var source = creation.MediaSource;
        // Auto should start at a watchable picture rather than the bottom rung, then follow
        // the connection from there.
        if (quality == 0)
        {
            var initial = stream.Qualities.FirstOrDefault(q => q.Height <= 1080) ?? stream.Qualities.FirstOrDefault();
            if (initial is not null) source.InitialBitrate = (uint)Math.Max(1, initial.Bandwidth);
        }

        source.DownloadFailed += (_, _) => _lastDownloadFailure = DateTimeOffset.UtcNow;
        return source;
    }

    // MARK: Player events

    private void OnOpened()
    {
        VideoTrace.Write($"picture opened pending={_pendingPosition:0.00} {Snapshot()}");
        if (_direct)
        {
            _videoReady = true;
            FinishDirectOpen();
            return;
        }

        var at = _pendingPosition > 0.5 ? _pendingPosition : 0;
        if (at > 0)
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(at);
        }

        _pendingPosition = 0;
        _duration = _player.PlaybackSession.NaturalDuration.TotalSeconds;
        IsLoading = false;
        try
        {
            _player.PlaybackSession.PlaybackRate = Speed;
        }
        catch (Exception)
        {
            // A source that refuses a rate still plays at 1x.
        }

        if (_shouldPlay)
        {
            PlayCore();
            WaitForPicture(at);
        }

        Changed?.Invoke();
    }

    private void OnAudioOpened()
    {
        VideoTrace.Write($"sound opened {Snapshot()}");
        if (!_direct) return;
        _audioReady = true;
        FinishDirectOpen();
    }

    /// <summary>
    /// Picture and sound both open, already at the place (the clock was set up for it when the
    /// sources went on): the clock learns where the video ends, and starts.
    /// </summary>
    private void FinishDirectOpen()
    {
        if (!_videoReady || !_audioReady) return;
        _videoReady = _audioReady = false;
        _duration = _player.PlaybackSession.NaturalDuration.TotalSeconds;

        var at = Math.Max(0, _pendingPosition);
        VideoTrace.Write($"both open, start at {at:0.00} {Snapshot()}");
        _pendingPosition = 0;
        IsLoading = false;

        // The clock opened with the players, at zero, offset to the place. It is not swapped
        // now: a seek made while it opened moves the offset, and the end is set now that the
        // length is known.
        SetOffset(at);
        if (_shouldPlay)
        {
            _clock.Resume();
            WaitForPicture(at);
        }

        Changed?.Invoke();
    }

    // MARK: Two players, one clock

    /// <summary>Play: the shared clock when picture and sound are separate files.</summary>
    private void PlayCore()
    {
        VideoTrace.Write($"play {Snapshot()}");
        _held = false;
        if (_direct) _clock.Resume();
        else _player.Play();
    }

    private void PauseCore()
    {
        VideoTrace.Write($"pause {Snapshot()}");
        _held = false;
        if (_direct) _clock.Pause();
        else _player.Pause();
    }

    /// <summary>
    /// The clock does not wait for a player that runs out of data, so it is held while either
    /// buffers and let go when both have caught up (or after three seconds, when Windows' own
    /// catching up does better than waiting longer).
    /// </summary>
    private void HoldClock()
    {
        if (!_direct || IsLoading || !_shouldPlay || _player.Source is null || _audio.Source is null) return;
        var buffering = Waiting(_player) || Waiting(_audio);
        if (!_held && buffering && _clock.State == MediaTimelineControllerState.Running)
        {
            _clock.Pause();
            _held = true;
            _heldSince = DateTimeOffset.UtcNow;
            VideoTrace.Write($"clock held {Snapshot()}");
        }
        else if (_held && (!buffering || DateTimeOffset.UtcNow - _heldSince > TimeSpan.FromSeconds(3)))
        {
            _held = false;
            _clock.Resume();
            VideoTrace.Write($"clock let go {Snapshot()}");
        }
    }

    // MARK: Loading state

    /// <summary>One line of state for the trace: the picture, the sound, the clock.</summary>
    private string Snapshot()
    {
        static string Of(MediaPlayer player) =>
            $"{player.PlaybackSession.PlaybackState}@{player.PlaybackSession.Position.TotalSeconds:0.00}";
        return $"direct={_direct} should={_shouldPlay} wait={_waitingFor?.ToString("0.00") ?? "-"} "
            + $"video={Of(_player)} audio={(_direct ? Of(_audio) : "-")} "
            + $"clock={(_direct ? $"{_clock.State}@{_clock.Position.TotalSeconds:0.00}+{_offset:0.00}" : "-")} "
            + $"music={PlaybackService.Instance.IsPlaying}";
    }

    /// <summary>A start, seek or quality change shows as loading from the moment it is asked for
    /// until the picture is moving from that place, whatever the players report meanwhile.</summary>
    private void WaitForPicture(double at)
    {
        VideoTrace.Write($"wait for picture at {at:0.00} {Snapshot()}");
        _waitingFor = at;
        _waitingSince = DateTimeOffset.UtcNow;
        Changed?.Invoke();
    }

    /// <summary>Four times a second: whether the picture has started moving, and whether the
    /// loading state changed, so the spinner comes and goes on time.</summary>
    private void CheckPicture()
    {
        if (!HasVideo) return;
        HoldClock();
        if (DateTimeOffset.UtcNow - _waitingSince < TimeSpan.FromSeconds(12) || _waitingFor is not null)
        {
            VideoTrace.Write($"tick {Snapshot()}");
        }

        if (_waitingFor is { } at)
        {
            var session = _player.PlaybackSession;
            var position = session.Position.TotalSeconds;
            var moving = session.PlaybackState == MediaPlaybackState.Playing && position >= at + 0.15 && position < at + 30;
            // Past half a minute the stall recovery has had its turn; the spinner does not stay forever.
            var given = DateTimeOffset.UtcNow - _waitingSince > TimeSpan.FromSeconds(30);
            if (moving || given || !_shouldPlay || Error is not null)
            {
                VideoTrace.Write($"wait over: moving={moving} given={given} should={_shouldPlay} error={Error is not null} "
                    + $"after {(DateTimeOffset.UtcNow - _waitingSince).TotalSeconds:0.00}s");
                _waitingFor = null;
            }
        }

        var buffering = IsBuffering;
        if (buffering && _bufferingSince == DateTimeOffset.MaxValue) _bufferingSince = DateTimeOffset.UtcNow;
        if (!buffering) _bufferingSince = DateTimeOffset.MaxValue;
        if (buffering != _wasBuffering)
        {
            _wasBuffering = buffering;
            Changed?.Invoke();
        }
    }

    private static bool Waiting(MediaPlayer player) =>
        player.PlaybackSession.PlaybackState is MediaPlaybackState.Buffering or MediaPlaybackState.Opening;

    private void OnStateChanged()
    {
        UpdateSmoothMotion();
        CheckPicture();
        Changed?.Invoke();
    }

    private void OnEnded()
    {
        _shouldPlay = false;
        if (_direct) _clock.Pause();
        UpdateSmoothMotion();
        SaveProgress(force: true);
        Ended?.Invoke();
        Changed?.Invoke();
        if (App.Settings.AutoplaysVideos && Watch?.Next is not null) BeginAutoplay();
    }

    private void OnFailed(MediaPlayerFailedEventArgs args)
    {
        if (_recovering || Current is null)
        {
            IsLoading = false;
            Error = "This video stopped playing. Try again.";
            Changed?.Invoke();
            return;
        }

        _ = RecoverAsync(args.ErrorMessage);
    }

    private async Task RecoverAsync(string reason)
    {
        if (_recovering || Current is not { } video) return;
        _recovering = true;
        _recoverAttempts++;
        var position = Position;
        try
        {
            if (_direct)
            {
                // The VP9 file failed or stalled: the same height in H.264 is the safe way on.
                _directFailed = true;
                StopAudio();
                await OpenAsync(video, position, Quality, record: false);
                return;
            }

            // googlevideo URLs expire; a refused stream is resolved again, then the same place
            // is put back.
            await EngineHost.Music.VideoStreamFailedAsync(video.Id);
            _stream = null;
            await OpenAsync(video, position, Quality, record: false);
        }
        catch (Exception)
        {
            Error = "This video stopped playing. Try again.";
            IsLoading = false;
            Changed?.Invoke();
        }
        finally
        {
            _recovering = false;
        }
    }

    // MARK: Transport

    public void TogglePlay()
    {
        VideoTrace.Write($"toggle loading={IsLoading} source={_player.Source is not null} {Snapshot()}");
        if (!HasVideo || _player.Source is null) return;
        if (IsPlaying)
        {
            PauseCore();
            _shouldPlay = false;
            SaveProgress(force: true);
        }
        else
        {
            _musicPausedForVideo |= PlaybackService.Instance.IsPlaying;
            PlaybackService.Instance.Pause();
            _shouldPlay = true;
            PlayCore();
        }

        UpdateSmoothMotion();
        Changed?.Invoke();
    }

    public void Seek(double seconds)
    {
        if (!HasVideo) return;
        _smooth.Reset();
        var duration = Duration;
        var position = Math.Clamp(seconds, 0, duration > 0 ? duration : seconds);
        VideoTrace.Write($"seek to {position:0.00} loading={IsLoading} {Snapshot()}");
        if (IsLoading)
        {
            // Not open yet: the place is taken when it is.
            _pendingPosition = position;
            return;
        }

        if (_direct)
        {
            // The clock is never moved or swapped here (see _clock).
            SeekDirect(position);
        }
        else
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(position);
        }

        if (_shouldPlay) WaitForPicture(position);
        _lastSavedPosition = -1;
        Tick?.Invoke();
    }

    public void Skip(double seconds) => Seek(Position + seconds);

    public async Task NextAsync()
    {
        if (Watch?.Next is { } next)
        {
            CancelAutoplay();
            await PlayAsync(next);
        }
    }

    public void SetSpeed(double speed)
    {
        Speed = speed is 0.5 or 0.75 or 1 or 1.25 or 1.5 or 1.75 or 2 ? speed : 1;
        UpdateSmoothMotion();
        try
        {
            _player.PlaybackSession.PlaybackRate = Speed;
        }
        catch (Exception)
        {
            // Ignored: the next source picks the rate up.
        }

        if (_direct)
        {
            try
            {
                _clock.ClockRate = Speed;
            }
            catch (Exception)
            {
                // Ignored: the clock keeps its rate.
            }
        }

        Changed?.Invoke();
    }

    public void SetVolume(double percent) => _player.Volume = _audio.Volume = Math.Clamp(percent / 100, 0, 1);

    public bool IsMuted
    {
        get => _player.IsMuted;
        set
        {
            _player.IsMuted = _audio.IsMuted = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Tries the video that failed again, from where it stopped.</summary>
    public async Task RetryAsync()
    {
        if (Current is not { } video) return;
        await EngineHost.Music.VideoStreamFailedAsync(video.Id);
        _stream = null;
        _recoverAttempts = 0;
        await OpenAsync(video, Position, Quality, record: false);
    }

    public async Task SetQualityAsync(int height)
    {
        if (Current is not { } video || _stream is null) return;
        if (height != 0 && !Qualities.Any(q => q.Height == height)) return;
        var resume = Position;
        var wasPlaying = _shouldPlay || IsPlaying;
        Quality = height;
        var generation = ++_generation;
        IsLoading = true;
        Changed?.Invoke();
        try
        {
            _pendingPosition = resume;
            _shouldPlay = wasPlaying;
            await SetSourceAsync(_stream, height, generation);
        }
        catch (Exception)
        {
            IsLoading = false;
            Error = "That quality didn't start. Try another.";
            Changed?.Invoke();
        }
    }

    // MARK: Captions

    public async Task ToggleCaptionsAsync()
    {
        if (CaptionsOn)
        {
            CaptionsOn = false;
            _caption = "";
            Changed?.Invoke();
            return;
        }

        if (_captionTracks.Length == 0) await LoadCaptionTracksAsync();
        if (_captionTracks.Length == 0) return;
        await SelectCaptionAsync(_captionTrack ?? _captionTracks[0]);
    }

    public async Task LoadCaptionTracksAsync()
    {
        if (Current is not { } video || _loadingCaptions) return;
        _loadingCaptions = true;
        try
        {
            _captionTracks = await EngineHost.Music.VideoCaptionsAsync(video.Id);
            _captionTrack ??= _captionTracks.FirstOrDefault(t => !t.IsAutomatic) ?? _captionTracks.FirstOrDefault();
        }
        catch (MusicException)
        {
            _captionTracks = [];
        }
        finally
        {
            _loadingCaptions = false;
        }

        Changed?.Invoke();
    }

    public async Task SelectCaptionAsync(VideoCaptionTrack track)
    {
        _captionTrack = track;
        try
        {
            var vtt = await _http.GetStringAsync(track.Url);
            _cues = ParseVtt(vtt);
            CaptionsOn = _cues.Count > 0;
            _caption = "";
        }
        catch (Exception)
        {
            _cues = [];
            CaptionsOn = false;
        }

        Changed?.Invoke();
    }

    private static List<(double Start, double End, string Text)> ParseVtt(string text)
    {
        var cues = new List<(double, double, string)>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var startText = line[..arrow].Trim().Split(' ')[0];
            var endText = line[(arrow + 3)..].Trim().Split(' ')[0];
            if (!TryTime(startText, out var start) || !TryTime(endText, out var end)) continue;
            var body = new StringBuilder();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
            {
                if (body.Length > 0) body.Append('\n');
                body.Append(Regex.Replace(lines[i], "<[^>]+>", ""));
            }

            var cue = System.Net.WebUtility.HtmlDecode(body.ToString()).Trim();
            if (cue.Length > 0) cues.Add((start, end, cue));
        }

        return cues;
    }

    private static bool TryTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        seconds = parts.Length == 3
            ? values[0] * 3600 + values[1] * 60 + values[2]
            : values[0] * 60 + values[1];
        return true;
    }

    // MARK: Autoplay

    private void BeginAutoplay()
    {
        _autoplaySeconds = 5;
        Changed?.Invoke();
        _autoplayTimer?.Start();
    }

    public void CancelAutoplay()
    {
        _autoplayTimer?.Stop();
        if (_autoplaySeconds == 0) return;
        _autoplaySeconds = 0;
        Changed?.Invoke();
    }

    public async Task PlayNextNowAsync()
    {
        CancelAutoplay();
        await NextAsync();
    }

    // MARK: Attach

    /// <summary>The picture-in-picture window's element while it is open: it keeps the picture,
    /// and whatever asks for it meanwhile is parked until the window closes.</summary>
    private MediaPlayerElement? _pip;
    private MediaPlayerElement? _parked;

    public bool InPictureInPicture => _pip is not null;

    public void BeginPictureInPicture(MediaPlayerElement element)
    {
        _smooth.Attach(null);
        _parked = _element;
        _element?.SetMediaPlayer(null);
        _element = null;
        _pip = element;
        Attach(element);
        Changed?.Invoke();
    }

    /// <summary>The window closed: the picture goes back to where it was asked for last.</summary>
    public void EndPictureInPicture()
    {
        if (_pip is not { } pip) return;
        _pip = null;
        Detach(pip);
        if (_parked is { } parked)
        {
            _parked = null;
            Attach(parked);
        }

        Changed?.Invoke();
    }

    /// <summary>Puts the player's picture in a page's element, taking it from wherever it was.</summary>
    public void Attach(MediaPlayerElement element)
    {
        if (_pip is not null && element != _pip)
        {
            _parked = element;
            return;
        }

        if (_element == element)
        {
            element.SetMediaPlayer(_player);
            _smooth.Attach(element);
            UpdateSmoothMotion();
            return;
        }

        _smooth.Attach(null);
        _element?.SetMediaPlayer(null);
        _element = element;
        element.SetMediaPlayer(_player);
        _smooth.Attach(element);
        UpdateSmoothMotion();
    }

    public void Detach(MediaPlayerElement element)
    {
        if (_parked == element) _parked = null;
        if (_element == element)
        {
            _smooth.Attach(null);
            element.SetMediaPlayer(null);
            _element = null;
        }
    }

    /// <summary>Whether this element is the one drawing the picture.</summary>
    public bool IsAttached(MediaPlayerElement element) => _element == element;

    // MARK: Ticks and history

    private void OnDispatch(Action action)
    {
        if (_dispatch is null) action();
        else _dispatch.TryEnqueue(() => action());
    }

    /// <summary>Called by the shell once the dispatcher is known; starts the clock and countdown.</summary>
    public void StartTimers()
    {
        if (_dispatch is null || _tick is not null) return;
        var tick = _dispatch.CreateTimer();
        tick.Interval = TimeSpan.FromMilliseconds(500);
        tick.IsRepeating = true;
        tick.Tick += (_, _) => OnTick();
        tick.Start();
        _tick = tick;

        // A VP9 picture and its sound are checked for buffering four times a second; idle otherwise.
        var sync = _dispatch.CreateTimer();
        sync.Interval = TimeSpan.FromMilliseconds(250);
        sync.IsRepeating = true;
        sync.Tick += (_, _) => CheckPicture();
        _sync = sync;
        sync.Start();

        var autoplay = _dispatch.CreateTimer();
        autoplay.Interval = TimeSpan.FromSeconds(1);
        autoplay.IsRepeating = true;
        autoplay.Tick += (_, _) =>
        {
            if (_autoplaySeconds <= 0)
            {
                autoplay.Stop();
                return;
            }

            _autoplaySeconds--;
            if (_autoplaySeconds == 0)
            {
                autoplay.Stop();
                _ = PlayNextNowAsync();
            }
            else
            {
                Changed?.Invoke();
            }
        };
        _autoplayTimer = autoplay;
    }

    private void OnTick()
    {
        UpdateSmoothMotion();
        if (!HasVideo) return;
        SkipSponsor(Position);
        Tick?.Invoke();

        if (CaptionsOn && _cues.Count > 0)
        {
            var position = Position;
            var cue = _cues.LastOrDefault(c => c.Start <= position);
            var text = cue.Text is not null && position <= cue.End ? cue.Text : "";
            if (text != _caption)
            {
                _caption = text;
                Changed?.Invoke();
            }
        }

        SaveProgress();
        if (IsBuffering && _bufferingSince != DateTimeOffset.MaxValue
            && (DateTimeOffset.UtcNow - _bufferingSince).TotalSeconds > 12
            && !_recovering && _recoverAttempts < 2 && _player.Source is not null)
        {
            _bufferingSince = DateTimeOffset.MaxValue;
            _ = RecoverAsync("stalled");
        }
    }

    private void SaveProgress(bool force = false)
    {
        if (Current is not { } video) return;
        var position = Position;
        var duration = Duration;
        if (duration <= 0 || position <= 0) return;
        if (!force && Math.Abs(position - _lastSavedPosition) < 5) return;
        _lastSavedPosition = position;
        App.Library.SetVideoProgress(position, duration, video.Id);
    }

    /// <summary>Leaving the app: the place is kept now, not on the next tick.</summary>
    public void Flush() => SaveProgress(force: true);

    /// <summary>Releases the GPU presenter before the shell's XAML tree is destroyed.</summary>
    public void Shutdown()
    {
        Flush();
        _smooth.Attach(null);
    }

    /// <summary>Stops and clears the video, for closing it from the bar.</summary>
    public void Stop()
    {
        _smooth.NewSource();
        _generation++;
        SaveProgress(force: true);
        // The clock, not the player, when picture and sound share a timeline controller:
        // Windows refuses Pause on a player that has one attached.
        PauseCore();
        _shouldPlay = false;
        StopAudio();
        _player.Source = null;
        Current = null;
        Watch = null;
        Error = null;
        IsLoading = false;
        _duration = 0;
        _stream = null;
        _cues = [];
        _captionTracks = [];
        CaptionsOn = false;
        CancelAutoplay();
        if (_musicPausedForVideo)
        {
            _musicPausedForVideo = false;
            PlaybackService.Instance.Play();
        }

        Changed?.Invoke();
    }
}
