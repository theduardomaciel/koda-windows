using SmoothMotion.Windows;
using Xunit;

namespace SmoothMotion.Windows.Tests;

public class OutputClockTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(120)]
    [InlineData(144)]
    public void HighRefreshDisplaysKeepSixtyFpsWithoutFalseDrops(int refreshRate)
    {
        var clock = new OutputClock(60);
        int presented = 0, dropped = 0;
        for (int i = 0; i < refreshRate * 10; ++i)
        {
            if (!clock.TryTick(i / (double)refreshRate, out var missed)) continue;
            presented++; dropped += missed;
        }
        Assert.Equal(600, presented);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void StallsCountMissingTicksAndResetDoesNotCarryThemAcrossSeeks()
    {
        var clock = new OutputClock(60);
        Assert.True(clock.TryTick(0, out _));
        Assert.True(clock.TryTick(0.1, out var missed));
        Assert.Equal(5, missed);
        clock.Reset();
        Assert.True(clock.TryTick(20, out missed));
        Assert.Equal(0, missed);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    public void A120FpsClockKeepsItsCadenceOnHighRefreshDisplays(int refreshRate)
    {
        var clock = new OutputClock(120);
        int frames = 0, missed = 0;
        for (int i = 0; i < refreshRate * 10; i++)
        {
            if (!clock.TryTick(i / (double)refreshRate, out var dropped)) continue;
            frames++; missed += dropped;
        }
        Assert.Equal(1200, frames);
        Assert.Equal(0, missed);
    }

    [Theory]
    [InlineData(120, 60, 60)]
    [InlineData(120, 120, 120)]
    [InlineData(120, 144, 120)]
    [InlineData(60, 144, 60)]
    [InlineData(120, 0, 60)]
    public void OutputRespectsBothTheSettingAndTheDisplay(int cap, double display, double expected) =>
        Assert.Equal(expected, OutputClock.TargetRate(cap, display));
}
