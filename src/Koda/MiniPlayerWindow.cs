using System;
using System.IO;
using Koda.Helpers;
using Koda.Interop;
using Koda.Pages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Koda;

/// <summary>
/// The mini player as Windows knows it: a compact overlay, the same picture-in-picture
/// surface video apps use, so it floats above other windows with the system's own frame
/// and can be moved where the listener wants it. It is a second window, not a restyle of
/// the main one, so the library stays open behind it.
/// </summary>
public sealed class MiniPlayerWindow : Window
{
    private const double CardWidth = 450;
    private const double CardHeight = 200; // 187.5 to keep the old proportion

    private readonly MiniPlayerPage _page;
    private bool _customTitleBar;
    private bool _frameApplied;

    public MiniPlayerWindow()
    {
        Title = "Koda";
        SystemBackdrop = new MicaBackdrop();

        _page = new MiniPlayerPage();
        Content = _page;
        Koda.Helpers.ShellChrome.Register(_page);

        var appWindow = AppWindow;
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        // Windows sizes a compact overlay from a preset, then lets the app make it its
        // own. A card, not a video frame: wide enough for the cover and the title, tall
        // enough for the seek bar and a full control row.
        var presenter = CompactOverlayPresenter.Create();
        presenter.InitialSize = CompactOverlaySize.Small;
        appWindow.SetPresenter(presenter);

        // No system title bar over the card: the page reaches the window's top edge and
        // its first strip is the drag region, so the artwork and Mica run edge to edge
        // instead of sitting under a white band. The caption buttons float above it.
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(_page.TitleBarControl);
            _customTitleBar = true;
        }
        catch (Exception)
        {
            // A shell that refuses to extend the content keeps the system bar; it is
            // painted to match the theme below rather than left white.
            _customTitleBar = false;
        }

        _page.ActualThemeChanged += (_, _) => PaintCaptionButtons();
        PaintCaptionButtons();

        ResizeCard();

        // The compact presenter restyles the window after the constructor; apply the
        // frame theme once the window is really on screen.
        Activated += (_, _) =>
        {
            if (_frameApplied)
            {
                return;
            }

            _frameApplied = true;
            PaintCaptionButtons();
        };

        _page.FullPlayerRequested += OnFullPlayerRequested;
        Closed += (_, _) =>
        {
            _page.FullPlayerRequested -= OnFullPlayerRequested;
            _page.Detach();
        };
    }

    /// <summary>The window handle, for the DPI-aware size.</summary>
    private nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>The card's size in effective pixels, scaled to the monitor's DPI.</summary>
    private void ResizeCard()
    {
        var scale = Win32.GetDpiForWindow(Handle) / 96.0;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1;
        }

        try
        {
            AppWindow.Resize(new SizeInt32(
                (int)Math.Round(CardWidth * scale),
                (int)Math.Round(CardHeight * scale)));
        }
        catch (Exception)
        {
            // The preset is a perfectly good mini player if the shell refuses the size.
        }
    }

    /// <summary>The caption button floats on the page's background, so it takes its colors
    /// from the theme like the rest of the card.</summary>
    private void PaintCaptionButtons()
    {
        if (AppWindow?.TitleBar is not { } bar)
        {
            return;
        }

        var dark = CaptionChrome.IsDark(_page.ActualTheme);
        var foreground = CaptionChrome.Foreground(dark);
        var hover = CaptionChrome.HoverBackground(dark);
        var pressed = CaptionChrome.PressedBackground(dark);

        if (_customTitleBar)
        {
            bar.BackgroundColor = Colors.Transparent;
            bar.InactiveBackgroundColor = Colors.Transparent;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        else
        {
            // The system bar is still there: give it the theme's own background instead
            // of the white it would otherwise use in a light-themed shell.
            var background = CaptionChrome.BarBackground(dark);
            bar.BackgroundColor = background;
            bar.InactiveBackgroundColor = background;
            bar.ButtonBackgroundColor = background;
            bar.ButtonInactiveBackgroundColor = background;
        }

        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = CaptionChrome.InactiveForeground(dark);
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = pressed;
        bar.ButtonPressedForegroundColor = foreground;

        _page.SetCaptionButtonColors(foreground, hover, pressed);

        // The window frame belongs to DWM, not XAML: without this it can stay in the
        // light frame while the card is dark, and the rounded top corners catch the
        // light as two white dots. Keep the frame in the card's mode.
        var immersiveDark = dark ? 1u : 0u;
        _ = Win32.DwmSetWindowAttribute(
            Handle, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref immersiveDark, sizeof(uint));
    }

    /// <summary>Leave the overlay and show the full player, optionally with one of its
    /// side panels open.</summary>
    private void OnFullPlayerRequested(string? panel)
    {
        Close();
        App.Shell?.ShowNowPlaying(panel);
    }
}
