using System;
using System.Runtime.InteropServices;
using Koda.Controls;

namespace Koda.Interop;

internal static class DisplayRefresh
{
    public static double For(VideoSurface? surface)
    {
        try
        {
            if (surface?.XamlRoot is not { } root) return double.NaN;
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
            var monitor = MonitorFromWindow(hwnd, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            var mode = new DisplayMode { Size = 220 };
            if (GetMonitorInfoW(monitor, ref info) && EnumDisplaySettingsW(info.Device, -1, ref mode) && mode.Frequency > 1)
                return mode.Frequency;
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Display refresh: {error.Message}"); }
        return double.NaN;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Win32.RECT Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(184)] public uint Frequency;
    }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplaySettingsW(string device, int mode, ref DisplayMode settings);
}
