using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Koda.Helpers;

/// <summary>
/// Lets a card's hover background fill its whole cell. The presenter insets the pointer-over
/// fill by 4,2,4,2, and on Windows 11 the rounded chrome draws it with an equally inset
/// backplate, which leaves a visible gap even when the item cells touch.
/// <c>helpers:CardHover.FillCell="True"</c>, usually from a style setter.
/// </summary>
public static class CardHover
{
    public static readonly DependencyProperty FillCellProperty = DependencyProperty.RegisterAttached(
        "FillCell", typeof(bool), typeof(CardHover), new PropertyMetadata(false, OnFillCellChanged));

    public static bool GetFillCell(DependencyObject d) => (bool)d.GetValue(FillCellProperty);
    public static void SetFillCell(DependencyObject d, bool value) => d.SetValue(FillCellProperty, value);

    private static void OnFillCellChanged(DependencyObject d, DependencyPropertyChangedEventArgs args)
    {
        if (d is not SelectorItem item)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            item.Loaded += OnLoaded;
        }
        else
        {
            item.Loaded -= OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is SelectorItem item && Descendant<ListViewItemPresenter>(item) is { } presenter)
        {
            presenter.PointerOverBackgroundMargin = new Thickness(0);

            // The rounded chrome's backplate is a Border the presenter inserts below its content.
            if (FindBackplate(presenter) is { } backplate)
            {
                backplate.Margin = new Thickness(0);
            }
        }
    }

    private static Border? FindBackplate(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border { IsHitTestVisible: false } border &&
                border.Margin is { Left: > 1, Top: > 0.5, Right: > 1, Bottom: > 0.5 })
            {
                return border;
            }

            if (FindBackplate(child) is { } deeper)
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
