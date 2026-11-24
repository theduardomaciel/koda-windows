using System;
using System.IO;
using Koda.Helpers;
using Koda.Interop;
using Koda.Pages;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Koda;

/// <summary>
/// The mini player as Windows knows it: a compact overlay, the same picture-in-picture
/// surface video apps use, so it floats above other windows with the system's own frame
/// and can be moved where the listener wants it. It is a second window, not a restyle of
/// the main one, so the library stays open behind it. The lyrics and queue grow it into a
/// taller card and collapsing returns it, at the same spot, to the compact one.
/// </summary>
public sealed class MiniPlayerWindow : Window
{
    private const double PreferredCardWidth = 425;
    private const double CompactCardHeight = 200;

    /// <summary>The expanded card keeps the reference player's portrait 6:11 shape.</summary>
    private const double ExpandedAspectWidth = 6;
    private const double ExpandedAspectHeight = 11;

    /// <summary>Narrower than this, the transport and the two side clusters would collide.</summary>
    private const double MinCardWidth = 360;

    private readonly MiniPlayerPage _page;
    private bool _customTitleBar;
    private bool _frameApplied;

    /// <summary>Re-fits the compact card once a move to another display has settled.</summary>
    private readonly DispatcherQueueTimer _widthFit;

    /// <summary>Where the compact card sat before it grew, so collapsing puts it back.</summary>
    private PointInt32 _compactPosition;

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

        // A move to another display can change the width the 6:11 expanded card needs;
        // the compact card re-fits once the move has settled.
        _widthFit = DispatcherQueue.CreateTimer();
        _widthFit.Interval = TimeSpan.FromMilliseconds(250);
        _widthFit.IsRepeating = false;
        _widthFit.Tick += (_, _) => ResizeCard();
        appWindow.Changed += OnAppWindowChanged;

        // No system title bar over the card: the page reaches the window's top edge and
        // its first strip is the drag region, so the artwork and Mica run edge to edge
        // instead of sitting under a white band. The caption buttons float above it.
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(_page.TitleBarControl);
            // WinUI's TitleBar misreads the caption inset at non-100% DPI; see TitleBarInsets.
            TitleBarInsets.Repair(_page.TitleBarControl, this);
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

        _page.ExpandedChanged += OnExpandedChanged;
        _page.FullPlayerRequested += OnFullPlayerRequested;
        Closed += (_, _) =>
        {
            AppWindow.Changed -= OnAppWindowChanged;
            _widthFit.Stop();
            _page.ExpandedChanged -= OnExpandedChanged;
            _page.FullPlayerRequested -= OnFullPlayerRequested;
            _page.Detach();
        };
    }

    /// <summary>The window handle, for the DPI-aware size.</summary>
    private nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>The compact card, fitted to its display: it takes the width the 6:11
    /// expanded card needs there even before a panel opens, so expanding never changes the
    /// card's width.</summary>
    private void ResizeCard()
    {
        if (_page.OpenPanel is not null)
        {
            return; // the expanded size owns the window
        }

        try
        {
            var scale = DpiScale();
            if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest) is not { } area)
            {
                return;
            }

            var size = CompactSize(area.WorkArea, scale);
            if (AppWindow.Size.Width == size.Width && AppWindow.Size.Height == size.Height)
            {
                return;
            }

            AppWindow.Resize(size);
        }
        catch (Exception)
        {
            // The preset is a perfectly good mini player if the shell refuses the size.
        }
    }

    private static SizeInt32 CompactSize(RectInt32 work, double scale) => new(
        (int)Math.Round(FitCardWidth(work, scale) * scale),
        (int)Math.Round(CompactCardHeight * scale));

    /// <summary>The width the card uses on a display: the preferred width, unless the
    /// screen is too short to hold the 6:11 expanded card at it. Fitting it from the start
    /// means the card is already the right width before a panel opens.</summary>
    private static double FitCardWidth(RectInt32 work, double scale)
    {
        var workHeight = work.Height / scale;
        var fit = workHeight * ExpandedAspectWidth / ExpandedAspectHeight;
        return Math.Clamp(Math.Min(PreferredCardWidth, fit), MinCardWidth, PreferredCardWidth);
    }

    /// <summary>A moved card may have crossed to a display that cannot hold the expanded
    /// card at the current width; once the drag settles, the compact card re-fits.</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange || _page.OpenPanel is not null)
        {
            return;
        }

        _widthFit.Stop();
        _widthFit.Start();
    }

    private double DpiScale()
    {
        var scale = Win32.GetDpiForWindow(Handle) / 96.0;
        return double.IsFinite(scale) && scale > 0 ? scale : 1;
    }

    /// <summary>Grows or shrinks the card for the page's side panel. If the shell refuses
    /// the taller overlay, the panel opens in the full player instead, as it used to.</summary>
    private void OnExpandedChanged(bool expanded)
    {
        if (TryResizeCard(expanded))
        {
            // A resized card moves the caption group and the passthrough area beside it;
            // the title bar re-reads them once the new size has laid out.
            DispatcherQueue.TryEnqueue(() => _page.TitleBarControl.RecomputeDragRegions());
            return;
        }

        if (!expanded)
        {
            return;
        }

        var panel = _page.OpenPanel;
        Close();
        App.Shell?.ShowNowPlaying(panel);
    }

    /// <summary>Resizes the card for the expanded panel, keeping the window on its screen.
    /// The compact and expanded cards share one width, so opening a panel only adds height.
    /// The card grows upward when the taller version would run past the work area, so an
    /// overlay parked near the taskbar keeps its panel in view.</summary>
    private bool TryResizeCard(bool expanded)
    {
        try
        {
            var scale = DpiScale();
            if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest) is not { } area)
            {
                return false;
            }

            var work = area.WorkArea;
            var width = (int)Math.Round(FitCardWidth(work, scale) * scale);
            var height = expanded
                ? (int)Math.Round(width * ExpandedAspectHeight / ExpandedAspectWidth)
                : (int)Math.Round(CompactCardHeight * scale);

            if (expanded && height > work.Height)
            {
                // The screen is below the width the controls need; the ratio has to give so
                // that the card stays on screen.
                height = work.Height;
                width = Math.Max(
                    (int)Math.Round(height * ExpandedAspectWidth / ExpandedAspectHeight),
                    (int)Math.Round(MinCardWidth * scale));
            }

            int x;
            int y;
            if (expanded)
            {
                // Remember the compact spot now, before the card moves or grows.
                _compactPosition = AppWindow.Position;
                x = _compactPosition.X;
                // Keep the top where it is while the taller card fits; otherwise the bottom
                // edge stays put and the card grows upward.
                y = Math.Clamp(_compactPosition.Y, work.Y, work.Y + work.Height - height);
            }
            else
            {
                x = _compactPosition.X;
                y = _compactPosition.Y;
            }

            // A display change or a moved card can leave the remembered spot outside the work
            // area; the card always comes back fully on screen.
            x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));

            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            return true;
        }
        catch (Exception)
        {
            return false;
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
