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
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Media;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;
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
    private readonly SmtcController _smtc = new();
    private readonly Windows.UI.ViewManagement.UISettings _systemColors = new();
    private nint _previousWndProc;
    private bool _placementApplied;
    private bool _scrubbing;
    private bool _suppressVolumePersistence;
    private string? _currentTag;
    private string? _notifiedSongId;
    private string _playlistSignature = "";
    private bool _hasUpNext;
    private readonly List<NavigationViewItem> _playlistItems = [];
    private DispatcherQueueTimer? _volumeSaveTimer;
    private DispatcherQueueTimer? _statusTimer;
    private DispatcherQueueTimer? _suggestTimer;
    private CancellationTokenSource? _suggestCancel;
    private YouTubeLink? _linkSuggestion;
    private DispatcherQueueTimer? _zoomTimer;
    private double _zoom = 1;
    private bool _shutdown;

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
        RestoreZoom();

        AddShortcuts();
        ApplySkipSettings();
        ApplySidebar();

        ContentFrame.Navigated += OnContentNavigated;
        Nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdatePaneChrome());
        NavigateRoot(typeof(HomePage), null, "home");
        RefreshSidebar();

        OnTrackChanged();
        OnPlayStateChanged();

        Closed += (_, _) =>
        {
            // A WinUI app keeps its message loop (and its XAML heap) alive after the last
            // window closes; Application.Exit alone still left a windowless process behind,
            // growing by 10-24 MB a minute. Closing the window is the listener quitting:
            // clean up once, then end the process.
            try
            {
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

        TitleProfileName.Visibility = width < 980 ? Visibility.Collapsed : Visibility.Visible;
        TitleSearch.Width = width >= 1040 ? 420 : width >= 860 ? 340 : 240;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => AppTitleBar.RecomputeDragRegions());
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
        var queue = SongMenu.Item("Add to queue", "\uE71C", () => Playback.AddToQueue(playlist.Songs));
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
        if (Playback.CurrentSong is not null && ContentFrame.Content is not NowPlayingPage)
        {
            ContentFrame.Navigate(typeof(NowPlayingPage), "queue", new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
        }
    }

    // MARK: search in the title bar

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _suggestTimer?.Stop();
        _linkSuggestion = YouTubeLink.Parse(sender.Text);
        if (sender.Text.Trim().Length == 0)
        {
            sender.ItemsSource = App.Library.Searches.Take(8).ToList();
            return;
        }

        // A pasted YouTube link is offered instead of searching for the URL itself.
        if (_linkSuggestion is { } link)
        {
            _suggestCancel?.Cancel();
            sender.ItemsSource = new[] { link.OpenTitle };
            sender.IsSuggestionListOpen = true;
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
                TitleSearch.ItemsSource = suggestions.Take(8).ToList();
            }
        }
        catch (Exception)
        {
            // Suggestions are a nicety; the search itself still works.
        }
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var chosen = args.ChosenSuggestion as string;
        var query = (chosen ?? args.QueryText).Trim();
        var link = chosen is not null && _linkSuggestion is { } pending && chosen == pending.OpenTitle
            ? pending
            : YouTubeLink.Parse(args.QueryText);
        _suggestTimer?.Stop();
        _suggestCancel?.Cancel();
        sender.ItemsSource = null;

        // A pasted YouTube or YouTube Music link plays (or opens) what it points at.
        if (link is not null)
        {
            _linkSuggestion = null;
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
        SkipBackButton.IsEnabled = SkipForwardButton.IsEnabled = song is not null && !Playback.IsLoading;
        _smtc.SetTransport(Playback.CanPrev, Playback.CanNext);
        Scrubber.Maximum = Math.Max(1, Playback.DurationSeconds);
        Scrubber.Value = Math.Clamp(Playback.PositionSeconds, 0, Scrubber.Maximum);
        ElapsedText.Text = song is null ? "" : FormatTime(Scrubber.Value);
        TotalText.Text = Playback.DurationSeconds > 0 ? FormatTime(Playback.DurationSeconds) : "";
        OnQueueModesChanged();
        UpdateTaskbar();

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
        if (ContentFrame.Content is NowPlayingPage)
        {
            GoBack();
        }
        else if (Playback.CurrentSong is not null)
        {
            ContentFrame.Navigate(typeof(NowPlayingPage), "queue", new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
        }
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

    /// <summary>The cover or title opens Now Playing, drilling in; tapped again it closes.</summary>
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

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        Playback.SetMuted(!Playback.IsMuted);
        MuteIcon.Glyph = Playback.IsMuted ? "" : "";
        ToolTipService.SetToolTip(MuteButton, Playback.IsMuted ? "Unmute" : "Mute");
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
        // Under 760 the volume goes speaker-only.
        VolumeSlider.Visibility = e.NewSize.Width < 760 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The title and artist take the room the cover, the gaps and the heart leave;
    /// past that they trim, so the heart stays beside the words instead of at the cell's edge.</summary>
    private void OnTrackInfoSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TrackInfoText.MaxWidth = Math.Max(80, e.NewSize.Width - 56 - 36 - 36);
    }

    // MARK: profiles

    /// <summary>The title bar's account chip: the active channel, and the way to switch
    /// account or channel.</summary>
    private void UpdateProfileChip()
    {
        var roster = Koda.Services.Profiles.Roster;
        var active = roster.Active;
        TitleProfilePicture.DisplayName = active.ListeningName;
        TitleProfilePicture.ProfilePicture = active.ListeningPhoto is { } photo && Uri.TryCreate(photo, UriKind.Absolute, out var url)
            ? new BitmapImage(url) { DecodePixelWidth = 52 }
            : null;
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
        VideoSubscriptionsItem.Visibility = VideoChannelsItem.Visibility = VideoLaterItem.Visibility = VideoLikedItem.Visibility = account;

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

    /// <summary>The watch page filling the screen: no title bar, sidebar or bar around it.</summary>
    public void SetVideoFullScreen(bool on)
    {
        if (_videoFullScreen == on)
        {
            return;
        }

        _videoFullScreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        AppTitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Root.RowDefinitions[0].Height = new GridLength(on ? 0 : TitleBarHeight);
        ApplySidebar();
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

    /// <summary>The sidebar shown or hidden entirely, as the listener left it; full screen
    /// always hides it. The title bar's button says which way it will go.</summary>
    private void ApplySidebar()
    {
        var hidden = App.Settings.HidesSidebar;
        var visible = !hidden && !_videoFullScreen;
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
        Video.Flush();
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
                    _current.AppWindow.Hide();
                    return 0;
                }

                break;

            case Win32.WM_SIZE:
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
