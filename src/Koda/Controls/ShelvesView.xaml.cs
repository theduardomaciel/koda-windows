using System;
using System.Collections.Generic;
using System.Linq;
using Koda.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Koda.Controls;

/// <summary>Rows of covers (Home, a mood, an artist's releases): a tap opens or plays, right-click on a song offers its menu.</summary>
public sealed partial class ShelvesView : UserControl
{
    /// <summary>The inset of each shelf's title and covers from this control's edges.</summary>
    public static readonly DependencyProperty ContentInsetProperty = DependencyProperty.Register(
        nameof(ContentInset), typeof(double), typeof(ShelvesView),
        new PropertyMetadata(32d, OnContentInsetChanged));

    public ShelvesView()
    {
        InitializeComponent();
    }

    public IReadOnlyList<ShelfView> Shelves
    {
        get => ShelfList.ItemsSource as IReadOnlyList<ShelfView> ?? [];
        set => ShelfList.ItemsSource = value;
    }

    /// <summary>
    /// The inset of each shelf's title and covers from this control's edges. The default
    /// matches the full-bleed shelves on Home; a page that already pads its content sets
    /// this to <c>0</c> so its shelves share that same margin.
    /// </summary>
    public double ContentInset
    {
        get => (double)GetValue(ContentInsetProperty);
        set => SetValue(ContentInsetProperty, value);
    }

    private static void OnContentInsetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ShelvesView)sender).ApplyContentInset();

    /// <summary>Lay out each realized shelf so its title and covers both land on <see cref="ContentInset"/>.</summary>
    private void ApplyContentInset()
    {
        foreach (var list in Lists(ShelfList))
        {
            ApplyContentInset(list);
        }
    }

    private void ApplyContentInset(ListView list)
    {
        if (FindShelfRow(list) is not { } row)
        {
            return;
        }

        var inset = Math.Max(0, ContentInset);
        // CardItemStyle's Padding and right Margin; see docs/card-layout.md.
        var left = Outer(inset, 12);
        var right = Outer(inset, 9);
        list.Padding = new Thickness(left.Padding, 0, right.Padding, 0);
        list.Margin = new Thickness(left.Margin, 0, right.Margin, 0);

        if (Find(row, "ShelfTitle") is FrameworkElement title)
        {
            title.Margin = new Thickness(inset, 0, 0, 0);
        }

        if (Find(row, "ShelfButtons") is FrameworkElement buttons)
        {
            buttons.Margin = new Thickness(0, 0, inset, 0);
        }
    }

    /// <summary>The outer padding, or a margin when the hover bleed is wider than the inset.</summary>
    private static (double Padding, double Margin) Outer(double inset, double itemInset)
    {
        var outer = inset - itemInset;
        return outer >= 0 ? (outer, 0) : (0, outer);
    }

    private static IEnumerable<ListView> Lists(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListView list)
            {
                yield return list;
            }

            foreach (var nested in Lists(child))
            {
                yield return nested;
            }
        }
    }

    private void OnCardClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is CardItem card && sender is ListView { DataContext: ShelfView shelf })
        {
            Navigator.Open(card.Item, shelf.Songs);
        }
    }

    private void OnShelfLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is ListView list && list.Tag is null)
        {
            list.Tag = "menu";
            SongMenu.Attach(list, () => list.DataContext is ShelfView shelf ? shelf.Songs : []);
            AlbumMenu.Attach(list);
            ArtistMenu.Attach(list);
            PlaylistMenu.Attach(list);
            ApplyContentInset(list);
            list.AddHandler(PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnShelfWheel), true);

            if (FindShelfRow(list) is { } row && Descendant<ScrollViewer>(list) is { } scroller)
            {
                // Extent grows as covers are realized, so watch it as well as the offset.
                scroller.RegisterPropertyChangedCallback(ScrollViewer.ScrollableWidthProperty, (_, _) => SyncButtons(row, scroller));
                scroller.ViewChanged += (_, _) => SyncButtons(row, scroller);
                scroller.SizeChanged += (_, _) => SyncButtons(row, scroller);
                SyncButtons(row, scroller);
            }
        }
    }

    /// <summary>Turns off a step button once its end of the shelf is reached.</summary>
    private static void SyncButtons(Grid row, ScrollViewer scroller)
    {
        if (Find(row, "StepLeftButton") is not Button left || Find(row, "StepRightButton") is not Button right)
        {
            return;
        }

        left.IsEnabled = scroller.HorizontalOffset > 0.5;
        right.IsEnabled = scroller.ScrollableWidth - scroller.HorizontalOffset > 0.5;
    }

    /// <summary>
    /// [judgement] A shelf never steals the page's scroll: its own scrolling is off for pointer
    /// input, so the plain wheel passes to the page. Shift+wheel and a sideways swipe (a
    /// horizontal wheel) move the shelf instead, as in File Explorer and the Store.
    /// </summary>
    private static void OnShelfWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (sender is not ListView list || Descendant<ScrollViewer>(list) is not { } scroller)
        {
            return;
        }

        var point = args.GetCurrentPoint(list).Properties;
        var shift = (args.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
        if (!point.IsHorizontalMouseWheel && !shift)
        {
            return; // the page scrolls
        }

        var delta = point.MouseWheelDelta;
        // A horizontal wheel reports right as positive; a vertical one reports down as negative.
        var step = point.IsHorizontalMouseWheel ? delta : -delta;
        scroller.ChangeView(System.Math.Max(0, scroller.HorizontalOffset + step * 1.5), null, null, true);
        args.Handled = true;
    }

    private void OnStepLeft(object sender, RoutedEventArgs args) => Step(sender, -1);

    private void OnStepRight(object sender, RoutedEventArgs args) => Step(sender, 1);

    private static void Step(object sender, int direction)
    {
        if (sender is not FrameworkElement button || FindShelfList(button) is not { } list || Descendant<ScrollViewer>(list) is not { } scroller)
        {
            return;
        }

        var target = scroller.HorizontalOffset + direction * scroller.ViewportWidth * 0.85;
        scroller.ChangeView(System.Math.Max(0, target), null, null);
    }

    private static ListView? FindShelfList(FrameworkElement from) =>
        FindShelfRow(from)?.Children.OfType<ListView>().FirstOrDefault();

    private static Grid? FindShelfRow(FrameworkElement from)
    {
        DependencyObject? node = from;
        while (node is not null && node is not Grid { RowDefinitions.Count: 2 })
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return node as Grid;
    }

    private static FrameworkElement? Find(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var found } element && found == name)
            {
                return element;
            }

            if (Find(child, name) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }

        return null;
    }
}
