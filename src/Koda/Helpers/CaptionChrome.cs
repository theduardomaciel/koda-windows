using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Koda.Helpers;

/// <summary>
/// The native caption buttons' palette. Both windows paint Windows' title bar with these
/// values, and the mini player's own expand button takes its hover and pressed fills from
/// the same place, so there is one definition of what a caption button looks like.
/// </summary>
internal static class CaptionChrome
{
    public static Color Foreground(bool dark) => dark ? Colors.White : Colors.Black;

    public static Color InactiveForeground(bool dark) => dark
        ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0x80, 0, 0, 0);

    public static Color HoverBackground(bool dark) => dark
        ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0x10, 0, 0, 0);

    public static Color PressedBackground(bool dark) => dark
        ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0x08, 0, 0, 0);

    /// <summary>The system bar Koda paints when it cannot extend its content.</summary>
    public static Color BarBackground(bool dark) => dark
        ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
        : Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

    public static bool IsDark(ElementTheme theme) => theme != ElementTheme.Light;
}
