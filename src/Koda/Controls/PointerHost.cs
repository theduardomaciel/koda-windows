using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Koda.Controls;

/// <summary>
/// A Grid that can hide the pointer while it is over it, as a video player does once its
/// controls fade. WinUI 3 sets the pointer per element through <c>ProtectedCursor</c>, which only
/// a subclass can reach; the blank shape is a cursor resource in Koda.exe (see the
/// KodaAddBlankCursor target in Koda.csproj), since there is no system "none" shape.
/// </summary>
public sealed partial class PointerHost : Grid
{
    /// <summary>The resource name the build gives the transparent cursor in Koda.exe.</summary>
    public const string BlankCursorName = "KODA_BLANK";

    private static InputCursor? _blank;
    private static bool _blankTried;

    /// <summary>Whether the pointer is hidden over this element. When the blank cursor can't be
    /// loaded the pointer simply stays.</summary>
    public bool IsPointerHidden
    {
        get => ProtectedCursor is not null && ProtectedCursor == _blank;
        set => ProtectedCursor = value ? Blank() : null;
    }

    private static InputCursor? Blank()
    {
        if (_blankTried) return _blank;
        _blankTried = true;

        // The resource is in the launcher, Koda.exe. Named outright first, by its path, then by
        // its file name, then left to the default module.
        var exe = Environment.ProcessPath;
        var attempts = new (string Label, Func<InputCursor> Make)[]
        {
            ("path", () => InputDesktopNamedResourceCursor.CreateFromModule(exe ?? "Koda.exe", BlankCursorName)),
            ("file", () => InputDesktopNamedResourceCursor.CreateFromModule(System.IO.Path.GetFileName(exe ?? "Koda.exe"), BlankCursorName)),
            ("default", () => InputDesktopNamedResourceCursor.Create(BlankCursorName)),
        };
        foreach (var (label, make) in attempts)
        {
            try
            {
                _blank = make();
                Playback.VideoTrace.Write($"blank cursor loaded ({label})");
                break;
            }
            catch (Exception error)
            {
                Playback.VideoTrace.Write($"blank cursor ({label}) failed: 0x{error.HResult:X8} {error.GetType().Name} {error.Message}");
            }
        }

        return _blank;
    }
}
