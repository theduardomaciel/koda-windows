using System;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Helpers;
using Koda.Pages;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Koda.Controls;

/// <summary>
/// The inline lyrics drawer's body: the same words and motion as Now Playing, hosted in
/// the main window. It owns its own <see cref="LyricsPanel"/> so leaving Now Playing for
/// the library does not move or rebuild the player's panel; only the visible panel paints.
/// </summary>
public sealed partial class InlineLyricsPane : UserControl, IDisposable
{
    private readonly LyricsPanel _lyrics;

    /// <summary>The song whose words were asked for last; guards a second request.</summary>
    private string? _requestedFor;

    /// <summary>The song whose words (or empty answer) are in the panel now.</summary>
    private string? _shownFor;

    private bool _active;
    private bool _masked;
    private bool _followPillShown;

    private PlaybackService Playback => PlaybackService.Instance;

    public event Action? CloseRequested;

    public InlineLyricsPane()
    {
        InitializeComponent();
        _lyrics = new LyricsPanel(LyricsScroll, LyricsLines, DispatcherQueue);
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

    /// <summary>Puts the words on screen and starts them following the song. Called when
    /// the drawer opens, and again when it is shown after Now Playing or a video.</summary>
    public void Activate()
    {
        if (_active)
        {
            return;
        }

        _active = true;
        Playback.TrackChanged += OnTrackChanged;
        Playback.PlayStateChanged += OnPlayStateChanged;

        // A song whose answer is already in the panel was left paused with its words
        // intact; PlayEntrance rebuilds the released rows and follows again.
        if (Playback.CurrentSong is { } song && _shownFor == song.Id)
        {
            _lyrics.Offset = App.Library.LyricsOffset(song.Id);
            _lyrics.PlayEntrance();
        }
        else
        {
            _ = LoadForAsync(Playback.CurrentSong);
        }

        _lyrics.SetPlaying(Playback.IsPlaying);
        _lyrics.SetPosition(Playback.PositionSeconds);
        _lyrics.Follow();
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
        Playback.TrackChanged -= OnTrackChanged;
        Playback.PlayStateChanged -= OnPlayStateChanged;
        _lyrics.Pause();
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
        // follow them now. Plain lyrics are a single block with no timing to follow.
        _lyrics.PlayEntrance();
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
