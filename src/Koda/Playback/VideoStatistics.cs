using System;
using Windows.Media.Playback;

namespace Koda.Playback;

internal static class VideoStatistics
{
    // Windows can reject this optional getter with E_NOINTERFACE, projected as an
    // InvalidCastException. Letting it escape a DispatcherQueue callback is fatal.
    internal static double BufferProgress(MediaPlaybackSession session, Action<Exception>? unavailable = null)
    {
        try { return session.BufferingProgress; }
        catch (Exception error)
        {
            unavailable?.Invoke(error);
            return double.NaN;
        }
    }
}
