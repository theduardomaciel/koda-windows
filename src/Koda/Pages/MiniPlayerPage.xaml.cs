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
using Microsoft.UI.Xaml.Media;

namespace Koda.Pages;

/// <summary>
/// The compact overlay's surface: the cover, what is playing, the seek bar, and the few
/// controls a window this size needs. It follows the player like the bar does. The lyrics
/// and queue buttons grow the card into a taller panel that hosts their words and list
/// directly, instead of leaving for the full Now Playing page; the caption's expand button
/// is still the way there, because Windows already gives the overlay a close button.
/// </summary>
public sealed partial class MiniPlayerPage : Page
{
    private bool _attached;
    private bool _scrubbing;
    private bool _suppressVolume;
    private bool _volumeDirty;
    private DispatcherQueueTimer? _volumeSave;

    /// <summary>The side panel in the expanded card: <c>"lyrics"</c>, <c>"queue"</c>, or null.</summary>
    private string? _panel;

    /// <summary>Whether the card has grown past its compact height.</summary>
    private bool _expanded;

    /// <summary>The compact track row's measured height. The collapsed card leaves a few
    /// pixels of slack around the cover; the expanded card freezes this height so the cover
    /// and the title do not move when the panel opens.</summary>
    private double _trackHeight;

    /// <summary>Raised when the card should grow for a side panel, or shrink back. The
    /// window owns the size; the page only decides what is shown in it.</summary>
    public event Action<bool>? ExpandedChanged;

    /// <summary>Raised when the listener asks for the full player. The argument is the
    /// panel to open there: <c>"lyrics"</c>, <c>"queue"</c>, or null for the player itself.</summary>
    public event Action<string?>? FullPlayerRequested;

    /// <summary>The panel the expanded card is showing, for the full-player handoff.</summary>
    public string? OpenPanel => _panel;

    /// <summary>The title bar the window hands to Windows as its drag region.</summary>
    public TitleBar TitleBarControl => MiniTitleBar;

    private PlaybackService Playback => PlaybackService.Instance;

    public MiniPlayerPage()
    {
        InitializeComponent();
        Attach();

        // The panel bodies normally draw the main window's drawer chrome: a surface and a
        // header of their own. In the card that chrome is the card's, so they give it up.
        LyricsPane.UseCompactChrome();
        QueuePane.UseCompactChrome();
        LyricsPane.CloseRequested += OnPanelCloseRequested;
        QueuePane.CloseRequested += OnPanelCloseRequested;

        // Esc puts the panel away first; Windows owns the way out of the overlay itself.
        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            if (_expanded)
            {
                Collapse();
                args.Handled = true;
            }
        };
        KeyboardAccelerators.Add(escape);

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

        LyricsPane.CloseRequested -= OnPanelCloseRequested;
        QueuePane.CloseRequested -= OnPanelCloseRequested;
        LyricsPane.Dispose();
        QueuePane.Deactivate();
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
        if (song is null && _expanded)
        {
            // The words and the queue have nothing to say without a song.
            Collapse();
        }

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

    private void OnLyricsClick(object sender, RoutedEventArgs e) => TogglePanel("lyrics");

    private void OnQueueClick(object sender, RoutedEventArgs e) => TogglePanel("queue");

    private void OnPanelCloseRequested() => Collapse();

    /// <summary>Opens one side panel in the taller card, swaps to the other, or closes the
    /// one already showing when its own button is pressed again.</summary>
    private void TogglePanel(string panel)
    {
        if (_panel == panel)
        {
            Collapse();
            return;
        }

        var grow = !_expanded;
        if (grow)
        {
            // Measure before the window grows: the collapsed star row is a few pixels taller
            // than the 80 DIP cover, and the expanded card has to keep that slack or the
            // cover and title jump up as the panel opens.
            _trackHeight = TrackRow.ActualHeight > 0 ? TrackRow.ActualHeight : TrackRow.MinHeight;
        }

        _expanded = true;
        // Set before asking the window to grow: if the shell refuses the taller overlay,
        // the window falls back to the full player and needs to know what to open there.
        _panel = panel;
        if (grow)
        {
            ExpandedChanged?.Invoke(true);
            if (!_attached)
            {
                return; // the overlay fell back to the full player and closed
            }
        }

        ApplyPanel();
    }

    /// <summary>Shrinks back to the compact card, keeping it where the listener left it.</summary>
    private void Collapse()
    {
        if (!_expanded)
        {
            return;
        }

        _panel = null;
        _expanded = false;
        // Hide the words before the window gets shorter, so nothing is squeezed.
        ApplyPanel();
        ExpandedChanged?.Invoke(false);
    }

    /// <summary>Puts the open panel in the card, or returns it to the compact rows.</summary>
    private void ApplyPanel()
    {
        var open = _panel is not null;

        TrackRow.Height = open ? new GridLength(_trackHeight) : new GridLength(1, GridUnitType.Star);
        PanelRow.Height = open ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PanelHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        var lyrics = _panel is "lyrics";
        var queue = _panel is "queue";
        LyricsPane.Visibility = lyrics ? Visibility.Visible : Visibility.Collapsed;
        QueuePane.Visibility = queue ? Visibility.Visible : Visibility.Collapsed;
        LyricsButton.IsChecked = lyrics;
        QueueButton.IsChecked = queue;
        SetPanelButtonLabel(LyricsButton, "lyrics", lyrics);
        SetPanelButtonLabel(QueueButton, "queue", queue);

        if (lyrics)
        {
            QueuePane.Deactivate();
            LyricsPane.Activate(animate: true);
        }
        else if (queue)
        {
            // Keep the words for a switch back; they are off screen meanwhile.
            LyricsPane.Suspend();
            QueuePane.Activate();
        }
        else
        {
            LyricsPane.Deactivate();
            QueuePane.Deactivate();
        }
    }

    private static void SetPanelButtonLabel(ToggleButton button, string panel, bool open)
    {
        var label = open ? $"Hide {panel}" : $"Show {panel}";
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e) => FullPlayerRequested?.Invoke(_panel);

    private void OnRestoreTapped(object sender, DoubleTappedRoutedEventArgs e) => FullPlayerRequested?.Invoke(_panel);

    /// <summary>The title bar's right header is measured after the bar itself, so its
    /// passthrough area must be recomputed once the loaded layout is complete.</summary>
    private void OnTitleBarLoaded(object sender, RoutedEventArgs e) => MiniTitleBar.RecomputeDragRegions();

    /// <summary>The caption palette belongs to the window; the app-drawn expand button
    /// follows the same colors as the native buttons beside it.</summary>
    public void SetCaptionButtonColors(Windows.UI.Color foreground, Windows.UI.Color hover, Windows.UI.Color pressed)
    {
        if (Resources["MiniCaptionHoverBrush"] is SolidColorBrush hoverBrush)
        {
            hoverBrush.Color = hover;
        }

        if (Resources["MiniCaptionPressedBrush"] is SolidColorBrush pressedBrush)
        {
            pressedBrush.Color = pressed;
        }

        RestoreButton.Foreground = new SolidColorBrush(foreground);
    }

    private static string FormatTime(double seconds)
    {
        var total = (long)Math.Floor(Math.Max(0, seconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:D2}:{secs:D2}" : $"{minutes}:{secs:D2}";
    }
}
