using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Koda.Helpers;

/// <summary>
/// Works around the WinUI TitleBar control's caption padding at display scales other
/// than 100%. The control copies <see cref="AppWindowTitleBar.RightInset"/> - physical
/// pixels - straight into its template's RightPaddingColumn, which is measured in DIPs.
/// At 150% or 200% that column is too wide, pushing the right header and the centered
/// content away from the caption buttons.
/// Upstream issue, still open: https://github.com/microsoft/microsoft-ui-xaml/issues/10344
/// </summary>
internal static class TitleBarInsets
{
    /// <summary>
    /// Repairs the right padding after the bar loads, on window activation (a compact
    /// overlay's presenter can settle after the first layout), and whenever the XamlRoot
    /// changes (DPI or monitor changes). Call once after SetTitleBar for the control.
    /// </summary>
    public static void Repair(TitleBar titleBar, Window window)
    {
        XamlRoot? subscribedRoot = null;

        void Apply()
        {
            // A minimized window reports 0 or a meaningless inset; keep the last good width.
            if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            {
                return;
            }

            var scale = titleBar.XamlRoot?.RasterizationScale ?? 0;
            if (!double.IsFinite(scale) || scale <= 0)
            {
                return;
            }

            if (window.AppWindow.TitleBar is not { } bar)
            {
                return;
            }

            var inset = bar.RightInset / scale;
            if (!double.IsFinite(inset) || inset <= 0)
            {
                return;
            }

            // The default TitleBar template ends with its RightPaddingColumn. The control
            // only recomputes that column when the template is applied, so set it here.
            if (FindPart(titleBar, "PART_LayoutRoot") is not Grid root
                || root.ColumnDefinitions.Count == 0)
            {
                return;
            }

            var column = root.ColumnDefinitions[root.ColumnDefinitions.Count - 1];
            if (Math.Abs(column.Width.Value - inset) > 0.5)
            {
                column.Width = new GridLength(inset);
            }
        }

        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Apply();

        void Subscribe()
        {
            if (titleBar.XamlRoot is not { } root || ReferenceEquals(root, subscribedRoot))
            {
                return;
            }

            if (subscribedRoot is not null)
            {
                subscribedRoot.Changed -= OnRootChanged;
            }

            subscribedRoot = root;
            root.Changed += OnRootChanged;
        }

        titleBar.Loaded += (_, _) =>
        {
            Subscribe();
            Apply();

            // Let the presenter finish settling before the inset is read again.
            titleBar.DispatcherQueue.TryEnqueue(Apply);
        };

        // The inset is valid again once the window comes back from minimized.
        window.Activated += (_, _) => Apply();
    }

    private static FrameworkElement? FindPart(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            if (FindPart(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
