using System;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Helpers;
using Koda.Pages;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Koda.Controls;

/// <summary>
/// The inline lyrics drawer's body: the same words and motion as Now Playing, hosted in
/// the main window. It owns its own <see cref="LyricsPanel"/> so leaving Now Playing for
/// the library does not move or rebuild the player's panel; only the visible panel paints.
/// </summary>
public sealed partial class InlineLyricsPane : UserControl, IDisposable
{
    /// <summary>Whether the pane gives up the drawer chrome for a host that provides its
    /// own, like the expanded mini player. Set it in the host's markup; it is not expected
    /// to change after the pane is shown.</summary>
    public static readonly DependencyProperty CompactChromeProperty = DependencyProperty.Register(
        nameof(CompactChrome),
        typeof(bool),
        typeof(InlineLyricsPane),
        new PropertyMetadata(false, OnCompactChromeChanged));

    private readonly LyricsPanel _lyrics;

    /// <summary>The drawer chrome, captured before a host switches the pane to compact.</summary>
    private readonly Brush? _drawerBackground;
    private readonly Thickness _drawerBorder;
    private readonly GridLength _drawerHeaderHeight;
    private readonly Thickness _drawerScrollMargin;

    /// <summary>Lets the panel and spinner render one frame before the rows are built.</summary>
    private readonly DispatcherQueueTimer _activationTimer;

    /// <summary>The song whose words were asked for last; guards a second request.</summary>
    private string? _requestedFor;

    /// <summary>The song whose words (or empty answer) are in the panel now.</summary>
    private string? _shownFor;

    private bool _active;
    private bool _suspended;
    private bool _animateEntrance = true;
    private bool _masked;
    private bool _followPillShown;

    private PlaybackService Playback => PlaybackService.Instance;

    public event Action? CloseRequested;

    public InlineLyricsPane()
    {
        InitializeComponent();
        _drawerBackground = Surface.Background;
        _drawerBorder = Surface.BorderThickness;
        _drawerHeaderHeight = HeaderRow.Height;
        _drawerScrollMargin = LyricsScroll.Margin;

        _lyrics = new LyricsPanel(LyricsScroll, LyricsLines, DispatcherQueue);
        _activationTimer = DispatcherQueue.CreateTimer();
        _activationTimer.Interval = TimeSpan.FromMilliseconds(220);
        _activationTimer.IsRepeating = false;
        _activationTimer.Tick += (_, _) =>
        {
            _activationTimer.Stop();
            Begin(_animateEntrance);
        };
        _lyrics.Seek = seconds =>
        {
            Playback.SeekSeconds(seconds);
            // Tapping a line is a request to hear it: a paused song starts again.
            Playback.Play();
        };
        _lyrics.PositionSource = () => Playback.PositionSeconds;
        _lyrics.ScrolledAwayChanged = ShowFollowPill;

        // A touch drag marks the press as handled when the ScrollViewer starts panning,
        // so the plain event never arrives; the drawer must stop following either way.
        LyricsScroll.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => _lyrics.UserScrolled = true),
            handledEventsToo: true);
        LyricsScroll.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, _) => _lyrics.UserScrolled = true),
            handledEventsToo: true);
        LyricsScroll.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler((_, e) =>
            {
                if (e.Key is Windows.System.VirtualKey.Up
                    or Windows.System.VirtualKey.Down
                    or Windows.System.VirtualKey.PageUp
                    or Windows.System.VirtualKey.PageDown
                    or Windows.System.VirtualKey.Home
                    or Windows.System.VirtualKey.End)
                {
                    _lyrics.UserScrolled = true;
                }
            }),
            handledEventsToo: true);

        // The follow pill's chevron points at the line being sung; keep it true while
        // the panel scrolls, including with the wheel or the scrollbar.
        LyricsScroll.ViewChanged += (_, _) =>
        {
            if (_followPillShown)
            {
                UpdateFollowArrow();
            }
        };

        Loaded += (_, _) => LyricsFade.Apply(LyricsView, LyricsMaskedContent, LyricsMask, ref _masked);
    }

    /// <summary>The drawer's chrome is the main window's; the expanded mini player provides
    /// its own and sets this in the host markup.</summary>
    public bool CompactChrome
    {
        get => (bool)GetValue(CompactChromeProperty);
        set => SetValue(CompactChromeProperty, value);
    }

    private static void OnCompactChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((InlineLyricsPane)d).ApplyChrome((bool)e.NewValue);

    /// <summary>Drops the drawer's own chrome for a host that provides it: no side divider,
    /// no surface of its own, and no header, so the words fill the whole panel. The expanded
    /// mini player hosts the body under its own track strip and controls.</summary>
    private void ApplyChrome(bool compact)
    {
        Surface.Background = compact ? null : _drawerBackground;
        Surface.BorderThickness = compact ? new Thickness(0) : _drawerBorder;
        HeaderRow.Height = compact ? new GridLength(0) : _drawerHeaderHeight;
        Header.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        // The card's own padding is the gutter: the words line up with the cover above.
        LyricsScroll.Margin = compact ? new Thickness(0) : _drawerScrollMargin;
    }

    /// <summary>Puts the words on screen and starts them following the song. Called when
    /// the drawer opens, and again when it is shown after Now Playing or a video.</summary>
    public void Activate(bool animate = true)
    {
        if (_active)
        {
            if (_suspended)
            {
                Resume(animate);
            }

            return;
        }

        _active = true;
        _suspended = false;
        _animateEntrance = animate;
        Playback.TrackChanged += OnTrackChanged;
        Playback.PlayStateChanged += OnPlayStateChanged;

        // Show the panel and its spinner now, then build the text surfaces after the
        // switch has painted. The switch is immediate; the heavy rows load behind it.
        Preparing.Visibility = Visibility.Visible;
        _activationTimer.Stop();
        _activationTimer.Start();
    }

    private void Begin(bool animate)
    {
        if (!_active || _suspended)
        {
            Preparing.Visibility = Visibility.Collapsed;
            return;
        }

        // A song whose answer is already in the panel was left paused with its words
        // intact; PlayEntrance rebuilds the released rows and follows again.
        if (Playback.CurrentSong is { } song && _shownFor == song.Id)
        {
            _lyrics.Offset = App.Library.LyricsOffset(song.Id);
            _lyrics.PlayEntrance(animate);
        }
        else
        {
            _ = LoadForAsync(Playback.CurrentSong);
        }

        _lyrics.SetPlaying(Playback.IsPlaying);
        _lyrics.SetPosition(Playback.PositionSeconds);
        _lyrics.Follow();
        Preparing.Visibility = Visibility.Collapsed;
    }

    /// <summary>Brings back a panel that was only swapped behind the other side panel.
    /// Its rows were kept, so this does not rebuild them.</summary>
    public void Resume(bool animate)
    {
        if (!_active)
        {
            Activate(animate);
            return;
        }

        if (!_suspended)
        {
            return;
        }

        _suspended = false;
        _animateEntrance = animate;
        if (Playback.CurrentSong is { } song && _shownFor == song.Id && _lyrics.HasRows)
        {
            Preparing.Visibility = Visibility.Collapsed;
            _lyrics.Offset = App.Library.LyricsOffset(song.Id);
            _lyrics.PlayEntrance(animate);
            _lyrics.SetPlaying(Playback.IsPlaying);
            _lyrics.SetPosition(Playback.PositionSeconds);
            _lyrics.Follow();
            return;
        }

        // The song changed while the panel was behind the queue, or the rows were
        // released: show the spinner and build after the switch has painted.
        Preparing.Visibility = Visibility.Visible;
        _activationTimer.Stop();
        _activationTimer.Start();
    }

    /// <summary>Swapped behind the queue while the drawer stays open: stop following and
    /// painting, but keep the rows so coming back is instant.</summary>
    public void Suspend()
    {
        if (!_active)
        {
            return;
        }

        _suspended = true;
        _activationTimer.Stop();
        _lyrics.Suspend();
        Preparing.Visibility = Visibility.Collapsed;
        ShowFollowPill(false);
    }

    /// <summary>The drawer closed: it stops reading the song and releases the text
    /// surfaces. The words stay for when it opens again.</summary>
    public void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _suspended = false;
        _activationTimer.Stop();
        Playback.TrackChanged -= OnTrackChanged;
        Playback.PlayStateChanged -= OnPlayStateChanged;
        _lyrics.Pause();
        Preparing.Visibility = Visibility.Collapsed;
        ShowFollowPill(false);
        // A request that never landed is asked again the next time the drawer opens.
        if (_requestedFor != _shownFor)
        {
            _requestedFor = null;
        }
    }

    /// <summary>The words for the song, from the same service Now Playing uses. A late
    /// answer for a song that is gone, or for a drawer that has closed, is dropped.</summary>
    private async Task LoadForAsync(Song? song)
    {
        if (song is null)
        {
            _requestedFor = _shownFor = null;
            _lyrics.Load(null);
            Preparing.Visibility = Visibility.Collapsed;
            LyricsLoading.Visibility = Visibility.Collapsed;
            LyricsEmpty.Text = "Nothing playing.";
            LyricsEmpty.Visibility = Visibility.Visible;
            return;
        }

        if (_requestedFor == song.Id)
        {
            return;
        }

        _requestedFor = song.Id;
        LyricsEmpty.Visibility = Visibility.Collapsed;
        LyricsLoading.Visibility = Visibility.Visible;
        _lyrics.Load(null);

        Lyrics? lyrics;
        try
        {
            lyrics = await LyricsService.For(song);
        }
        catch (Exception)
        {
            lyrics = null;
        }

        if (!_active || _requestedFor != song.Id)
        {
            return;
        }

        LyricsLoading.Visibility = Visibility.Collapsed;
        Preparing.Visibility = Visibility.Collapsed;
        _lyrics.Load(lyrics);
        _lyrics.Offset = App.Library.LyricsOffset(song.Id);
        _shownFor = song.Id;

        if (lyrics is null)
        {
            LyricsEmpty.Text = "No lyrics for this song.";
            LyricsEmpty.Visibility = Visibility.Visible;
            return;
        }

        // Timed or plain, the words arrived while the drawer was on screen: show and
        // follow them now. A suspended panel keeps them for Resume.
        if (_suspended)
        {
            return;
        }

        _lyrics.PlayEntrance(_animateEntrance);
        _lyrics.SetPosition(Playback.PositionSeconds);
        _lyrics.Follow();
    }

    private void OnTrackChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_active)
        {
            _ = LoadForAsync(Playback.CurrentSong);
        }
    });

    private void OnPlayStateChanged() => DispatcherQueue.TryEnqueue(() => _lyrics.SetPlaying(Playback.IsPlaying));

    private void ShowFollowPill(bool show)
    {
        _followPillShown = show;
        FollowLyricsButton.IsHitTestVisible = show;
        FollowLyricsButton.IsTabStop = show;
        FollowLyricsButton.Opacity = show ? 1 : 0;
        if (show)
        {
            UpdateFollowArrow();
        }
    }

    private void UpdateFollowArrow() =>
        FollowLyricsChevron.Glyph = _lyrics.CurrentLineIsAbove ? "\uE70E" : "\uE70D";

    private void OnFollowLyricsClick(object sender, RoutedEventArgs e) => _lyrics.ResumeFollowing();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    public void Dispose()
    {
        Deactivate();
        _lyrics.Dispose();
    }
}
