using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SmoothMotion.Windows;
using Windows.Media.Playback;

namespace Koda.Controls;

/// <summary>The native picture with an optional GPU presenter, shared by the page, bar and PiP.</summary>
public sealed partial class VideoSurface : Grid
{
    private readonly MediaPlayerElement _native = new() { AreTransportControlsEnabled = false };
    private readonly SwapChainPanel _panel = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private MotionEngine? _engine;
    internal event Action<Exception>? PresenterFailed;

    public VideoSurface()
    {
        IsHitTestVisible = false;
        Children.Add(_native);
        Children.Add(_panel);
        SizeChanged += (_, _) => ResizePresenter();
    }

    public bool AreTransportControlsEnabled { get => _native.AreTransportControlsEnabled; set => _native.AreTransportControlsEnabled = value; }
    public Stretch Stretch { get => _native.Stretch; set { _native.Stretch = value; ResizePresenter(); } }
    public void SetMediaPlayer(MediaPlayer? player) => _native.SetMediaPlayer(player);

    internal void SetPresenter(MotionEngine? engine)
    {
        if (engine is null)
        {
            _engine = null;
            _panel.Visibility = Visibility.Collapsed;
            _native.Visibility = Visibility.Visible;
        }
        // WinUI's interface differs from Windows.UI.Xaml's interface with the same name.
        var native = WinRT.CastExtensions.As<ISwapChainPanelNative>(_panel);
        Marshal.ThrowExceptionForHR(native.SetSwapChain(engine?.SwapChain ?? 0));
        _engine = engine;
        _panel.Visibility = engine is null ? Visibility.Collapsed : Visibility.Visible;
        _native.Visibility = engine is null ? Visibility.Visible : Visibility.Collapsed;
        ResizePresenter();
    }

    private void ResizePresenter()
    {
        Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight) };
        if (_engine is not { } engine || ActualWidth <= 0 || ActualHeight <= 0) return;
        var scale = Stretch == Stretch.UniformToFill
            ? Math.Max(ActualWidth / engine.Width, ActualHeight / engine.Height)
            : Math.Min(ActualWidth / engine.Width, ActualHeight / engine.Height);
        _panel.Width = engine.Width * scale;
        _panel.Height = engine.Height * scale;
        try { lock (engine.SyncRoot) engine.SetPresentationSize((float)_panel.Width, (float)_panel.Height); }
        catch (Exception error) { PresenterFailed?.Invoke(error); }
    }

    [ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig] int SetSwapChain(nint swapChain);
    }
}
