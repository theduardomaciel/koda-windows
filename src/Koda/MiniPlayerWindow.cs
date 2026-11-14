using System;
using System.IO;
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

    /// <summary>Windows' caption button width; the compact overlay only has Close, but
    /// the value is the same. Used when the title bar has not measured itself yet.</summary>
    private const double DefaultCaptionInset = 46;

    private readonly MiniPlayerPage _page;
    private bool _customTitleBar;

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
            SetTitleBar(_page.DragRegion);
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
        SyncCaptionInset();

        _page.Loaded += (_, _) => SyncCaptionInset();
        Activated += (_, _) => SyncCaptionInset();
        appWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange)
            {
                SyncCaptionInset();
            }
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

    /// <summary>The native caption buttons sit over the page, so the page reserves their
    /// width and the app's own full-player button sits to their left.</summary>
    private void SyncCaptionInset()
    {
        if (!_customTitleBar)
        {
            _page.SetCaptionInset(0);
            return;
        }

        var scale = _page.XamlRoot?.RasterizationScale ?? 1;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1;
        }

        // Before the title bar has measured itself RightInset is 0, and during a
        // presenter change it can report a value that has nothing to do with the
        // one close button. Keep the app's button next to Close in both cases.
        var inset = AppWindow.TitleBar.RightInset / scale;
        if (!double.IsFinite(inset) || inset < 32 || inset > 96)
        {
            inset = DefaultCaptionInset;
        }

        _page.SetCaptionInset(inset);
    }

    /// <summary>The caption button floats on the page's background, so it takes its colors
    /// from the theme like the rest of the card.</summary>
    private void PaintCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        var dark = _page.ActualTheme != ElementTheme.Light;
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
            var background = dark
                ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
                : Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
            bar.BackgroundColor = background;
            bar.InactiveBackgroundColor = background;
            bar.ButtonBackgroundColor = background;
            bar.ButtonInactiveBackgroundColor = background;
        }

        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark
            ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
        bar.ButtonHoverBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x08, 0, 0, 0);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }

    /// <summary>Leave the overlay and show the full player, optionally with one of its
    /// side panels open.</summary>
    private void OnFullPlayerRequested(string? panel)
    {
        Close();
        App.Shell?.ShowNowPlaying(panel);
    }
}
