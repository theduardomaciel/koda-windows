using System;
using System.IO;
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
        // own. A strip, not a video frame: wide enough for a title, short enough to sit
        // over a browser without hiding it.
        var presenter = CompactOverlayPresenter.Create();
        presenter.InitialSize = CompactOverlaySize.Small;
        appWindow.SetPresenter(presenter);

        // No system title bar over the strip: the page reaches the window's top edge and
        // its first strip is the drag region, so the artwork and Mica run edge to edge
        // instead of sitting under a white band. Only the caption button floats above it.
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

        try
        {
            appWindow.Resize(new SizeInt32(420, 156));
        }
        catch (Exception)
        {
            // The preset is a perfectly good mini player if the shell refuses the size.
        }

        _page.RestoreRequested += OnRestoreRequested;
        Closed += (_, _) =>
        {
            _page.RestoreRequested -= OnRestoreRequested;
            _page.Detach();
        };
    }

    /// <summary>The caption button floats on the page's background, so it takes its colors
    /// from the theme like the rest of the strip.</summary>
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

    /// <summary>Leave the overlay and bring the library forward.</summary>
    private void OnRestoreRequested()
    {
        Close();
        App.Shell?.ShowFromTray();
    }
}
