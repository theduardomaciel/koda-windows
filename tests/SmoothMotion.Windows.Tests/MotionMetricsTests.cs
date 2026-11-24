using Xunit;

namespace SmoothMotion.Windows.Tests;

public class MotionMetricsTests
{
    [Fact]
    public void RatesCountAcceptedPresentsAndSeparateIntermediateRendersAndDrops()
    {
        var metrics = new MotionMetrics();
        metrics.Reset(0);
        for (int i = 0; i < 24; i++) metrics.Capture(false);
        metrics.Capture(true);
        for (int i = 0; i < 120; i++) metrics.Present(true, i % 5 != 0, 0, 0.25);
        metrics.Present(false, true, 2, 1);
        var sample = metrics.Sample(1);
        Assert.Equal(24, sample.InputFps);
        Assert.Equal(120, sample.OutputFps);
        Assert.Equal(96, sample.IntermediateFps);
        Assert.Equal(120, sample.Presented);
        Assert.Equal(1, sample.CaptureDrops);
        Assert.Equal(3, sample.MissedTicks);
        Assert.Equal(0.25, sample.SubmitCpuMs, 5);
        var paused = metrics.Sample(2);
        Assert.Equal(0, paused.InputFps);
        Assert.Equal(0, paused.OutputFps);
        metrics.Reset(3);
        Assert.Equal(0, metrics.Sample(4).Presented);
    }

    [Fact]
    public void CountersAcceptConcurrentCaptureAndPresentation()
    {
        var metrics = new MotionMetrics(); metrics.Reset(0);
        Parallel.For(0, 1000, _ => { metrics.Capture(false); metrics.Present(true, true, 0, 0.1); });
        Assert.Equal(1000, metrics.Sample(1).Presented);
        Assert.Equal(1000, metrics.Sample(1).Captured);
    }

    [Fact]
    public void TheHighResolutionTimerCanBeCancelledWithoutWaitingForItsDeadline()
    {
        using var timer = new FrameTimer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False(timer.Wait(1, cancellation.Token));
    }
}
