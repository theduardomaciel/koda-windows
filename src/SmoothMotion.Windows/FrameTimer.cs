using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SmoothMotion.Windows;

/// <summary>A high-resolution waitable timer for 8.33ms presentation without a busy loop.</summary>
[SupportedOSPlatform("windows")]
public sealed class FrameTimer : IDisposable
{
    private readonly EventWaitHandle _timer = new(false, EventResetMode.AutoReset);

    public FrameTimer()
    {
        var handle = CreateWaitableTimerExW(0, null, 2, 0x1F0003); // HIGH_RESOLUTION, TIMER_ALL_ACCESS
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _timer.SafeWaitHandle = new SafeWaitHandle(handle, true);
    }

    public bool Wait(double seconds, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested) return false;
        if (seconds <= 0) return true;
        long due = -Math.Max(1, (long)(seconds * 10_000_000));
        if (!SetWaitableTimer(_timer.SafeWaitHandle, ref due, 0, 0, 0, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return WaitHandle.WaitAny([_timer, cancellation.WaitHandle]) == 0;
    }

    public void Dispose() => _timer.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint callback, nint argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
}
