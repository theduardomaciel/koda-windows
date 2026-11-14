using System;
using System.Linq;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Koda.Pages;

/// <summary>
/// The compact overlay's surface: the cover, what is playing, the seek bar, and the few
/// controls a window this size needs. It follows the player like the bar does, and its way
/// out is the full Now Playing page, because Windows already gives the overlay a close button.
/// </summary>
public sealed partial class MiniPlayerPage : Page
{
    private bool _attached;
    private bool _scrubbing;
    private bool _suppressVolume;
    private bool _volumeDirty;
    private DispatcherQueueTimer? _volumeSave;

    /// <summary>Raised when the listener asks for the full player. The argument is the
    /// panel to open there: <c>"lyrics"</c>, <c>"queue"</c>, or null for the player itself.</summary>
    public event Action<string?>? FullPlayerRequested;

    /// <summary>The strip the window hands to Windows as its drag region.</summary>
    public FrameworkElement DragRegion => DragBar;

    private PlaybackService Playback => PlaybackService.Instance;

    public MiniPlayerPage()
    {
        InitializeComponent();
        Attach();

        _volumeSave = DispatcherQueue.CreateTimer();
        _volumeSave.Interval = TimeSpan.FromMilliseconds(300);
        _volumeSave.IsRepeating = false;
        _volumeSave.Tick += (_, _) =>
        {
            _volumeDirty = false;
            App.SetVolume(VolumeSlider.Value);
        };

        VolumeSlider.Value = App.Volume;
        VolumeSlider.ValueChanged += OnVolumeChanged;

        Scrubbing.Attach(Progress, on => _scrubbing = on, Playback.SeekSeconds);

        RenderTrack();
        RenderPlayState();
        RenderPosition();
        RenderVolume();
    }

    private void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        Playback.TrackChanged += OnTrackChanged;
        Playback.PlayStateChanged += OnPlayStateChanged;
        Playback.PositionTick += OnPositionTick;
        Playback.QueueChanged += OnQueueChanged;
        App.Settings.Changed += OnSettingsChanged;
        Unloaded += OnUnloaded;
    }

    /// <summary>Stop listening: the window is closing, and the events outlive it.</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        Playback.TrackChanged -= OnTrackChanged;
        Playback.PlayStateChanged -= OnPlayStateChanged;
        Playback.PositionTick -= OnPositionTick;
        Playback.QueueChanged -= OnQueueChanged;
        App.Settings.Changed -= OnSettingsChanged;
        Unloaded -= OnUnloaded;

        // A volume change made just before closing should still be the one the next
        // launch opens with, even if the debounce had not fired yet.
        if (_volumeDirty)
        {
            _volumeSave?.Stop();
            _volumeDirty = false;
            App.SetVolume(VolumeSlider.Value);
        }
        else
        {
            _volumeSave?.Stop();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void OnTrackChanged() => RenderTrack();

    private void OnPlayStateChanged() => RenderPlayState();

    private void OnPositionTick() => RenderPosition();

    private void OnQueueChanged() => RenderPlayState();

    private void OnSettingsChanged(Settings settings) => DispatcherQueue.TryEnqueue(RenderVolume);

    private void RenderTrack()
    {
        var song = Playback.CurrentSong;
        TitleText.Text = song?.Title ?? "Nothing playing";
        ArtistText.Text = song?.ArtistLine ?? "";
        Art.SetSource(CoverImage, song?.Artwork);
        CoverGlyph.Visibility = song?.Artwork is null ? Visibility.Visible : Visibility.Collapsed;

        RenderPlayState();
        RenderPosition();
    }

    private void RenderPlayState()
    {
        var playing = Playback.IsPlaying;
        var loading = Playback.IsLoading;
        PlayIcon.Glyph = playing ? "\uF8AE" : "\uF5B0";
        PlayIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(PlayButton, playing ? "Pause" : "Play");
        AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");

        var hasSong = Playback.CurrentSong is not null;
        PreviousButton.IsEnabled = hasSong;
        PlayButton.IsEnabled = hasSong;
        NextButton.IsEnabled = Playback.CanNext;
        MoreButton.IsEnabled = hasSong;
        LyricsButton.IsEnabled = hasSong;
        QueueButton.IsEnabled = hasSong;
    }

    private void RenderPosition()
    {
        var duration = Playback.DurationSeconds;
        var hasDuration = duration > 0;

        Progress.IsEnabled = hasDuration;
        if (_scrubbing)
        {
            return;
        }

        Progress.Maximum = Math.Max(1, duration);
        Progress.Value = hasDuration ? Math.Clamp(Playback.PositionSeconds, 0, duration) : 0;
        UpdateTimes(Progress.Value, duration);
    }

    private void OnProgressChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_scrubbing)
        {
            UpdateTimes(e.NewValue, Playback.DurationSeconds);
        }
    }

    private void UpdateTimes(double position, double duration)
    {
        if (duration <= 0)
        {
            ElapsedText.Text = "";
            RemainingText.Text = "";
            return;
        }

        ElapsedText.Text = FormatTime(position);
        RemainingText.Text = "-" + FormatTime(Math.Max(0, duration - position));
    }

    private void RenderVolume()
    {
        var muted = Playback.IsMuted || App.Volume <= 0;
        var glyph = muted ? "\uE74F" : "\uE767";
        VolumeIcon.Glyph = glyph;
        MuteIcon.Glyph = glyph;

        var label = Playback.IsMuted ? "Unmute" : "Mute";
        ToolTipService.SetToolTip(MuteButton, label);
        AutomationProperties.SetName(MuteButton, label);
    }

    private void OnVolumeFlyoutOpening(object sender, object e)
    {
        _suppressVolume = true;
        VolumeSlider.Value = App.Volume;
        _suppressVolume = false;
        RenderVolume();
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolume)
        {
            return;
        }

        Playback.SetVolumePercent(e.NewValue);
        _volumeDirty = true;
        _volumeSave?.Stop();
        _volumeSave?.Start();
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        Playback.SetMuted(!Playback.IsMuted);
        RenderVolume();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentSong is { } song)
        {
            var menu = SongMenu.Build(song, Playback.Queue.Items.Select(i => i.Song).ToArray());
            menu.ShowAt(MoreButton);
        }
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = Playback.PreviousAsync();

    private void OnPlayClick(object sender, RoutedEventArgs e) => Playback.Toggle();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = Playback.NextAsync();

    private void OnLyricsClick(object sender, RoutedEventArgs e) => FullPlayerRequested?.Invoke("lyrics");

    private void OnQueueClick(object sender, RoutedEventArgs e) => FullPlayerRequested?.Invoke("queue");

    private void OnRestoreClick(object sender, RoutedEventArgs e) => FullPlayerRequested?.Invoke(null);

    private void OnRestoreTapped(object sender, DoubleTappedRoutedEventArgs e) => FullPlayerRequested?.Invoke(null);

    /// <summary>The native caption buttons float over the page's top-right; the window
    /// measures their width so the app's own full-player button can sit beside them.</summary>
    public void SetCaptionInset(double width) =>
        CaptionInset.Width = Math.Max(0, width);

    private static string FormatTime(double seconds)
    {
        var total = (long)Math.Floor(Math.Max(0, seconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:D2}:{secs:D2}" : $"{minutes}:{secs:D2}";
    }
}
