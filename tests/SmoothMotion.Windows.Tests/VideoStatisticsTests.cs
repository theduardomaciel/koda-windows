using Koda.Playback;
using Windows.Media.Playback;
using Xunit;

namespace SmoothMotion.Windows.Tests;

public class VideoStatisticsTests
{
    [Fact]
    public void AnUnsupportedWindowsBufferStatisticCannotEscapeToTheUiCallback()
    {
        using var player = new MediaPlayer();
        Exception? unsupported = null;
        var progress = VideoStatistics.BufferProgress(player.PlaybackSession, error => unsupported = error);
        if (unsupported is not null)
        {
            Assert.True(double.IsNaN(progress));
        }
        else
        {
            // Other Windows media providers can implement the optional statistic.
            Assert.InRange(progress, 0, 1);
        }
    }
}
