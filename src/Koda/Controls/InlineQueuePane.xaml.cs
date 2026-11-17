using System;
using System.Collections.ObjectModel;
using System.Linq;
using Koda.Helpers;
using Koda.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Koda.Controls;

/// <summary>
/// The inline queue drawer's body: Playing Next and History as two views of the same
/// play queue, with Clear, Autoplay and drag-to-reorder where they belong.
/// </summary>
public sealed partial class InlineQueuePane : UserControl
{
    private readonly ObservableCollection<QueueItem> _upcoming = [];
    private readonly ObservableCollection<QueueItem> _history = [];

    private bool _active;
    private bool _historyTab;
    private bool _dragging;
    private QueueItem? _dragged;
    private int _dragFrom;

    private PlaybackService Playback => PlaybackService.Instance;

    public event Action? CloseRequested;

    public InlineQueuePane()
    {
        InitializeComponent();
        UpNext.ItemsSource = _upcoming;
        HistoryList.ItemsSource = _history;
        Loaded += OnLoaded;
    }

    /// <summary>Drops the drawer's own chrome for a host that provides it: no side divider,
    /// no surface of its own, and no tab or close rows, leaving the Playing Next list under
    /// a slim Clear/Autoplay bar. History stays a drawer-only view.</summary>
    public void UseCompactChrome()
    {
        Surface.Background = null;
        Surface.BorderThickness = new Thickness(0);
        Tabs.Visibility = Visibility.Collapsed;
        TabsRow.Height = new GridLength(0);
        CloseButton.Visibility = Visibility.Collapsed;
    }

    public void Activate()
    {
        if (!_active)
        {
            _active = true;
            Playback.QueueChanged += OnQueueChanged;
            Playback.TrackChanged += OnQueueChanged;
        }

        Render();
    }

    public void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        Playback.QueueChanged -= OnQueueChanged;
        Playback.TrackChanged -= OnQueueChanged;
    }

    private void OnQueueChanged() => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        if (!_dragging)
        {
            var upcoming = Playback.Queue.Upcoming;
            // Only what changed: the list keeps its scroll while autoplay appends.
            var same = upcoming.Count >= _upcoming.Count
                && _upcoming.Select((item, index) => upcoming[index].Id == item.Id).All(match => match);
            if (same)
            {
                foreach (var item in upcoming.Skip(_upcoming.Count))
                {
                    _upcoming.Add(item);
                }
            }
            else
            {
                _upcoming.Clear();
                foreach (var item in upcoming)
                {
                    _upcoming.Add(item);
                }
            }

            _history.Clear();
            foreach (var item in Playback.Queue.History)
            {
                _history.Add(item);
            }
        }

        AutoplayToggle.IsChecked = Playback.Autoplay;
        SectionTitle.Text = _historyTab ? "History" : "Playing Next";
        ClearButton.Visibility = !_historyTab && _upcoming.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpNext.Visibility = _historyTab ? Visibility.Collapsed : Visibility.Visible;
        HistoryList.Visibility = _historyTab ? Visibility.Visible : Visibility.Collapsed;

        var count = _historyTab ? _history.Count : _upcoming.Count;
        EmptyText.Text = _historyTab ? "Nothing played yet." : "Nothing up next.";
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateTabs();
    }

    private void UpdateTabs()
    {
        PlayingNextTab.IsChecked = !_historyTab;
        HistoryTab.IsChecked = _historyTab;
    }

    private void OnPlayingNextClick(object sender, RoutedEventArgs e)
    {
        _historyTab = false;
        Render();
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        _historyTab = true;
        Render();
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QueueItem item)
        {
            _ = Playback.JumpAsync(item.Id);
        }
    }

    private void OnItemContext(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { DataContext: QueueItem item }
            || sender is not FrameworkElement host)
        {
            return;
        }

        var menu = SongMenu.Build(item.Song, [.. Playback.Queue.Items.Select(i => i.Song)]);
        var remove = new MenuFlyoutItem { Text = "Remove from queue", Icon = new FontIcon { Glyph = "\uE711" } };
        remove.Click += (_, _) => Playback.Remove(item.Id);
        menu.Items.Insert(0, new MenuFlyoutSeparator());
        menu.Items.Insert(0, remove);
        if (args.TryGetPosition(host, out var at))
        {
            menu.ShowAt(host, at);
        }
        else
        {
            menu.ShowAt(host);
        }

        args.Handled = true;
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => Playback.ClearUpcoming();

    private void OnAutoplayClick(object sender, RoutedEventArgs e) =>
        Playback.SetAutoplay(AutoplayToggle.IsChecked == true);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragging = true;
        _dragged = e.Items.FirstOrDefault() as QueueItem;
        _dragFrom = _dragged is null ? -1 : _upcoming.IndexOf(_dragged);
    }

    private void OnDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _dragging = false;
        if (_dragged is { } item && _dragFrom >= 0
            && _upcoming.IndexOf(item) is var to and >= 0 && to != _dragFrom)
        {
            Playback.MoveUpcoming(_dragFrom, to);
        }

        _dragged = null;
        Render();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Descendant<ScrollViewer>(UpNext) is not { } scroller)
        {
            return;
        }

        // Near the end of the list, Autoplay fetches more: the queue never runs dry.
        scroller.ViewChanged += (_, _) =>
        {
            if (_active && !_historyTab && scroller.ScrollableHeight > 0
                && scroller.VerticalOffset > scroller.ScrollableHeight - 400)
            {
                _ = Playback.LoadMoreAsync();
            }
        };
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (Descendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
