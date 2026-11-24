using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Koda.Helpers;

/// <summary>
/// The hand pointer over everything clickable, as on the web: buttons, rows, cards, sidebar
/// items, menu items, toggles, sliders and dropdowns, plus surfaces marked with
/// <c>helpers:PointerCursors.IsClickable="True"</c>. Text fields keep their text cursor, and a
/// disabled control keeps the arrow.
///
/// This is the owner's choice and goes against Microsoft's guidance ("Mouse interactions":
/// keep the arrow on clickable elements and show them with hover effects), so it lives here
/// in one place.
///
/// WinUI 3 sets a pointer per element through the protected <c>UIElement.ProtectedCursor</c>
/// and offers no app-wide switch. Pointer moves are watched at each window's root and in every
/// popup (menus, flyouts, dialogs live outside the window's content), and the clickable element
/// under the pointer is given the hand once; from then on WinUI shows it there by itself.
/// </summary>
public static class PointerCursors
{
    public static readonly DependencyProperty IsClickableProperty = DependencyProperty.RegisterAttached(
        "IsClickable", typeof(bool), typeof(PointerCursors), new PropertyMetadata(false));

    public static bool GetIsClickable(DependencyObject d) => (bool)d.GetValue(IsClickableProperty);
    public static void SetIsClickable(DependencyObject d, bool value) => d.SetValue(IsClickableProperty, value);

    /// <summary>ProtectedCursor is protected; this sets it on elements Koda didn't derive.</summary>
    private static readonly PropertyInfo? Cursor = typeof(UIElement).GetProperty(
        "ProtectedCursor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static readonly Lazy<InputCursor> Hand = new(() => InputSystemCursor.Create(InputSystemCursorShape.Hand));

    private static readonly ConditionalWeakTable<UIElement, object> Watched = new();
    private static readonly PointerEventHandler Moved = OnPointerMoved;

    /// <summary>Watch a window's content, and the popups that open over it.</summary>
    public static void Attach(UIElement root)
    {
        Watch(root);
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.Tick += (_, _) =>
        {
            if (root.XamlRoot is not { } xamlRoot) return;
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                if (popup.Child is { } child) Watch(child);
            }
        };
        timer.Start();

        // A window that closes (the mini player, picture in picture) stops looking for popups.
        if (root is FrameworkElement element)
        {
            element.Unloaded += (_, _) => timer.Stop();
            element.Loaded += (_, _) => timer.Start();
        }
    }

    private static void Watch(UIElement element)
    {
        if (Watched.TryGetValue(element, out _)) return;
        Watched.Add(element, new object());
        element.AddHandler(UIElement.PointerMovedEvent, Moved, handledEventsToo: true);
    }

    private static void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Cursor is null || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            // Text keeps the text cursor its control sets.
            if (node is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox) return;
            if (node is UIElement element && IsClickable(element))
            {
                Apply(element);
                return;
            }

            if (ReferenceEquals(node, sender)) return;
        }
    }

    private static bool IsClickable(UIElement element) => element switch
    {
        ButtonBase or ToggleSwitch or ComboBox or SelectorItem or MenuFlyoutItemBase
            or NavigationViewItem or Slider or RatingControl or PipsPager => true,
        _ => GetIsClickable(element),
    };

    private static void Apply(UIElement element)
    {
        var enabled = element is not Control { IsEnabled: false };
        var wanted = enabled ? Hand.Value : null;
        if (!ReferenceEquals(Cursor!.GetValue(element), wanted)) Cursor.SetValue(element, wanted);
    }
}
