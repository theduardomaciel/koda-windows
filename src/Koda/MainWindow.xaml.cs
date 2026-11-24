using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Koda.Helpers;
using Koda.Interop;
using Koda.Pages;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Media;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;
using MusicItem = Koda.Core.MusicItem;
using SearchSuggestions = Koda.Core.SearchSuggestions;
using SuggestionRanking = Koda.Core.SuggestionRanking;
using YouTubeLink = Koda.Core.YouTubeLink;

namespace Koda;

public sealed partial class MainWindow : Window
{
    private static MainWindow? _current;

    private const int MinWindowDipWidth = 700;
    private const int MinWindowDipHeight = 520;

    /// <summary>The steps Ctrl + and Ctrl - walk through; 1 is 100%. The bottom of the
    /// range is for very large displays, where the window has room for a lot more.</summary>
    private static readonly double[] ZoomSteps = [0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5];

    /// <summary>The title bar is the tall Windows height (48): it holds the search field and
    /// the account chip with room above and below, while the caption buttons stay native.</summary>
    private const double TitleBarHeight = 48;

    private TrayIconHost? _tray;
    private Taskbar? _taskbar;
    private MiniPlayerWindow? _mini;
    /// <summary>Whether the drawer's lyrics pane was suspended because the shell window
    /// was hidden or minimized, so showing the window resumes exactly what it paused.</summary>
    private bool _lyricsSuspendedForShell;
    private readonly SmtcController _smtc = new();
    private readonly Windows.UI.ViewManagement.UISettings _systemColors = new();
    private nint _previousWndProc;
    private bool _placementApplied;
    private bool _scrubbing;
    private bool _suppressVolumePersistence;
    private bool _suppressBarVolume;
    private string? _currentTag;
    private string? _notifiedSongId;
    private string _playlistSignature = "";
    private bool _hasUpNext;
    private readonly List<NavigationViewItem> _playlistItems = [];
    private string _videoPlaylistSignature = "";
    private readonly List<NavigationViewItem> _videoPlaylistItems = [];
    private DispatcherQueueTimer? _volumeSaveTimer;
    private DispatcherQueueTimer? _statusTimer;
    private DispatcherQueueTimer? _suggestTimer;
    private CancellationTokenSource? _suggestCancel;
    private DispatcherQueueTimer? _zoomTimer;
    private double _zoom = 1;
    private bool _shutdown;

    /// <summary>Whether the app folded the sidebar because the window is compact; widening
    /// may unfold it again unless the listener has made a choice since.</summary>
    private bool _sidebarCollapsedForWidth;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
    private static readonly WndProc SubclassedProc = OnWndProc;

    private PlaybackService Playback => PlaybackService.Instance;

    private VideoPlaybackService Video => VideoPlaybackService.Instance;

    private bool _videoScrubbing;
    private bool _videoFullScreen;

    /// <summary>Whether the sidebar was wide or collapsed to icons when it was last hidden.</summary>
    private bool _paneWasOpen = true;

    /// <summary>The sidebar collapsed to icons, as MainWindow.xaml sets it.</summary>
    private const double CompactPaneWidth = 52;

    /// <summary>The width the page keeps beside an inline lyrics drawer; below it the
    /// drawer floats above the page instead of squeezing it.</summary>
    private const double InlinePanelPageWidth = 520;

    /// <summary>Whether the listener asked for the inline lyrics drawer. A whole-window
    /// player hides the drawer without forgetting the choice; leaving restores it.</summary>
    private bool _lyricsOpen;

    /// <summary>The queue drawer's open state, kept across pages like the lyrics'.</summary>
    private bool _queueOpen;

    /// <summary>The crossfade between the two panels while the drawer stays open.</summary>
    private Storyboard? _panelCrossfade;
    private int _panelCrossfadeVersion;

    /// <summary>The cover's hover scrim fade; stopping it between fades starts the next
    /// one from the opacity on screen.</summary>
    private Storyboard? _coverHoverFade;

    /// <summary>Set while the drawer is closed on purpose, so the pane's light-dismiss
    /// guard knows this close is not a tap outside.</summary>
    private bool _closingSidePanel;

    /// <summary>Below this width the search takes its smallest step and the sidebar folds
    /// to its icons; above it both open up again.</summary>
    private const double NarrowWindowWidth = 860;

    /// <summary>Below this width the account chip drops the name and keeps only the avatar.
    /// Measured in DIPs on the title bar itself.</summary>
    private const double ProfileNameMinWidth = 980;

    /// <summary>The visual nudge that balances the search against the full profile chip.
    /// It ramps in with the extra room the full profile layout provides, so narrowing the
    /// window closes the gap instead of pushing the search into the chip.</summary>
    private const double TitleSearchNudge = 128;

    public MainWindow()
    {
        InitializeComponent();
        _current = this;

        Title = "Koda";
        SystemBackdrop = new MicaBackdrop();

        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(1180, 780));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        // The title bar is ours: Mica runs to the top edge, caption buttons float on it.
        // Windows' tall height leaves room for the search field and the account chip;
        // the bar's content shrinks with the UI zoom.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // WinUI's TitleBar misreads the caption inset at non-100% DPI; see TitleBarInsets.
        TitleBarInsets.Repair(AppTitleBar, this);
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Root.RowDefinitions[0].Height = new GridLength(TitleBarHeight);
        Root.ActualThemeChanged += (_, _) => PaintCaptionButtons();
        PaintCaptionButtons();
        AppTitleBar.SizeChanged += OnTitleBarSizeChanged;
        ApplyTitleBarChrome();
        Activated += OnActivated;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowPlacement.Restore(hwnd);
        _previousWndProc = Win32.SetWindowLongPtr(
            hwnd, Win32.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(SubclassedProc));

        _smtc.Bind(hwnd, OnSmtcButton);
        _taskbar = new Taskbar(hwnd,
            () => _ = Playback.PreviousAsync(),
            Playback.Toggle,
            () => _ = Playback.NextAsync());

        _tray = new TrayIconHost(
            hwnd,
            Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"),
            () => Playback.IsPlaying ? "Pause" : "Play",
            OnTrayCommand);

        // The taskbar's Jump List: the transport without opening the window.
        JumpList.Apply();

        var dispatch = DispatcherQueue.GetForCurrentThread();
        // Energy Saver quietly stills the decorative motion; the pages that move follow it.
        EnergySaver.Watch(dispatch);
        Playback.Connect(dispatch, _smtc);
        Playback.TrackChanged += () => dispatch.TryEnqueue(OnTrackChanged);
        Playback.QueueChanged += () => dispatch.TryEnqueue(OnQueueModesChanged);
        Playback.PlayStateChanged += () => dispatch.TryEnqueue(OnPlayStateChanged);
        Playback.PositionTick += () => dispatch.TryEnqueue(OnPositionTick);
        Playback.StatusRaised += (tone, message) => dispatch.TryEnqueue(() => OnStatusRaised(tone, message));
        Video.Connect(dispatch);
        Video.StartTimers();
        Video.Changed += () => dispatch.TryEnqueue(UpdateVideoBar);
        Video.Tick += () => dispatch.TryEnqueue(UpdateVideoBarTime);
        Scrubbing.Attach(VideoScrubber, on => _videoScrubbing = on, Video.Seek, () => Video.HasVideo);
        App.Account.Changed += () => dispatch.TryEnqueue(UpdateVideoSidebar);
        App.Settings.Changed += _ => dispatch.TryEnqueue(UpdateVideoSidebar);
        UpdateVideoSidebar();
        Koda.Services.Profiles.Roster.Changed += () => dispatch.TryEnqueue(UpdateProfileChip);
        UpdateProfileChip();
        App.Settings.Changed += _ => dispatch.TryEnqueue(UpdateSidebarButton);
        UpdateSidebarButton();

        // The taskbar's flyout follows Windows, not the app theme; rebuild its
        // icons when the system light/dark switch moves.
        _systemColors.ColorValuesChanged += (_, _) => dispatch.TryEnqueue(() => _taskbar?.RefreshTheme());
        App.Library.Changed += () => dispatch.TryEnqueue(RefreshSidebar);
        App.Library.Changed += () => dispatch.TryEnqueue(UpdateVideoSidebar);
        App.AudioStore.Changed += () => dispatch.TryEnqueue(RefreshSidebar);
        App.Library.Changed += () => dispatch.TryEnqueue(UpdateLike);
        App.Settings.Changed += _ => dispatch.TryEnqueue(ApplySkipSettings);
        App.Settings.Changed += _ => dispatch.TryEnqueue(RefreshNextSong);

        // A toast clicked while Koda was closed waited for the playback service and the
        // bar's events; now its action can run against a working app.
        Notifications.Attach(this);

        Scrubbing.Attach(Scrubber, on => _scrubbing = on, Playback.SeekSeconds, () => Playback.CurrentSong is not null);
        Scrubber.ValueChanged += (_, _) =>
        {
            if (_scrubbing) ElapsedText.Text = FormatTime(Scrubber.Value);
        };

        _suppressVolumePersistence = true;
        VolumeSlider.Value = App.Volume;
        _suppressVolumePersistence = false;

        _volumeSaveTimer = dispatch.CreateTimer();
        _volumeSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _volumeSaveTimer.IsRepeating = false;
        _volumeSaveTimer.Tick += (_, _) => App.SetVolume(VolumeSlider.Value);

        _statusTimer = dispatch.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(6);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (_, _) => StatusRibbon.IsOpen = false;

        _suggestTimer = dispatch.CreateTimer();
        _suggestTimer.Interval = TimeSpan.FromMilliseconds(220);
        _suggestTimer.IsRepeating = false;
        _suggestTimer.Tick += (_, _) => _ = LoadSuggestionsAsync(TitleSearch.Text);

        _zoomTimer = dispatch.CreateTimer();
        _zoomTimer.Interval = TimeSpan.FromSeconds(1.4);
        _zoomTimer.IsRepeating = false;
        _zoomTimer.Tick += (_, _) => ZoomBadge.Visibility = Visibility.Collapsed;

        ZoomHost.SizeChanged += (_, _) => ApplyZoom();
        SidePanel.SizeChanged += (_, _) => ApplySidePanelLayout();
        Root.Loaded += (_, _) =>
        {
            if (Root.XamlRoot is { } root)
            {
                root.Changed -= OnRootChanged;
                root.Changed += OnRootChanged;
            }
        };
        RestoreZoom();

        AddShortcuts();
        ApplySkipSettings();
        ApplySidebar();

        ContentFrame.Navigated += OnContentNavigated;
        // The pane's width is part of the content's room: after it opens or closes, measure
        // the page again. Without this a page laid out at the old width stays cropped.
        Nav.PaneOpened += (_, _) =>
        {
            RelayoutContent();
            ApplySidePanelLayout();
        };
        Nav.PaneClosed += (_, _) =>
        {
            RelayoutContent();
            ApplySidePanelLayout();
        };
        Nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
        {
            // Opening the pane while the window is narrow is the listener's choice;
            // widening must not open it on top of that.
            if (_sidebarCollapsedForWidth && Nav.IsPaneOpen)
            {
                _sidebarCollapsedForWidth = false;
            }

            UpdatePaneChrome();
        });
        NavigateRoot(typeof(HomePage), null, "home");
        RefreshSidebar();

        OnTrackChanged();
        OnPlayStateChanged();
        ApplySidePanelLayout();

        Closed += (_, _) =>
        {
            // A WinUI app keeps its message loop (and its XAML heap) alive after the last
            // window closes; Application.Exit alone still left a windowless process behind,
            // growing by 10-24 MB a minute. Closing the window is the listener quitting:
            // clean up once, then end the process.
            try
            {
                InlineLyrics.Dispose();
                Shutdown();
            }
            catch (Exception error)
            {
                App.WriteError("closing Koda", error);
            }
            finally
            {
                Environment.Exit(0);
            }
        };
    }

    /// <summary>The window handle, for the shell integrations that need one.</summary>
    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    // MARK: title bar

    private void PaintCaptionButtons()
    {
        if (AppWindow?.TitleBar is not { } bar)
        {
            return;
        }

        var dark = CaptionChrome.IsDark(Root.ActualTheme);
        var foreground = CaptionChrome.Foreground(dark);
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = CaptionChrome.InactiveForeground(dark);
        bar.ButtonHoverBackgroundColor = CaptionChrome.HoverBackground(dark);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = CaptionChrome.PressedBackground(dark);
        bar.ButtonPressedForegroundColor = foreground;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // SetWindowPlacement remembers the normal rectangle before the window is shown,
        // but WinUI activates it normal; the saved maximised state is applied once the
        // window is really on screen, so tearing it down starts from the same place.
        if (!_placementApplied)
        {
            _placementApplied = true;
            if (App.Settings.WindowMaximized)
            {
                (AppWindow.Presenter as OverlappedPresenter)?.Maximize();
            }
        }
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs args) => ApplyTitleBarChrome();

    /// <summary>The sidebar opening or closing changes the room the page has; measure the
    /// page and its lyrics again at that room, rather than leaving them at the old width.</summary>
    private void RelayoutContent()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ZoomContent.InvalidateMeasure();
            ZoomContent.InvalidateArrange();
            Root.InvalidateMeasure();
            Root.InvalidateArrange();
            Root.UpdateLayout();
            ContentFrame.UpdateLayout();
            if (ContentFrame.Content is NowPlayingPage page)
            {
                page.Relayout();
            }
        });
    }

    /// <summary>A display scale change can leave the zoomed content at its old DIP size,
    /// which clips every page. Lay the root out at the new size, then re-apply the zoom.</summary>
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            Root.UpdateLayout();
            ApplyZoom();
        });

    /// <summary>The bar's own responsive step: the account name and the search field give way
    /// as the window narrows, before anything overlaps. The TitleBar control owns the caption
    /// inset and drag regions; ask it to re-read them after these content changes.</summary>
    private void ApplyTitleBarChrome()
    {
        var width = AppTitleBar.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var fullProfile = width >= ProfileNameMinWidth;
        TitleProfileName.Visibility = fullProfile ? Visibility.Visible : Visibility.Collapsed;
        // The content column gains a pixel on each side for every two the window gains,
        // so spend at most half of that on the nudge and keep the rest as the chip's gap.
        TitleSearchOffset.X = Math.Clamp((width - ProfileNameMinWidth) / 2, 0, TitleSearchNudge);
        TitleSearch.Width = width >= 1040 ? 420 : width >= NarrowWindowWidth ? 340 : 240;
        CollapseSidebarForWidth(width);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => AppTitleBar.RecomputeDragRegions());
    }

    /// <summary>The sidebar folds to its icons when the window gets narrow and unfolds
    /// when it has room again. Opening it while narrow counts as a choice, so widening
    /// then leaves it as the listener left it.</summary>
    private void CollapseSidebarForWidth(double width)
    {
        if (width < NarrowWindowWidth)
        {
            if (!_sidebarCollapsedForWidth && Nav.IsPaneVisible && Nav.IsPaneOpen)
            {
                _sidebarCollapsedForWidth = true;
                Nav.IsPaneOpen = false;
            }
        }
        else if (_sidebarCollapsedForWidth)
        {
            _sidebarCollapsedForWidth = false;
            if (Nav.IsPaneVisible)
            {
                Nav.IsPaneOpen = true;
            }
        }
    }

    // MARK: navigation

    public void NavigateTo(Type page, object? parameter = null)
    {
        if (page == typeof(SearchPage) && ContentFrame.Content is SearchPage open && parameter is string query)
        {
            open.Search(query);
            return;
        }

        try
        {
            ContentFrame.Navigate(page, parameter, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
        }
        catch (Exception error)
        {
            App.WriteError($"Could not open {page.Name}", error);
            Note($"{page.Name} couldn't open. The reason was written to crash.txt.");
            if (_currentTag is { } current)
            {
                SelectTag(current);
            }
        }
    }

    /// <summary>A sidebar section's own page: what is behind it is dropped, so Back stops here.</summary>
    /// <param name="fresh">Open it again even if it's already showing: a new profile's Home.</param>
    public void NavigateRoot(Type page, object? parameter = null, string? tag = null, bool fresh = false)
    {
        if (!fresh && tag is not null && tag == _currentTag && !ContentFrame.CanGoBack && ContentFrame.CurrentSourcePageType == page)
        {
            return;
        }

        try
        {
            ContentFrame.Navigate(page, parameter, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
            ContentFrame.BackStack.Clear();
            ContentFrame.ForwardStack.Clear();
            // Navigated is raised during Navigate, before the stack is cleared: without this
            // the button stays lit on a section's first page with nowhere to go.
            UpdateBackButton();
        }
        catch (Exception error)
        {
            App.WriteError($"Could not open {page.Name}", error);
            Note($"{page.Name} couldn't open. The reason was written to crash.txt.");
            if (_currentTag is { } current)
            {
                SelectTag(current);
            }
        }
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            NavigateRoot(typeof(SettingsPage), null, "settings");
            return;
        }

        var tag = (args.InvokedItemContainer as NavigationViewItem)?.Tag as string;
        switch (tag)
        {
            case "home":
                NavigateRoot(typeof(HomePage), null, tag);
                break;
            case "browse":
                NavigateRoot(typeof(BrowsePage), null, tag);
                break;
            case "search":
                ShowSearch();
                break;
            case "recent":
            case "liked":
            case "downloads":
            case "offline":
                NavigateRoot(typeof(SongsPage), tag, tag);
                break;
            case "history":
                NavigateRoot(typeof(HistoryPage), null, tag);
                break;
            case "albums":
            case "playlists":
            case "artists":
                NavigateRoot(typeof(LibraryGridPage), tag, tag);
                break;
            case "stats":
                NavigateRoot(typeof(StatsPage), null, tag);
                break;
            case "glance":
                NavigateRoot(typeof(StatsPage), null, "stats");
                break;
            case "lastfm":
                NavigateRoot(typeof(LastFmPage), null, tag);
                break;
            case "up-next":
                ShowQueue();
                break;
            case "video-home":
                NavigateRoot(typeof(VideoListPage), new VideoListRequest(VideoListKind.Home), tag);
                break;
            case "video-subscriptions":
                NavigateRoot(typeof(VideoListPage), new VideoListRequest(VideoListKind.Subscriptions), tag);
                break;
            case "video-explore":
                NavigateRoot(typeof(VideoExplorePage), null, tag);
                break;
            case "video-channels":
                NavigateRoot(typeof(VideoListPage), new VideoListRequest(VideoListKind.Channels), tag);
                break;
            case "video-later":
                NavigateRoot(typeof(VideoListPage), new VideoListRequest(VideoListKind.WatchLater), tag);
                break;
            case "video-liked":
                NavigateRoot(typeof(VideoListPage), new VideoListRequest(VideoListKind.Liked), tag);
                break;
            case "video-history":
                NavigateRoot(typeof(VideoListPage), new VideoListRequest(VideoListKind.History), tag);
                break;
            case "new-playlist":
                _ = Dialogs.CreatePlaylistAsync([]);
                break;
            case not null when tag.StartsWith("video-playlist:", StringComparison.Ordinal)
                && Guid.TryParse(tag["video-playlist:".Length..], out var videoPlaylist):
                NavigateRoot(typeof(VideoListPage), new VideoListRequest(VideoListKind.Playlist, Playlist: videoPlaylist), tag);
                break;
            case not null when tag.StartsWith("playlist:", StringComparison.Ordinal)
                                && Guid.TryParse(tag["playlist:".Length..], out var id):
                NavigateRoot(typeof(UserPlaylistPage), id, tag);
                break;
        }
    }

    /// <summary>Search results is a place only once there is a search; the sidebar gains it then.</summary>
    public void SearchFor(string query)
    {
        query = query.Trim();
        SearchResultsItem.Visibility = Visibility.Visible;
        TitleSearch.Text = query;
        if (ContentFrame.Content is SearchPage open)
        {
            if (query.Length > 0)
            {
                open.Search(query);
            }

            SelectTag("search");
        }
        else
        {
            NavigateRoot(typeof(SearchPage), query.Length > 0 ? query : null, "search");
        }

        TitleSearch.Focus(FocusState.Programmatic);
    }

    private void ShowSearch()
    {
        var query = TitleSearch.Text.Trim();
        if (query.Length > 0)
        {
            SearchFor(query);
            return;
        }

        SearchResultsItem.Visibility = Visibility.Visible;
        NavigateRoot(typeof(SearchPage), null, "search");
        TitleSearch.Focus(FocusState.Programmatic);
    }

    private void OnBackRequested(TitleBar sender, object args) => GoBack();

    private void GoBack()
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    /// <summary>The built-in back button appears while there is somewhere to go.</summary>
    private void UpdateBackButton()
    {
        var canGoBack = ContentFrame.CanGoBack;
        if (AppTitleBar.IsBackButtonVisible == canGoBack
            && AppTitleBar.IsBackButtonEnabled == canGoBack)
        {
            return;
        }

        AppTitleBar.IsBackButtonVisible = canGoBack;
        AppTitleBar.IsBackButtonEnabled = canGoBack;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => AppTitleBar.RecomputeDragRegions());
    }

    /// <summary>The upstream sidebar button is optional; the pane's own toggle and Ctrl+B
    /// are always there.</summary>
    private void UpdateSidebarButton()
    {
        var visible = App.Settings.ShowsTitleBarSidebarToggle;
        if (AppTitleBar.IsPaneToggleButtonVisible == visible)
        {
            return;
        }

        AppTitleBar.IsPaneToggleButtonVisible = visible;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => AppTitleBar.RecomputeDragRegions());
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) => ToggleSidebar();

    private void OnContentNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs args)
    {
        UpdateBackButton();
        // Now Playing and the watch page are whole players: the bars step aside while they're open.
        UpdateVideoBar();
        ApplySidePanel();
        // The frame can still report the outgoing whole-window player for a tick; ask
        // again once it has settled, so the drawer comes back after Now Playing.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplySidePanel);
        TitleSearch.PlaceholderText = args.Content is IVideoPage ? "Search YouTube" : "Songs, albums, artists or a link";
        if (args.SourcePageType == typeof(SearchPage))
        {
            SearchResultsItem.Visibility = Visibility.Visible;
        }

        // The pane shows the section the page belongs to; detail pages keep their section lit.
        _currentTag = TagFor(args.SourcePageType, args.Parameter);
        if (_currentTag is null)
        {
            return;
        }

        if (_currentTag == "settings")
        {
            Nav.SelectedItem = Nav.SettingsItem;
        }
        else
        {
            SelectTag(_currentTag);
        }
    }

    private static string? TagFor(Type page, object? parameter) => page.Name switch
    {
        nameof(HomePage) => "home",
        nameof(BrowsePage) or nameof(MoodPage) => "browse",
        nameof(SearchPage) => "search",
        nameof(HistoryPage) => "history",
        nameof(StatsPage) => "stats",
        nameof(LastFmPage) => "lastfm",
        nameof(SongsPage) => (parameter as string) switch
        {
            "recent" => "recent",
            "downloads" => "downloads",
            "offline" => "offline",
            // The mix is a generated playlist, not the listener's liked songs.
            "mix" => "playlists",
            _ => "liked",
        },
        nameof(LibraryGridPage) => (parameter as string) switch
        {
            "playlists" => "playlists",
            "artists" => "artists",
            _ => "albums",
        },
        nameof(UserPlaylistPage) => parameter is Guid id ? $"playlist:{id}" : null,
        nameof(AlbumPage) => "albums",
        nameof(PlaylistPage) => "playlists",
        nameof(ArtistPage) => "artists",
        nameof(SettingsPage) => "settings",
        nameof(VideoExplorePage) => "video-explore",
        nameof(VideoListPage) => (parameter as VideoListRequest)?.Kind switch
        {
            VideoListKind.Subscriptions => "video-subscriptions",
            VideoListKind.Channels => "video-channels",
            VideoListKind.WatchLater => "video-later",
            VideoListKind.Liked => "video-liked",
            VideoListKind.History => "video-history",
            VideoListKind.Playlist => $"video-playlist:{(parameter as VideoListRequest)?.Playlist}",
            _ => "video-home",
        },
        _ => null,
    };

    private void SelectTag(string tag)
    {
        if (tag == "settings")
        {
            Nav.SelectedItem = Nav.SettingsItem;
            return;
        }

        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag as string == tag);
        if (item is not null)
        {
            Nav.SelectedItem = item;
        }
    }

    // MARK: sidebar counts and foot

    private void RefreshSidebar()
    {
        SetBadge(LikedItem, App.Library.Liked.Length);
        SetBadge(DownloadsItem, App.AudioStore.Downloads.Length);
        RebuildPlaylists();
        UpdateGlance();
        UpdateUpNext();
    }

    private static void SetBadge(NavigationViewItem item, int count)
    {
        if (count <= 0)
        {
            item.InfoBadge = null;
        }
        else if (item.InfoBadge is InfoBadge badge)
        {
            badge.Value = count;
        }
        else
        {
            item.InfoBadge = new InfoBadge { Value = count };
        }
    }

    /// <summary>The listener's playlists in the pane, between their header and New Playlist.</summary>
    private void RebuildPlaylists()
    {
        var playlists = App.Library.UserPlaylists;
        var signature = string.Join("|", playlists.Select(p => $"{p.Id}:{p.Name}:{p.Songs.Length}:{p.Modified.UtcTicks}"));
        if (signature == _playlistSignature)
        {
            return;
        }

        _playlistSignature = signature;
        foreach (var old in _playlistItems)
        {
            Nav.MenuItems.Remove(old);
        }

        _playlistItems.Clear();
        var at = Nav.MenuItems.IndexOf(NewPlaylistItem);
        if (at < 0)
        {
            at = Nav.MenuItems.Count;
        }

        foreach (var playlist in playlists)
        {
            var item = BuildPlaylistItem(playlist);
            Nav.MenuItems.Insert(at++, item);
            _playlistItems.Add(item);
        }

        // A new playlist from a song menu, or a rename: the pane follows the selection.
        if (_currentTag is { } tag && tag.StartsWith("playlist:", StringComparison.Ordinal))
        {
            SelectTag(tag);
        }
    }

    private NavigationViewItem BuildPlaylistItem(Koda.Core.UserPlaylist playlist)
    {
        var item = new NavigationViewItem
        {
            Content = playlist.Name,
            Tag = $"playlist:{playlist.Id}",
            Icon = new FontIcon { Glyph = "\uE8FD" },
        };

        var menu = new MenuFlyout();
        var play = SongMenu.Item("Play", "\uE768", () =>
        {
            if (playlist.Songs.Length > 0)
            {
                _ = Playback.PlayNowAsync(playlist.Songs[0], playlist.Songs);
            }
        });
        play.IsEnabled = playlist.Songs.Length > 0;
        menu.Items.Add(play);
        var queue = SongMenu.Item("Add to queue", "\uE8FD", () => Playback.AddToQueue(playlist.Songs));
        queue.IsEnabled = playlist.Songs.Length > 0;
        menu.Items.Add(queue);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(SongMenu.Item("Rename…", "\uE70F", () => _ = Dialogs.RenamePlaylistAsync(playlist)));
        menu.Items.Add(SongMenu.Item("Delete playlist…", "\uE74D", () => _ = DeletePlaylistAsync(playlist)));
        item.ContextFlyout = menu;
        return item;
    }

    private async System.Threading.Tasks.Task DeletePlaylistAsync(Koda.Core.UserPlaylist playlist)
    {
        if (!await Dialogs.ConfirmAsync($"Delete “{playlist.Name}”?",
                "The songs stay wherever else you saved them.", "Delete Playlist"))
        {
            return;
        }

        if (_currentTag == $"playlist:{playlist.Id}")
        {
            NavigateRoot(typeof(HomePage), null, "home");
        }

        App.Library.DeletePlaylist(playlist.Id);
    }

    private void UpdateGlance()
    {
        GlanceListened.Text = $"{Format.Listening(App.Library.ListeningOn(DateTimeOffset.Now))} today";
        var streak = App.Library.Streak();
        GlanceStreak.Text = streak == 0 ? "Start a streak" : Format.StreakLine(streak);
        var label = $"{GlanceListened.Text}, {GlanceStreak.Text}. Open Listening Stats";
        ToolTipService.SetToolTip(GlanceItem, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GlanceItem, label);
    }

    /// <summary>The song after this one, as a row of the pane; invoked, it opens the queue.
    /// The listener can keep it out of the pane with Settings' Show next song.</summary>
    private void UpdateUpNext()
    {
        var next = App.Settings.ShowsNextSong ? Playback.Queue.Upcoming.FirstOrDefault() : null;
        _hasUpNext = next is not null;
        UpNextTitle.Text = next?.Song.Title ?? "";
        UpNextArtist.Text = next is null || string.IsNullOrEmpty(next.Song.ArtistLine)
            ? "Up next"
            : $"Up next · {next.Song.ArtistLine}";
        var label = next is null ? "Up next" : $"Up next: {next.Song}. Open the queue";
        ToolTipService.SetToolTip(UpNextItem, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UpNextItem, label);
        UpdatePaneChrome();
    }

    private void UpdatePaneChrome() =>
        UpNextItem.Visibility = _hasUpNext ? Visibility.Visible : Visibility.Collapsed;

    private void ShowQueue()
    {
        if (Playback.CurrentSong is null)
        {
            return;
        }

        _queueOpen = true;
        _lyricsOpen = false;
        ApplySidePanel();
    }

    // MARK: search in the title bar

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _suggestTimer?.Stop();
        _suggestCancel?.Cancel();
        var text = sender.Text.Trim();
        if (text.Length == 0)
        {
            ShowSuggestions(sender, RecentRows());
            return;
        }

        // A pasted YouTube link is offered instead of searching for the URL itself.
        if (YouTubeLink.Parse(sender.Text) is { } link)
        {
            ShowSuggestions(sender, [SuggestionRow.LinkRow(link)]);
            return;
        }

        // One letter is not enough to ask YouTube for rich rows; keep the
        // dropdown useful with local history and a way to submit.
        if (text.Length < 2)
        {
            ShowSuggestions(sender, MatchingRecentRows(text));
            return;
        }

        _suggestTimer?.Start();
    }

    private async System.Threading.Tasks.Task LoadSuggestionsAsync(string text)
    {
        // The link row is the only suggestion for a link, even if a tick was already queued.
        if (YouTubeLink.Parse(text) is not null)
        {
            return;
        }

        _suggestCancel?.Cancel();
        var cancel = _suggestCancel = new CancellationTokenSource();
        try
        {
            var suggestions = await EngineHost.Music.SuggestionsAsync(text.Trim(), cancel.Token);
            if (!cancel.IsCancellationRequested && TitleSearch.Text == text)
            {
                ShowSuggestions(TitleSearch, BuildSuggestions(text.Trim(), suggestions));
            }
        }
        catch (Exception)
        {
            // Suggestions are a nicety; the search itself still works. A
            // failure still leaves history and the "See all results" row.
            if (!cancel.IsCancellationRequested && TitleSearch.Text == text)
            {
                ShowSuggestions(TitleSearch, MatchingRecentRows(text.Trim()));
            }
        }
    }

    private static void ShowSuggestions(AutoSuggestBox box, List<SuggestionRow> rows)
    {
        box.ItemsSource = rows;
        box.IsSuggestionListOpen = rows.Count > 0;
    }

    private static List<SuggestionRow> RecentRows() =>
        [.. App.Library.Searches.Take(8).Select(SuggestionRow.Recent)];

    private static List<SuggestionRow> MatchingRecentRows(string text)
    {
        var normalized = SuggestionRanking.Normalize(text);
        var rows = App.Library.Searches
            .Where(query => normalized.Length == 0
                || SuggestionRanking.Normalize(query).StartsWith(normalized, StringComparison.Ordinal))
            .Take(normalized.Length == 0 ? 8 : 7)
            .Select(SuggestionRow.Recent)
            .ToList();

        if (normalized.Length > 0)
        {
            rows.Add(SuggestionRow.SeeAll(text));
        }

        return rows;
    }

    /// <summary>
    /// Turns the endpoint's two groups into one keyboard list: confident music
    /// rows first, then YouTube's query expansions, then the full-results row.
    /// The exact query and any expansion that merely repeats a promoted title
    /// are dropped so the list does not say the same thing twice.
    /// </summary>
    private static List<SuggestionRow> BuildSuggestions(string query, SearchSuggestions suggestions)
    {
        var rows = suggestions.Entities
            .Select(entity => entity.Item)
            .OfType<MusicItem>()
            .Select(item => SuggestionRow.From(item, query))
            .ToList();

        var titles = rows
            .Select(row => SuggestionRanking.Normalize(row.Title))
            .ToHashSet(StringComparer.Ordinal);
        var typed = SuggestionRanking.Normalize(query);

        var expansions = new List<SuggestionRow>();
        foreach (var candidate in suggestions.Queries)
        {
            if (expansions.Count == 4)
            {
                break;
            }

            var normalized = SuggestionRanking.Normalize(candidate);
            if (normalized.Length == 0 || normalized == typed || titles.Contains(normalized))
            {
                continue;
            }

            expansions.Add(SuggestionRow.QueryRow(candidate));
        }

        if (rows.Count > 0 && expansions.Count > 0)
        {
            expansions[0] = expansions[0].WithDivider();
        }

        rows.AddRange(expansions);
        if (query.Length > 0)
        {
            rows.Add(SuggestionRow.SeeAll(query));
        }

        return rows;
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var chosen = args.ChosenSuggestion as SuggestionRow;
        _suggestTimer?.Stop();
        _suggestCancel?.Cancel();
        sender.ItemsSource = null;
        sender.IsSuggestionListOpen = false;

        // A rich row is not a query: selecting it plays the track or opens the
        // album/artist page. The "See all results" row is the way to the page.
        if (chosen?.Item is { } item)
        {
            OpenSuggestedItem(item);
            return;
        }

        if (chosen?.Link is { } chosenLink)
        {
            sender.Text = "";
            OpenLink(chosenLink);
            return;
        }

        var query = (chosen?.Query ?? chosen?.Title ?? args.QueryText).Trim();

        // A pasted YouTube or YouTube Music link plays (or opens) what it points at.
        if (YouTubeLink.Parse(args.QueryText) is { } link)
        {
            sender.Text = "";
            OpenLink(link);
            return;
        }

        if (query.Length == 0)
        {
            return;
        }

        sender.Text = query;

        // While a video page is open the search is YouTube's.
        if (ContentFrame.Content is IVideoPage)
        {
            if (ContentFrame.Content is VideoListPage open && open.IsSearch)
            {
                open.Search(query);
            }
            else
            {
                NavigateTo(typeof(VideoListPage), new VideoListRequest(VideoListKind.Search, query));
            }

            return;
        }

        SearchFor(query);
    }

    private void OpenSuggestedItem(MusicItem item)
    {
        // On a video page a video suggestion belongs in the video player; a
        // track still goes through the audio queue.
        if (item is MusicItem.SongRecord { Value.IsVideo: true } video
            && ContentFrame.Content is IVideoPage)
        {
            OpenVideo(new Koda.Core.Video.Video(video.Value.Id, video.Value.Title));
            return;
        }

        Navigator.Open(item);
    }

    /// <summary>A link from search: a song plays with its radio, anything else opens its page.</summary>
    private void OpenLink(YouTubeLink link)
    {
        switch (link)
        {
            case YouTubeLink.Song song when ContentFrame.Content is IVideoPage:
                OpenVideo(new Koda.Core.Video.Video(song.VideoId, "YouTube video"));
                break;
            case YouTubeLink.Song song:
                _ = Playback.PlayLinkedSongAsync(song.VideoId);
                break;
            case YouTubeLink.Album album:
                NavigateTo(typeof(AlbumPage), album.BrowseId);
                break;
            case YouTubeLink.Playlist playlist:
                NavigateTo(typeof(PlaylistPage), playlist.Id);
                break;
            case YouTubeLink.Artist artist:
                NavigateTo(typeof(ArtistPage), artist.ChannelId);
                break;
        }
    }

    public void SetSearchText(string query) => TitleSearch.Text = query;

    private void AddShortcuts()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                action();
                args.Handled = true;
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }

        Add(VirtualKey.F, VirtualKeyModifiers.Control, () => TitleSearch.Focus(FocusState.Keyboard));
        Add(VirtualKey.B, VirtualKeyModifiers.Control, ToggleSidebar);
        Add(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = Koda.Services.Profiles.SwitchBackAsync());
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, GoBack);
        Add(VirtualKey.GoBack, VirtualKeyModifiers.None, GoBack);
        Add(VirtualKey.Right, VirtualKeyModifiers.Control, () => _ = Playback.NextAsync());
        Add(VirtualKey.Left, VirtualKeyModifiers.Control, () => _ = Playback.PreviousAsync());
        Add(VirtualKey.Left, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () => Playback.Skip(-App.Settings.SkipSeconds));
        Add(VirtualKey.Right, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () => Playback.Skip(App.Settings.SkipSeconds));
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // Space plays/pauses unless something that types or presses has focus.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key != VirtualKey.Space)
            {
                return;
            }

            var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
            if (focused is TextBox or AutoSuggestBox or ButtonBase or ListViewItem or Slider or ToggleSwitch)
            {
                return;
            }

            if (Video.HasVideo)
            {
                Video.TogglePlay();
            }
            else
            {
                Playback.Toggle();
            }

            args.Handled = true;
        }), true);

        // Escape closes the inline lyrics drawer before it reaches the page.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key != VirtualKey.Escape || !SidePanel.IsPaneOpen)
            {
                return;
            }

            _lyricsOpen = false;
            _queueOpen = false;
            ApplySidePanel();
            args.Handled = true;
        }), true);

        // The watch page's keys, unless something that types has focus; arrows stay with a
        // focused slider, which they move. Space is handled above.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (ContentFrame.Content is not VideoWatchPage page || args.Key == VirtualKey.Space)
            {
                return;
            }

            var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
            if (focused is TextBox or AutoSuggestBox
                || (focused is Slider && args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down))
            {
                return;
            }

            var keys = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)
                || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
            if (!keys && page.HandleKey(args.Key))
            {
                args.Handled = true;
            }
        }), true);

        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnZoomKeyDown), true);
    }

    // MARK: zoom

    /// <summary>Ctrl + and Ctrl - step the content's scale; Ctrl 0 puts it back to 100%.
    /// The main row's + and - arrive as the OEM keys, which the accelerator list cannot
    /// name, so the keys are read here instead.</summary>
    private void OnZoomKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)
            || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }

        switch ((int)args.Key)
        {
            case 0xBB: // =
            case 0x6B: // the numeric keypad's +
                ZoomBy(1);
                args.Handled = true;
                break;

            case 0xBD: // -
            case 0x6D: // the numeric keypad's -
                ZoomBy(-1);
                args.Handled = true;
                break;

            case 0x30: // 0
                SetZoom(1);
                args.Handled = true;
                break;
        }
    }

    /// <summary>Puts the saved zoom back before the first layout; the window keeps its size,
    /// and the content inside is what changes.</summary>
    private void RestoreZoom()
    {
        var saved = App.Settings.UiZoom;
        _zoom = double.IsFinite(saved) && saved > 0
            ? Math.Clamp(saved, ZoomSteps[0], ZoomSteps[^1])
            : 1;
    }

    private void ZoomBy(int direction)
    {
        var index = NearestZoomIndex(_zoom);
        SetZoom(ZoomSteps[Math.Clamp(index + direction, 0, ZoomSteps.Length - 1)]);
    }

    private static int NearestZoomIndex(double zoom)
    {
        var index = 0;
        for (var i = 1; i < ZoomSteps.Length; i++)
        {
            if (Math.Abs(ZoomSteps[i] - zoom) < Math.Abs(ZoomSteps[index] - zoom))
            {
                index = i;
            }
        }

        return index;
    }

    private void SetZoom(double zoom)
    {
        var wanted = Math.Clamp(zoom, ZoomSteps[0], ZoomSteps[^1]);
        if (Math.Abs(wanted - _zoom) > 0.0001)
        {
            _zoom = wanted;
            App.Settings.Update(settings => settings.UiZoom = _zoom);
            ApplyZoom();
        }

        ZoomBadgeText.Text = $"{Math.Round(_zoom * 100)}%";
        ZoomBadge.Visibility = Visibility.Visible;
        _zoomTimer?.Stop();
        _zoomTimer?.Start();
    }

    /// <summary>The content lays out at the room divided by the zoom, then is scaled back up:
    /// text grows and less fits, as a browser zoom does. The title bar and its caption
    /// buttons stay native and keep their normal size.</summary>
    private void ApplyZoom()
    {
        var width = ZoomHost.ActualWidth;
        var height = ZoomHost.ActualHeight;
        if (width > 0 && height > 0)
        {
            ZoomContent.Width = width / _zoom;
            ZoomContent.Height = height / _zoom;
            ZoomScale.ScaleX = _zoom;
            ZoomScale.ScaleY = _zoom;
        }
    }

    // MARK: player bar

    private void OnTrackChanged()
    {
        var song = Playback.CurrentSong;
        TrackTitleText.Text = song?.Title ?? "Nothing playing";
        TrackArtistText.Text = song?.ArtistLine ?? "";
        RefreshNextUp();
        Art.SetSource(CoverImage, song?.Artwork);
        CoverGlyph.Visibility = song?.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
        LikeButton.Visibility = song is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateLike();

        NextButton.IsEnabled = Playback.CanNext;
        PreviousButton.IsEnabled = song is not null;
        QueueButton.IsEnabled = MiniButton.IsEnabled = ShuffleButton.IsEnabled = RepeatButton.IsEnabled = song is not null;
        LyricsButton.IsEnabled = song is not null;
        SkipBackButton.IsEnabled = SkipForwardButton.IsEnabled = song is not null && !Playback.IsLoading;
        _smtc.SetTransport(Playback.CanPrev, Playback.CanNext);
        Scrubber.Maximum = Math.Max(1, Playback.DurationSeconds);
        Scrubber.Value = Math.Clamp(Playback.PositionSeconds, 0, Scrubber.Maximum);
        ElapsedText.Text = song is null ? "" : FormatTime(Scrubber.Value);
        TotalText.Text = Playback.DurationSeconds > 0 ? FormatTime(Playback.DurationSeconds) : "";
        OnQueueModesChanged();
        UpdateTaskbar();
        ApplySidePanel();

        // One toast per song, and only when the track really changed: TrackChanged also
        // fires when the queue modes or the known duration change.
        if (song is null)
        {
            _notifiedSongId = null;
        }
        else if (song.Id != _notifiedSongId)
        {
            _notifiedSongId = song.Id;
            Notifications.SongChanged(song, Playback.IsPlaying);
        }
    }

    /// <summary>What plays after this one; follows the queue as it is shuffled or reordered.
    /// Only shown when Settings' Show next song is on.</summary>
    private void RefreshNextUp()
    {
        var next = App.Settings.ShowsNextSong && Playback.CurrentSong is not null ? Playback.NextSong : null;
        NextUpText.Text = next is null ? "" : $"Next: {next.Title}";
        NextUpText.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The setting moves both places at once: the bar's line and the pane's row.</summary>
    private void RefreshNextSong()
    {
        RefreshNextUp();
        UpdateUpNext();
    }

    private void OnQueueModesChanged()
    {
        RefreshNextUp();
        ShuffleButton.IsChecked = Playback.Queue.IsShuffled;
        var repeat = Playback.Queue.Repeat;
        RepeatButton.IsChecked = repeat != RepeatMode.Off;
        RepeatIcon.Glyph = repeat == RepeatMode.One ? "" : "";
        ToolTipService.SetToolTip(RepeatButton, repeat switch
        {
            RepeatMode.All => "Repeat all",
            RepeatMode.One => "Repeat one",
            _ => "Repeat off",
        });
        NextButton.IsEnabled = Playback.CanNext;
        UpdateUpNext();
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => Playback.SetShuffle(ShuffleButton.IsChecked == true);

    private void OnRepeatClick(object sender, RoutedEventArgs e) => Playback.CycleRepeat();

    private void OnQueueClick(object sender, RoutedEventArgs e)
    {
        _queueOpen = QueueButton.IsChecked == true;
        if (_queueOpen)
        {
            _lyricsOpen = false;
        }

        ApplySidePanel();
    }

    // MARK: side panels

    /// <summary>The side panel has no place on a whole-window player or while a video is on.</summary>
    private bool SidePanelAllowed =>
        ContentFrame.Content is not (NowPlayingPage or VideoWatchPage)
        && !Video.HasVideo;

    private void OnLyricsClick(object sender, RoutedEventArgs e)
    {
        _lyricsOpen = LyricsButton.IsChecked == true;
        if (_lyricsOpen)
        {
            _queueOpen = false;
        }

        ApplySidePanel();
    }

    private void OnLyricsCloseRequested()
    {
        _lyricsOpen = false;
        ApplySidePanel();
    }

    private void OnQueueCloseRequested()
    {
        _queueOpen = false;
        ApplySidePanel();
    }

    /// <summary>Opens one drawer for the current page — the lyrics or the queue — keeping
    /// the listener's choice when a player or a video is what keeps it off screen.</summary>
    private void ApplySidePanel()
    {
        var allowed = Playback.CurrentSong is not null && SidePanelAllowed;
        var showLyrics = allowed && _lyricsOpen;
        var showQueue = allowed && _queueOpen;
        if (showLyrics && showQueue)
        {
            // One surface, one panel: the queue wins if both were left set.
            showLyrics = false;
            _lyricsOpen = false;
        }

        var show = showLyrics || showQueue;
        var wasOpen = SidePanel.IsPaneOpen;
        var wasLyrics = InlineLyrics.Visibility == Visibility.Visible;
        var wasQueue = InlineQueue.Visibility == Visibility.Visible;
        var switching = wasOpen && show
            && ((showLyrics && !wasLyrics) || (showQueue && !wasQueue));

        SetSidePanelOpen(show);

        if (switching)
        {
            // Keep both surfaces on screen for the crossfade; the incoming one is
            // brought to the front so it can fade over the one leaving.
            if (showLyrics)
            {
                InlineLyrics.Opacity = 0;
                InlineLyrics.Visibility = Visibility.Visible;
                Canvas.SetZIndex(InlineLyrics, 1);
                Canvas.SetZIndex(InlineQueue, 0);
            }
            else
            {
                InlineQueue.Opacity = 0;
                InlineQueue.Visibility = Visibility.Visible;
                Canvas.SetZIndex(InlineQueue, 1);
                Canvas.SetZIndex(InlineLyrics, 0);
            }
        }
        else
        {
            _panelCrossfade?.Stop();
            _panelCrossfadeVersion++;
            InlineLyrics.Opacity = 1;
            InlineQueue.Opacity = 1;
            InlineLyrics.Visibility = showLyrics ? Visibility.Visible : Visibility.Collapsed;
            InlineQueue.Visibility = showQueue ? Visibility.Visible : Visibility.Collapsed;
        }

        if (showLyrics)
        {
            // A switch uses the drawer's crossfade; only an entrance from a closed
            // drawer should fade the words in on their own.
            InlineLyrics.Activate(animate: !wasOpen);
        }
        else if (!switching)
        {
            InlineLyrics.Deactivate();
        }

        if (showQueue)
        {
            InlineQueue.Activate();
        }
        else if (!switching)
        {
            InlineQueue.Deactivate();
        }

        if (switching)
        {
            CrossfadePanels(toLyrics: showLyrics);
        }

        UpdateSidePanelChrome();
    }

    /// <summary>A short native storyboard crossfade between the lyrics and the queue; the
    /// drawer itself never closes or moves.</summary>
    private void CrossfadePanels(bool toLyrics)
    {
        var version = ++_panelCrossfadeVersion;
        _panelCrossfade?.Stop();

        var incoming = toLyrics ? (FrameworkElement)InlineLyrics : InlineQueue;
        var outgoing = toLyrics ? (FrameworkElement)InlineQueue : InlineLyrics;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();

        var fadeIn = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fadeIn, incoming);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        storyboard.Children.Add(fadeIn);

        if (outgoing.Visibility == Visibility.Visible)
        {
            var fadeOut = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = ease,
            };
            Storyboard.SetTarget(fadeOut, outgoing);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            storyboard.Children.Add(fadeOut);
        }

        storyboard.Completed += (_, _) =>
        {
            if (version != _panelCrossfadeVersion)
            {
                return;
            }

            InlineLyrics.Opacity = 1;
            InlineQueue.Opacity = 1;
            InlineLyrics.Visibility = SidePanel.IsPaneOpen && _lyricsOpen
                ? Visibility.Visible
                : Visibility.Collapsed;
            InlineQueue.Visibility = SidePanel.IsPaneOpen && _queueOpen
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (InlineLyrics.Visibility != Visibility.Visible)
            {
                // The drawer is still open with the queue showing: keep the rows so
                // coming back to the lyrics does not rebuild them.
                if (SidePanel.IsPaneOpen)
                {
                    InlineLyrics.Suspend();
                }
                else
                {
                    InlineLyrics.Deactivate();
                }
            }

            if (InlineQueue.Visibility != Visibility.Visible)
            {
                InlineQueue.Deactivate();
            }
        };

        _panelCrossfade = storyboard;
        storyboard.Begin();
    }

    /// <summary>Changes the pane without the light-dismiss guard mistaking a deliberate
    /// close for a tap outside it.</summary>
    private void SetSidePanelOpen(bool open)
    {
        if (SidePanel.IsPaneOpen == open)
        {
            return;
        }

        _closingSidePanel = true;
        try
        {
            SidePanel.IsPaneOpen = open;
        }
        finally
        {
            _closingSidePanel = false;
        }
    }

    /// <summary>SplitView closes an Overlay pane when the listener taps anywhere outside
    /// it. That dismissal is theirs: the drawer goes away and the bar's toggles follow,
    /// rather than staying checked for a panel that is no longer on screen.</summary>
    private void OnSidePaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
    {
        if (_closingSidePanel)
        {
            return;
        }

        _lyricsOpen = false;
        _queueOpen = false;
        UpdateSidePanelChrome();
    }

    private void OnSidePaneClosed(SplitView sender, object args) => _closingSidePanel = false;

    private void UpdateSidePanelChrome()
    {
        var hasSong = Playback.CurrentSong is not null;

        LyricsButton.IsEnabled = hasSong;
        LyricsButton.IsChecked = _lyricsOpen;
        var lyricsLabel = _lyricsOpen ? "Hide lyrics" : "Lyrics";
        ToolTipService.SetToolTip(LyricsButton, lyricsLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LyricsButton, lyricsLabel);

        QueueButton.IsEnabled = hasSong;
        QueueButton.IsChecked = _queueOpen;
        var queueLabel = _queueOpen ? "Hide queue" : "Queue";
        ToolTipService.SetToolTip(QueueButton, queueLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(QueueButton, queueLabel);
    }

    /// <summary>Wide enough for the page to keep its room with the drawer beside it, the
    /// drawer pushes the page; otherwise it floats above it so the page is not crushed.</summary>
    private void ApplySidePanelLayout()
    {
        var width = SidePanel.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var sidebar = Nav.IsPaneOpen ? Nav.OpenPaneLength : Nav.CompactPaneLength;
        var inline = width - sidebar - SidePanel.OpenPaneLength >= InlinePanelPageWidth;
        var mode = inline ? SplitViewDisplayMode.CompactInline : SplitViewDisplayMode.Overlay;
        if (SidePanel.DisplayMode == mode)
        {
            return;
        }

        // SplitView drops the open pane when it changes display mode, and a plain
        // IsPaneOpen = true is a no-op while the property still says it is open.
        // Re-assert the listener's state once the mode change has settled.
        SidePanel.DisplayMode = mode;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            SetSidePanelOpen(false);
            ApplySidePanel();
        });
    }

    private void UpdateLike()
    {
        var liked = Playback.CurrentSong is { } song && App.Library.IsLiked(song);
        LikeIcon.Glyph = liked ? "" : "";
        LikeIcon.Foreground = liked
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var label = liked ? "Remove from Liked" : "Add to Liked";
        ToolTipService.SetToolTip(LikeButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LikeButton, label);
    }

    private void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentSong is { } song)
        {
            App.Library.ToggleLike(song);
        }
    }

    /// <summary>The cover opens Now Playing, drilling in; tapped again it closes.</summary>
    private void OnNowPlayingTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ContentFrame.Content is NowPlayingPage)
        {
            GoBack();
        }
        else if (Playback.CurrentSong is not null)
        {
            ContentFrame.Navigate(typeof(NowPlayingPage), null, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
        }
    }

    /// <summary>The words name the song, so they open the album it belongs to and leave
    /// that song selected in the table.</summary>
    private void OnTrackTitleTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Playback.CurrentSong is { Album: { } album } song)
        {
            NavigateTo(typeof(AlbumPage), new AlbumPage.AlbumRequest(album.Id, song.Id));
        }
    }

    /// <summary>Right-clicking anywhere on the bar is the song's own menu, as on any row.</summary>
    private void OnPlayerBarContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (Playback.CurrentSong is not { } song)
        {
            return;
        }

        var menu = SongMenu.Build(song, [.. Playback.Queue.Items.Select(item => item.Song)]);
        if (args.TryGetPosition(PlayerBar, out var position))
        {
            menu.ShowAt(PlayerBar, position);
        }
        else
        {
            menu.ShowAt(PlayerBar);
        }

        args.Handled = true;
    }

    private void OnArtistTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Playback.CurrentSong?.Artists.FirstOrDefault(a => a.Id is not null) is { Id: { } id })
        {
            NavigateTo(typeof(ArtistPage), id);
        }
    }

    private void OnPlayStateChanged()
    {
        var playing = Playback.IsPlaying;
        var loading = Playback.IsLoading;
        PlayPauseIcon.Glyph = playing ? "" : "";
        PlayPauseIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        BarLoading.IsActive = loading;
        BarLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        var label = playing ? "Pause (Space)" : "Play (Space)";
        ToolTipService.SetToolTip(PlayPauseButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayPauseButton, playing ? "Pause" : "Play");
        PlayPauseButton.IsEnabled = Playback.CurrentSong is not null;
        Scrubber.IsEnabled = Playback.CurrentSong is not null;
        NextButton.IsEnabled = Playback.CanNext;
        SkipBackButton.IsEnabled = SkipForwardButton.IsEnabled = Playback.CurrentSong is not null && !loading;
        UpdateTaskbar();
    }

    private void OnPositionTick()
    {
        // The like state is read again each tick too: a cheap belt to the Changed braces.
        if (LikeButton.Visibility == Visibility.Visible) UpdateLike();
        UpdateTaskbar();
        var duration = Playback.DurationSeconds;
        if (duration <= 0 || _scrubbing)
        {
            return;
        }

        Scrubber.Maximum = duration;
        Scrubber.Value = Math.Clamp(Playback.PositionSeconds, 0, duration);
        ElapsedText.Text = FormatTime(Scrubber.Value);
        TotalText.Text = FormatTime(duration);
    }

    /// <summary>The taskbar's transport buttons and progress bar follow the queue.</summary>
    private void UpdateTaskbar() => _taskbar?.Update(
        Playback.CurrentSong is not null,
        Playback.IsPlaying,
        Playback.IsLoading,
        Playback.PositionSeconds,
        Playback.DurationSeconds);

    private static string FormatTime(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:D2}:{secs:D2}" : $"{minutes}:{secs:D2}";
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => Playback.Toggle();

    private void OnMiniClick(object sender, RoutedEventArgs e) => ShowMiniPlayer();

    private void OnPrevClick(object sender, RoutedEventArgs e) => _ = Playback.PreviousAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = Playback.NextAsync();

    private void OnSkipBackClick(object sender, RoutedEventArgs e) => Playback.Skip(-App.Settings.SkipSeconds);

    private void OnSkipForwardClick(object sender, RoutedEventArgs e) => Playback.Skip(App.Settings.SkipSeconds);

    /// <summary>The skip buttons follow the listener's interval, or go away if they turned
    /// them off; the tooltips and the screen reader names carry the seconds and the keys.</summary>
    private void ApplySkipSettings()
    {
        var settings = App.Settings;
        var seconds = settings.SkipSeconds;
        var visible = settings.ShowsSkipButtons ? Visibility.Visible : Visibility.Collapsed;

        SkipBackButton.Visibility = SkipForwardButton.Visibility = visible;
        SkipBackIcon.Seconds = SkipForwardIcon.Seconds = seconds;

        var back = $"Back {seconds} seconds";
        var ahead = $"Ahead {seconds} seconds";
        ToolTipService.SetToolTip(SkipBackButton, $"{back} (Ctrl+Alt+Left)");
        ToolTipService.SetToolTip(SkipForwardButton, $"{ahead} (Ctrl+Alt+Right)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SkipBackButton, back);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SkipForwardButton, ahead);
    }

    private void OnMuteClick(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        Playback.SetMuted(!Playback.IsMuted);
        UpdateMuteChrome();
    }

    /// <summary>The bar's mute button and the compact bar's volume flyout show the same
    /// state; both icons and their labels move together.</summary>
    private void UpdateMuteChrome()
    {
        var glyph = Playback.IsMuted ? "" : "";
        MuteIcon.Glyph = glyph;
        BarVolumeIcon.Glyph = glyph;
        BarVolumeMuteIcon.Glyph = glyph;
        var label = Playback.IsMuted ? "Unmute" : "Mute";
        ToolTipService.SetToolTip(MuteButton, label);
        ToolTipService.SetToolTip(BarVolumeMuteButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MuteButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BarVolumeMuteButton, label);
    }

    /// <summary>The compact bar's speaker opens the mini player's volume control: mute
    /// and the same slider, read from the current volume as the flyout appears.</summary>
    private void OnBarVolumeFlyoutOpening(object sender, object e)
    {
        _suppressBarVolume = true;
        BarVolumeSlider.Value = Playback.VolumePercent;
        _suppressBarVolume = false;
        UpdateMuteChrome();
    }

    private void OnBarVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_suppressBarVolume)
        {
            OnVolumeChanged(sender, e);
        }
    }

    /// <summary>The compact bar's one button: the actions that do not fit beside the
    /// transport, with the lyrics and mute state read at the moment the menu opens.</summary>
    private void OnBarMoreClick(object sender, RoutedEventArgs e)
    {
        var hasSong = Playback.CurrentSong is not null;
        var menu = new MenuFlyout();

        var lyrics = new MenuFlyoutItem
        {
            Text = "Lyrics",
            Icon = new FontIcon { Glyph = "\uE8F2" },
            IsEnabled = hasSong,
        };
        lyrics.Click += (_, _) =>
        {
            // The compact drawer is a surface that closes on the next tap outside it,
            // so this menu is only an opener; it never carries a checked state.
            _lyricsOpen = true;
            _queueOpen = false;
            ApplySidePanel();
        };
        menu.Items.Add(lyrics);

        var queue = new MenuFlyoutItem
        {
            Text = "Queue",
            Icon = new FontIcon { Glyph = "\uE8FD" },
            IsEnabled = hasSong,
        };
        queue.Click += (_, _) => ShowQueue();
        menu.Items.Add(queue);

        var mini = new MenuFlyoutItem
        {
            Text = "Mini player",
            Icon = new FontIcon { Glyph = "\uE9A6" },
            IsEnabled = hasSong,
        };
        mini.Click += (_, _) => ShowMiniPlayer();
        menu.Items.Add(mini);

        menu.Items.Add(new MenuFlyoutSeparator());

        var output = new MenuFlyoutItem
        {
            Text = "Audio output",
            Icon = new FontIcon { Glyph = "\uE7F5" },
        };
        output.Click += (_, _) => AudioOutputMenu.Show(BarMoreButton);
        menu.Items.Add(output);

        menu.ShowAt(BarMoreButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedRight });
    }

    private void OnOutputClick(object sender, RoutedEventArgs e) => AudioOutputMenu.Show(OutputButton);

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Playback.SetVolumePercent(e.NewValue);
        if (_suppressVolumePersistence)
        {
            return;
        }

        _volumeSaveTimer?.Stop();
        _volumeSaveTimer?.Start();
    }

    private void OnPlayerBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The bar carries five action buttons, the volume and the lyrics toggle. The
        // volume goes first, then the actions fold into the more button, so the lyrics,
        // queue and output never sit under the transport at a small window.
        var wide = e.NewSize.Width >= 1100;
        var compact = e.NewSize.Width < 900;
        // The inline slider needs the whole right side; below it the speaker opens the
        // mini player's volume flyout, so the bar keeps a volume control without one.
        VolumeSlider.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        MuteButton.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        BarVolumeButton.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        if (wide)
        {
            // The slider was hidden while the compact flyout owned the volume.
            _suppressVolumePersistence = true;
            VolumeSlider.Value = Playback.VolumePercent;
            _suppressVolumePersistence = false;
        }
        MiniButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LyricsButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        QueueButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        OutputButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BarMoreButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The title and artist take the room the cover, the gaps and the heart leave;
    /// past that they trim with an ellipsis, so the heart stays beside the words instead of
    /// at the cell's edge. The text column is Auto, with the stack capped at the available
    /// width: an Auto column alone measures its child with unlimited width, which lets the
    /// text paint past its slot and be clipped without an ellipsis, but the cap makes the
    /// column as wide as the words really are, so a short title keeps the heart close. A
    /// little of that width is reserved for the ellipsis itself, which WinUI otherwise lets
    /// the last word crowd out of the slot.</summary>
    private void OnTrackInfoSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var reserve = Math.Max(10, Math.Round(TrackTitleText.FontSize * 0.8));
        var width = Math.Clamp(e.NewSize.Width - 56 - 36 - 36 - reserve, 80, 280);
        TrackInfoText.MaxWidth = width;

        // Writing the lines again makes the text measure and trim at the new room; WinUI
        // otherwise keeps the truncation it decided at the old width.
        var song = Playback.CurrentSong;
        TrackTitleText.Text = song?.Title ?? "Nothing playing";
        TrackArtistText.Text = song?.ArtistLine ?? "";
        RefreshNextUp();
    }

    private void OnCoverPointerEntered(object sender, PointerRoutedEventArgs e) =>
        FadeCoverHover(Playback.CurrentSong is not null);

    private void OnCoverPointerExited(object sender, PointerRoutedEventArgs e) =>
        FadeCoverHover(false);

    /// <summary>Fades the cover's scrim instead of popping it. A reversal mid-fade starts
    /// from the opacity on screen, so a quick in-and-out never jumps.</summary>
    private void FadeCoverHover(bool show)
    {
        var from = CoverHover.Opacity;
        _coverHoverFade?.Stop();

        var fade = new DoubleAnimation
        {
            From = from,
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(show ? 140 : 100),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, CoverHover);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        _coverHoverFade = storyboard;
        storyboard.Begin();
    }

    /// <summary>Clears the scrim without a fade when the bar disappears underneath the
    /// pointer; the next hover starts from transparent.</summary>
    private void ClearCoverHover()
    {
        _coverHoverFade?.Stop();
        _coverHoverFade = null;
        CoverHover.Opacity = 0;
    }

    // MARK: profiles

    /// <summary>The title bar's account chip: the active channel, and the way to switch
    /// account or channel.</summary>
    private void UpdateProfileChip()
    {
        var roster = Koda.Services.Profiles.Roster;
        var active = roster.Active;
        var photo = active.ListeningPhoto is { } photoUrl && Uri.TryCreate(photoUrl, UriKind.Absolute, out var url)
            ? new BitmapImage(url) { DecodePixelWidth = 52 }
            : null;

        TitleProfilePicture.ProfilePicture = photo;
        // PersonPicture draws its initials under the photo. Leave them blank when there is
        // a photo, or they show through as the TitleBar dims the chip in an inactive window.
        TitleProfilePicture.DisplayName = photo is null ? active.ListeningName : "";
        TitleProfileName.Text = active.ListeningName;

        var label = $"{active.ListeningName}, {active.ListeningSubtitle}. Switch account or channel";
        ToolTipService.SetToolTip(TitleProfileButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TitleProfileButton, label);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => AppTitleBar.RecomputeDragRegions());
    }

    private async void OnProfileButtonClick(object sender, RoutedEventArgs e)
    {
        var active = Koda.Services.Profiles.Roster.Active;
        if (active.IsSignedIn)
        {
            var refresh = Koda.Services.Profiles.RefreshChannelsAsync();
            if (active.Channels.Count == 0)
            {
                // The first open can wait a moment for the channel list; later opens use the cache.
                await System.Threading.Tasks.Task.WhenAny(
                    refresh, System.Threading.Tasks.Task.Delay(1500));
            }
            else
            {
                _ = refresh;
            }
        }

        ShowProfileMenu(TitleProfileButton);
    }

    private void ShowProfileMenu(FrameworkElement anchor)
    {
        var roster = Koda.Services.Profiles.Roster;
        var active = roster.Active;
        var menu = new MenuFlyout();

        var heading = active.IsSignedIn
            ? active.AccountName ?? active.DisplayName
            : active.DisplayName;
        menu.Items.Add(new MenuFlyoutItem { Text = heading, IsEnabled = false });

        if (active.IsSignedIn)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var change = new MenuFlyoutSubItem { Text = "Change channel" };
            if (active.Channels.Count > 0)
            {
                foreach (var channel in active.Channels)
                {
                    var target = channel;
                    var item = new RadioMenuFlyoutItem
                    {
                        Text = channel.DisplayName,
                        GroupName = "Channel",
                        IsChecked = string.Equals(channel.PageId, active.ActiveChannelPageId, StringComparison.Ordinal),
                    };
                    item.Click += (_, _) => _ = SwitchChannelAsync(target);
                    change.Items.Add(item);
                }
            }
            else
            {
                var reason = Koda.Services.Profiles.LastChannelsError;
                change.Items.Add(new MenuFlyoutItem
                {
                    Text = reason is null ? "Channels unavailable" : $"Channels unavailable: {reason}",
                    IsEnabled = false,
                });
                var retry = new MenuFlyoutItem { Text = "Try again", Icon = new FontIcon { Glyph = "\uE72C" } };
                retry.Click += async (_, _) =>
                {
                    await Koda.Services.Profiles.RefreshChannelsAsync();
                    await System.Threading.Tasks.Task.Delay(150);
                    ShowProfileMenu(anchor);
                };
                change.Items.Add(retry);
            }
            menu.Items.Add(change);
        }

        var accounts = roster.Profiles
            .Where(profile => profile.Id != active.Id && profile.IsSignedIn)
            .ToList();
        if (accounts.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var other = new MenuFlyoutSubItem { Text = "Other accounts" };
            foreach (var profile in accounts)
            {
                other.Items.Add(ProfileRow(profile, roster.ActiveId));
            }
            menu.Items.Add(other);
        }

        var offline = roster.Profiles
            .Where(profile => profile.Id != active.Id && !profile.IsSignedIn)
            .ToList();
        if (offline.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var group = new MenuFlyoutSubItem { Text = "Offline" };
            foreach (var profile in offline)
            {
                group.Items.Add(ProfileRow(profile, roster.ActiveId));
            }
            menu.Items.Add(group);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(SongMenu.Item("Add account…", "\uE710", () => _ = Koda.Services.Profiles.AddSignedInAsync()));
        menu.Items.Add(SongMenu.Item("Manage profiles…", "\uE713", () => _ = ProfilesDialog.ShowAsync()));
        if (active.IsSignedIn)
        {
            menu.Items.Add(SongMenu.Item("Sign out", "\uE7E8", SignOutActive));
        }

        menu.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;
        menu.ShowAt(anchor);
    }

    private static RadioMenuFlyoutItem ProfileRow(Koda.Core.Profiles.KodaProfile profile, Guid activeId)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = profile.DisplayName,
            GroupName = "Profile",
            IsChecked = profile.Id == activeId,
        };
        item.Click += (_, _) => _ = Koda.Services.Profiles.SwitchAsync(profile.Id);
        return item;
    }

    /// <summary>Signing out keeps the library and downloads on this PC; the profile becomes
    /// an offline one, exactly as the account section has always done.</summary>
    private void SignOutActive()
    {
        if (!Koda.Services.Profiles.Roster.Active.IsSignedIn)
        {
            return;
        }

        try
        {
            App.Account.SignOut();
        }
        catch (Exception error)
        {
            App.WriteError("signing out", error);
        }

        Koda.Services.Profiles.FollowAccount();
        NavigateRoot(typeof(HomePage), null, "home", fresh: true);
        Note("Signed out. The library and downloads stay on this PC.");
    }

    /// <summary>Uses YouTube's server-issued switch URL, then swaps the library to the channel's
    /// folder. A cancel leaves the current channel as it was.</summary>
    private async System.Threading.Tasks.Task SwitchChannelAsync(Koda.Core.Profiles.KodaChannel channel)
    {
        if (Koda.Services.EngineHost.Music.Accounts.Active?.Cookies is not { } cookies)
        {
            Note("Sign in again to switch channels.");
            return;
        }

        string? warning = null;
        var updated = cookies;
        if (channel.SigninUrl is { } signinUrl)
        {
            YouTubeSignInDialog.ChannelSwitchResult switched;
            try
            {
                switched = await YouTubeSignInDialog.SwitchChannelAsync(signinUrl, cookies, channel.PageId);
            }
            catch (Koda.Core.YouTubeSignInException error)
            {
                Note(error.Message);
                return;
            }

            if (switched.Cookies is null)
            {
                return;
            }

            warning = switched.Warning;
            updated = switched.Cookies;
        }
        else
        {
            // Without a server-issued identity link the library can still move; only the
            // cookie-bound playback history may stay where it was.
            warning = "Switching the channel's library. YouTube didn't provide an identity "
                + "link, so history may stay on the previous channel.";
        }

        try
        {
            await Koda.Services.Profiles.SwitchChannelAsync(channel, updated);
            if (warning is { } message) Warn(message);
        }
        catch (Koda.Core.YouTubeSignInException error)
        {
            Note(error.Message);
        }
        catch (Koda.Core.MusicException)
        {
            Note("Couldn't switch channels. Try again in a moment.");
        }
        catch (Exception error)
        {
            App.WriteError("switching channel", error);
            Note("Couldn't switch channels. The reason was written to crash.txt.");
        }
    }

    // MARK: video

    /// <summary>The video playlists kept on this PC, under Watch History while videos are on.</summary>
    private void RebuildVideoPlaylists(bool on)
    {
        var playlists = on ? App.Library.VideoPlaylists : [];
        var signature = string.Join("|", playlists.Select(p => $"{p.Id}:{p.Name}"));
        if (signature == _videoPlaylistSignature)
        {
            return;
        }

        _videoPlaylistSignature = signature;
        foreach (var old in _videoPlaylistItems)
        {
            Nav.MenuItems.Remove(old);
        }

        _videoPlaylistItems.Clear();
        var at = Nav.MenuItems.IndexOf(VideoHistoryItem) + 1;
        foreach (var playlist in playlists)
        {
            var item = new NavigationViewItem
            {
                Content = playlist.Name,
                Tag = $"video-playlist:{playlist.Id}",
                Icon = new FontIcon { Glyph = "\uE90B" },
            };
            ToolTipService.SetToolTip(item, playlist.Name);
            var menu = new MenuFlyout();
            var shown = playlist;
            menu.Items.Add(SongMenu.Item("Rename…", "\uE70F", () => _ = Dialogs.RenameVideoPlaylistAsync(shown)));
            menu.Items.Add(SongMenu.Item("Delete playlist…", "\uE74D", () => _ = DeleteVideoPlaylistAsync(shown)));
            item.ContextFlyout = menu;
            Nav.MenuItems.Insert(at++, item);
            _videoPlaylistItems.Add(item);
        }

        if (_currentTag is { } tag && tag.StartsWith("video-playlist:", StringComparison.Ordinal))
        {
            SelectTag(tag);
        }
    }

    private async System.Threading.Tasks.Task DeleteVideoPlaylistAsync(Koda.Core.Video.VideoPlaylist playlist)
    {
        if (!await Dialogs.ConfirmAsync($"Delete “{playlist.Name}”?",
                "The videos stay wherever else you saved them.", "Delete Playlist"))
        {
            return;
        }

        if (_currentTag == $"video-playlist:{playlist.Id}")
        {
            NavigateRoot(typeof(VideoListPage), new VideoListRequest(VideoListKind.Home), "video-home");
        }

        App.Library.DeleteVideoPlaylist(playlist.Id);
    }

    /// <summary>Starts a video and opens its page; the one everything that opens a video goes through.</summary>
    public void OpenVideo(Koda.Core.Video.Video video)
    {
        _ = Video.PlayAsync(video);
        if (ContentFrame.Content is not VideoWatchPage)
        {
            NavigateTo(typeof(VideoWatchPage));
        }
    }

    /// <summary>The videos in the sidebar follow the setting, and the account's own lists need the account.</summary>
    private void UpdateVideoSidebar()
    {
        var on = App.Settings.ShowsVideos;
        var signedIn = App.Account.IsSignedIn;
        var visible = on ? Visibility.Visible : Visibility.Collapsed;
        var account = on && signedIn ? Visibility.Visible : Visibility.Collapsed;
        VideosHeader.Visibility = VideoHomeItem.Visibility = VideoExploreItem.Visibility = VideoHistoryItem.Visibility = visible;
        VideoLaterItem.Visibility = VideoLikedItem.Visibility = account;

        // Signed out, channels followed on this PC make Subscriptions and Channels.
        VideoSubscriptionsItem.Visibility = VideoChannelsItem.Visibility =
            on && (signedIn || App.Library.Subscriptions.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        RebuildVideoPlaylists(on);

        // Turned off: nothing of it stays behind, not a video in the bar nor a page of it.
        if (!on)
        {
            if (Video.HasVideo)
            {
                Video.Stop();
            }

            if (ContentFrame.Content is IVideoPage)
            {
                NavigateRoot(typeof(HomePage), null, "home");
            }
        }
    }

    /// <summary>One bar at a time: the music's, the video's while a video is on, none on a
    /// whole-window player. The picture follows to the bar when the watch page is left.</summary>
    private void UpdateVideoBar()
    {
        var onWatch = ContentFrame.Content is VideoWatchPage;
        var onNowPlaying = ContentFrame.Content is NowPlayingPage;
        var videoOn = Video.HasVideo && !onWatch;
        PlayerBar.Visibility = !onWatch && !onNowPlaying && !videoOn ? Visibility.Visible : Visibility.Collapsed;
        if (PlayerBar.Visibility == Visibility.Collapsed)
        {
            // The bar may vanish under the pointer (Now Playing, video); clear the cover's
            // hover scrim so it is not stuck on when the bar comes back.
            ClearCoverHover();
        }
        VideoBar.Visibility = videoOn && !onNowPlaying ? Visibility.Visible : Visibility.Collapsed;
        if (VideoBar.Visibility == Visibility.Visible)
        {
            if (!Video.IsAttached(VideoBarPicture))
            {
                Video.Attach(VideoBarPicture);
            }

            VideoBarTitle.Text = Video.Current?.Title ?? "";
            VideoBarChannel.Text = Video.Current?.Channel?.Name ?? "";
            VideoPlayIcon.Glyph = Video.IsPlaying ? "\uF8AE" : "\uF5B0";
            var label = Video.IsPlaying ? "Pause" : "Play";
            ToolTipService.SetToolTip(VideoPlayButton, $"{label} (Space)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(VideoPlayButton, label);
            UpdateVideoBarTime();
        }

        ApplySidePanel();
    }

    private void UpdateVideoBarTime()
    {
        if (VideoBar.Visibility != Visibility.Visible || _videoScrubbing)
        {
            return;
        }

        var duration = Video.Duration;
        if (duration > 0)
        {
            VideoScrubber.Maximum = duration;
            VideoScrubber.Value = Math.Clamp(Video.Position, 0, duration);
        }

        VideoElapsed.Text = FormatTime(Video.Position);
        VideoTotal.Text = FormatTime(duration);
    }

    private void OnVideoBarTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Video.HasVideo)
        {
            NavigateTo(typeof(VideoWatchPage));
        }
    }

    private void OnVideoPlayClick(object sender, RoutedEventArgs e) => Video.TogglePlay();

    private void OnVideoBackClick(object sender, RoutedEventArgs e) => Video.Skip(-10);

    private void OnVideoAheadClick(object sender, RoutedEventArgs e) => Video.Skip(10);

    private void OnVideoCloseClick(object sender, RoutedEventArgs e)
    {
        Video.Detach(VideoBarPicture);
        Video.Stop();
    }

    /// <summary>
    /// Full screen for a video, the way Windows means it: the window takes the full-screen
    /// presenter (no frame, no title bar, over the taskbar), and the player, handed over by the
    /// watch page, is lifted into a layer over the whole window. The shell under it is left
    /// alone, so nothing of it (sidebar, title bar row, the page's edges) can show around the
    /// picture, and leaving puts the window back exactly as it was.
    ///
    /// The app's own title bar is let go for the duration: with content extended into the title
    /// bar, the window keeps the strip of frame that holds the resize edge, and under the
    /// full-screen presenter that strip shows as a light line across the top of the screen.
    /// </summary>
    public void EnterVideoFullScreen(UIElement player)
    {
        if (_videoFullScreen)
        {
            return;
        }

        _videoFullScreen = true;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        ExtendsContentIntoTitleBar = false;
        // The title bar's drag region outlives it: left in place, the top of the picture would
        // drag the window and a double click there would try to maximize it.
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).ClearAllRegionRects();
        FullWindowLayer.Children.Add(player);
        FullWindowLayer.Visibility = Visibility.Visible;
        FillWithContent(Handle);
    }

    /// <summary>
    /// The Windows App SDK window keeps the top pixel of its client area for the resize edge and
    /// starts its content (the DesktopChildSiteBridge) one pixel down, even under the full-screen
    /// presenter, where there is no edge to resize. That row shows the bare window: a light
    /// line across the top of a full-screen video. In full screen the content is given the
    /// whole client area, after every resize, since the window sets the offset again on each.
    /// </summary>
    private static void FillWithContent(nint hwnd)
    {
        var content = Win32.FindWindowEx(hwnd, 0, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
        if (content == 0 || !Win32.GetClientRect(hwnd, out var client) || !Win32.GetWindowRect(content, out var placed))
        {
            return;
        }

        // Child positions are in the parent's client coordinates, which here start at the screen
        // origin of the window; only a content window that doesn't already fill it is moved.
        if (!Win32.GetWindowRect(hwnd, out var window)
            || (placed.Left == window.Left && placed.Top == window.Top
                && placed.Width == client.Width && placed.Height == client.Height))
        {
            return;
        }

        Win32.SetWindowPos(content, 0, 0, 0, client.Width, client.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    /// <summary>The player goes back to the page (the caller takes it), and the window back to
    /// its frame and the app's title bar.</summary>
    public void LeaveVideoFullScreen(UIElement player)
    {
        if (!_videoFullScreen)
        {
            return;
        }

        _videoFullScreen = false;
        FullWindowLayer.Children.Remove(player);
        FullWindowLayer.Visibility = Visibility.Collapsed;

        // The title bar is ours again before the frame comes back, so Windows' own never flashes.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        PaintCaptionButtons();
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
    }

    // MARK: sidebar

    private void ToggleSidebar()
    {
        if (_videoFullScreen)
        {
            return;
        }

        App.Settings.Update(s => s.HidesSidebar = !s.HidesSidebar);
        ApplySidebar();
    }

    /// <summary>The sidebar shown or hidden entirely, as the listener left it. The title bar's
    /// button says which way it will go.</summary>
    private void ApplySidebar()
    {
        var hidden = App.Settings.HidesSidebar;
        var visible = !hidden;
        if (Nav.IsPaneVisible != visible)
        {
            // NavigationView opens the pane wide whenever it is shown again; a sidebar the
            // listener had collapsed to icons comes back collapsed.
            if (!visible)
            {
                _paneWasOpen = Nav.IsPaneOpen;
            }

            // In Left mode a hidden pane that was collapsed to icons keeps its compact strip
            // reserved (the SplitView stays CompactInline). LeftMinimal reserves nothing, so the
            // hidden sidebar is Minimal, with its toggle button hidden too.
            Nav.PaneDisplayMode = visible ? NavigationViewPaneDisplayMode.Left : NavigationViewPaneDisplayMode.LeftMinimal;
            Nav.IsPaneToggleButtonVisible = visible;
            Nav.CompactPaneLength = visible ? CompactPaneWidth : 0;
            Nav.IsPaneVisible = visible;
            if (visible)
            {
                var open = _paneWasOpen;
                Nav.IsPaneOpen = open;
                // Back from Minimal, the section headers keep their wide look until the pane
                // changes state after the mode switch has settled; a collapsed sidebar is
                // opened and closed again then, so they take the compact one.
                var settle = DispatcherQueue.CreateTimer();
                settle.Interval = TimeSpan.FromMilliseconds(120);
                settle.IsRepeating = false;
                settle.Tick += (_, _) =>
                {
                    if (!Nav.IsPaneVisible) return;
                    if (!open) Nav.IsPaneOpen = true;
                    Nav.IsPaneOpen = open;
                };
                settle.Start();
            }
        }
    }

    // MARK: status ribbon

    private void OnStatusRaised(PlaybackService.StatusTone tone, string message)
    {
        StatusRibbon.Message = message;
        StatusRibbon.Title = string.Empty;
        StatusRibbon.Severity = tone == PlaybackService.StatusTone.Pacing ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
        StatusRibbon.IsOpen = true;
        _statusTimer?.Stop();
        _statusTimer?.Start();

        // A failure while the window is away deserves a toast; pacing notes do not.
        if (tone == PlaybackService.StatusTone.Fault)
        {
            Notifications.Fault(message);
        }
    }

    private void OnStatusClosed(InfoBar sender, object args) => _statusTimer?.Stop();

    /// <summary>A short note in the ribbon, for pages (import done, saved, and the like).</summary>
    public void Note(string message) => OnStatusRaised(PlaybackService.StatusTone.Pacing, message);

    /// <summary>A note that needs attention: same ribbon, warning colour.</summary>
    public void Warn(string message)
    {
        StatusRibbon.Message = message;
        StatusRibbon.Title = string.Empty;
        StatusRibbon.Severity = InfoBarSeverity.Warning;
        StatusRibbon.IsOpen = true;
        _statusTimer?.Stop();
        _statusTimer?.Start();
    }

    // MARK: SMTC + tray

    private void OnSmtcButton(SystemMediaTransportControlsButton button)
    {
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play:
            case SystemMediaTransportControlsButton.Pause:
                Playback.Toggle();
                break;
            case SystemMediaTransportControlsButton.Next:
                _ = Playback.NextAsync();
                break;
            case SystemMediaTransportControlsButton.Previous:
                _ = Playback.PreviousAsync();
                break;
        }
    }

    private void OnTrayCommand(uint command)
    {
        switch (command)
        {
            case TrayIconHost.MenuPlayPause:
                Playback.Toggle();
                break;
            case TrayIconHost.MenuShow:
                ShowFromTray();
                break;
            case TrayIconHost.MenuExit:
                ExitApplication();
                break;
        }
    }

    /// <summary>
    /// Brings the running window forward: from the tray, or because a second
    /// launch was redirected here. A minimised window is restored; a maximised
    /// or windowed one keeps the state and size the listener left it in.
    /// </summary>
    public void ShowFromTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // AppWindow.Show() brings a minimised window back at its normal rectangle,
        // dropping the maximised state; a hidden window keeps it. Reveal only a
        // hidden window here, and let the shell restore a minimised one to the
        // state it was minimised from.
        if (!Win32.IsWindowVisible(hwnd))
        {
            AppWindow.Show();
        }

        var restoredMaximized = false;
        if (Win32.IsIconic(hwnd))
        {
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            restoredMaximized = Win32.IsZoomed(hwnd);
        }

        // Activate() shows the window normally and would undo a restored maximised
        // state; that one comes forward through the foreground calls below instead.
        if (!restoredMaximized)
        {
            Activate();
        }
        if (!Win32.SetForegroundWindow(hwnd) || Win32.GetForegroundWindow() != hwnd)
        {
            Win32.SwitchToThisWindow(hwnd, altTab: true);
        }

        SetShellVisible(true);
    }

    /// <summary>
    /// The shell window is hidden to the tray or minimized: nothing on screen is being
    /// read, so the lyrics stop painting and let go of their word surfaces while the
    /// music keeps playing. Coming back rebuilds the words for the line being sung,
    /// the same way returning to Now Playing does.
    /// </summary>
    private void SetShellVisible(bool visible)
    {
        if (ContentFrame.Content is NowPlayingPage page)
        {
            page.SetShellVisible(visible);
        }

        if (visible)
        {
            if (_lyricsSuspendedForShell)
            {
                _lyricsSuspendedForShell = false;
                InlineLyrics.Resume(animate: false);
            }

            return;
        }

        // Suspend the drawer only if it was showing: resuming a pane the listener had
        // closed would open it again when the window comes back.
        if (InlineLyrics.Visibility == Visibility.Visible)
        {
            _lyricsSuspendedForShell = true;
            InlineLyrics.Suspend();
        }
    }

    /// <summary>
    /// Open the compact overlay, or bring the one that is already open forward. It is a
    /// second window so the library stays usable behind it; a second request focuses the
    /// same overlay instead of stacking another.
    /// </summary>
    /// <summary>Whether the listener can see what is playing: the window is up and not minimized,
    /// or the mini player is open. A song notification is for when they cannot.</summary>
    public bool ShowsPlayback =>
        _mini is not null ||
        (AppWindow.IsVisible && (AppWindow.Presenter as OverlappedPresenter)?.State != OverlappedPresenterState.Minimized);

    public void ShowMiniPlayer()
    {
        if (Playback.CurrentSong is null)
        {
            return;
        }

        if (_mini is not null)
        {
            _mini.Activate();
            MinimizeShell();
            return;
        }

        var mini = new MiniPlayerWindow();
        _mini = mini;
        mini.Closed += (_, _) =>
        {
            if (ReferenceEquals(_mini, mini))
            {
                _mini = null;
            }
        };
        mini.Activate();
        MinimizeShell();
    }

    /// <summary>The overlay is the player now, so the library gets out of the way. The
    /// main window keeps its restored/maximised placement for when it comes back.</summary>
    private void MinimizeShell()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    /// <summary>Bring the library forward with Now Playing showing, optionally with one of
    /// its side panels open. The mini player uses this as its way out.</summary>
    public void ShowNowPlaying(string? panel = null)
    {
        ShowFromTray();
        if (Playback.CurrentSong is null)
        {
            return;
        }

        if (ContentFrame.Content is NowPlayingPage open)
        {
            if (panel is not null)
            {
                open.OpenPanel(panel);
            }

            return;
        }

        ContentFrame.Navigate(
            typeof(NowPlayingPage),
            panel,
            new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
    }

    private VideoPipWindow? _videoPip;

    /// <summary>The video in its own always-on-top overlay; a second request toggles it closed.</summary>
    public void ToggleVideoPictureInPicture()
    {
        if (_videoPip is not null)
        {
            _videoPip.Close();
            return;
        }

        if (!Video.HasVideo) return;
        if (_videoFullScreen && ContentFrame.Content is VideoWatchPage page) page.LeaveFullScreen();
        var pip = new VideoPipWindow();
        _videoPip = pip;
        pip.RestoreRequested += () =>
        {
            pip.Close();
            ShowFromTray();
            if (ContentFrame.Content is not VideoWatchPage) NavigateTo(typeof(VideoWatchPage));
        };
        pip.Closed += (_, _) =>
        {
            if (ReferenceEquals(_videoPip, pip)) _videoPip = null;
        };
        pip.Activate();
    }

    public bool InVideoPictureInPicture => _videoPip is not null;

    /// <summary>Hiding the library hides the overlay with it; there is nothing left to see.</summary>
    public void CloseMiniPlayer()
    {
        var mini = _mini;
        _mini = null;
        mini?.Close();
    }

    private void ExitApplication()
    {
        try
        {
            WindowPlacement.Save(Handle);
            Shutdown();
        }
        catch (Exception error)
        {
            App.WriteError("exiting Koda", error);
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>Hands the shell integrations back once, whether the listener chose Exit
    /// or closed the window with close-to-tray off. The guard keeps the Closed handler
    /// from disposing the engine a second time after ExitApplication already did.</summary>
    private void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        CloseMiniPlayer();
        Video.Shutdown();
        Playback.Shutdown();
        _taskbar?.Dispose();
        _tray?.Dispose();
        _smtc.Dispose();
        App.ForgetShell(this);
        _current = null;
    }

    /// <summary>The end of a Windows session: keep the queue, the library and the
    /// window placement, but do not stop playback in case the shutdown is cancelled.</summary>
    private static void FlushSession()
    {
        if (_current is { } window)
        {
            WindowPlacement.Save(window.Handle);
        }

        PlaybackService.Instance.SaveState();
        App.Library.Flush();
        App.AudioStore.Flush();
        App.Settings.Save();
    }

    private static nint OnWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_GETMINMAXINFO:
            {
                var scale = Win32.GetDpiForWindow(hwnd) / 96.0;
                var info = Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
                info.ptMinTrackSize.X = (int)Math.Ceiling(MinWindowDipWidth * scale);
                info.ptMinTrackSize.Y = (int)Math.Ceiling(MinWindowDipHeight * scale);
                Marshal.StructureToPtr(info, lParam, false);
                return 0;
            }

            case Win32.WM_CLOSE:
                if (_current is not null)
                {
                    WindowPlacement.Save(hwnd);
                }

                if (App.CloseToTray && _current is not null)
                {
                    _current.CloseMiniPlayer();
                    _current.SetShellVisible(false);
                    _current.AppWindow.Hide();
                    return 0;
                }

                break;

            case Win32.WM_SIZE when _current is { _videoFullScreen: true, _previousWndProc: not 0 } shell:
            {
                // The window lays its content out first (one pixel down); full screen then
                // takes that pixel back. See FillWithContent.
                var result = Win32.CallWindowProc(shell._previousWndProc, hwnd, msg, wParam, lParam);
                FillWithContent(hwnd);
                return result;
            }

            case Win32.WM_SIZE:
                // The lyrics are not read while the window is out of sight; a minimized
                // shell lets them go, restoring brings them back for the line playing.
                if ((int)wParam == Win32.SIZE_MINIMIZED)
                {
                    _current?.SetShellVisible(false);
                }
                else if ((int)wParam == Win32.SIZE_RESTORED || (int)wParam == Win32.SIZE_MAXIMIZED)
                {
                    _current?.SetShellVisible(true);
                }

                // Maximising and restoring from the caption never ends a drag, so
                // WM_EXITSIZEMOVE does not fire for them. Save the state as soon as
                // it flips; during a drag it has not changed, so nothing is written.
                // Until the saved placement has been applied, WinUI's own first show
                // is still moving the window, and its state is not the listener's.
                if (_current is not null
                    && _current._placementApplied
                    && ((int)wParam == Win32.SIZE_MAXIMIZED || (int)wParam == Win32.SIZE_RESTORED))
                {
                    WindowPlacement.SaveState(hwnd);
                }

                break;

            case Win32.WM_EXITSIZEMOVE:
                if (_current is not null)
                {
                    WindowPlacement.Save(hwnd);
                }

                break;

            case Win32.WM_COMMAND:
            {
                var id = (int)((long)wParam & 0xFFFF);
                var code = (int)(((long)wParam >> 16) & 0xFFFF);
                if (code == Taskbar.ThumbnailClicked && _current?._taskbar?.HandleCommand(id) == true)
                {
                    return 0;
                }

                break;
            }

            case Win32.WM_QUERYENDSESSION:
                FlushSession();
                return 1;

            case Win32.WM_ENDSESSION:
                if (wParam != 0)
                {
                    FlushSession();
                }

                return 0;

            case TrayIconHost.TrayCallbackMessage:
                _current?._tray?.HandleMessage(lParam);
                return 0;

            case Win32.WM_NCDESTROY:
                if (_current is not null && _current._previousWndProc != 0)
                {
                    Win32.SetWindowLongPtr(hwnd, Win32.GWLP_WNDPROC, _current._previousWndProc);
                    _current._previousWndProc = 0;
                }

                break;
        }

        if (_current is not null && _current._previousWndProc != 0)
        {
            return Win32.CallWindowProc(_current._previousWndProc, hwnd, msg, wParam, lParam);
        }

        return 0;
    }
}
