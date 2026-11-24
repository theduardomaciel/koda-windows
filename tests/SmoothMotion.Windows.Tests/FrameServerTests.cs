using SmoothMotion.Windows;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Xunit;

namespace SmoothMotion.Windows.Tests;

/// <summary>Uses OS decoding and GPU surfaces without creating or activating any window.</summary>
public class FrameServerTests
{
    [Fact]
    public async Task FrameServerCanBeEnabledDuringPlaybackAndRedrawAfterAPausedSeek()
    {
        using var engine = new MotionEngine(256, 144, presentation: false);
        using var surface = WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(engine.InputSurface);
        using var player = new MediaPlayer { IsMuted = true };
        player.CommandManager.IsEnabled = false;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seeked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        bool seeking = false;
        void Frame(MediaPlayer sender, object args)
        {
            lock (gate)
            {
                try
                {
                    player.CopyFrameToVideoSurface(surface);
                    engine.SubmitFrame();
                    engine.RenderLatest();
                    if (seeking) seeked.TrySetResult(); else first.TrySetResult();
                }
                catch (Exception error)
                {
                    first.TrySetException(error); seeked.TrySetException(error);
                }
            }
        }
        player.MediaOpened += (_, _) => opened.TrySetResult();
        var clock = new MediaTimelineController();
        player.TimelineController = clock;
        player.Source = MediaSource.CreateFromUri(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "motion.webm")));
        clock.Resume();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        player.VideoFrameAvailable += Frame;
        player.IsVideoFrameServerEnabled = true;
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Pause();
            lock (gate) { engine.Reset(); seeking = true; }
            player.TimelineControllerPositionOffset = TimeSpan.FromSeconds(1);
            await seeked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (gate)
            {
                Assert.Contains(engine.ReadOutput().Where((_, i) => i % 4 != 3), c => c > 32);
            }
        }
        finally
        {
            clock.Pause(); player.VideoFrameAvailable -= Frame;
            lock (gate) { player.IsVideoFrameServerEnabled = false; player.Source = null; }
        }
    }

    [Theory]
    [InlineData("motion.mp4")]
    [InlineData("motion.webm")]
    public async Task DecodedVideoCanBeCopiedInterpolatedAndReturnedToNativePlayback(string fixture)
    {
        using var engine = new MotionEngine(256, 144, presentation: true);
        using var surface = WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(engine.InputSurface);
        using var player = new MediaPlayer { IsMuted = true, IsVideoFrameServerEnabled = true };
        player.CommandManager.IsEnabled = false;
        var completed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        int frames = 0;
        void Frame(MediaPlayer sender, object args)
        {
            lock (gate)
            {
                if (completed.Task.IsCompleted) return;
                try
                {
                    player.CopyFrameToVideoSurface(surface);
                    engine.SubmitFrame();
                    if (++frames < 5) return;
                    engine.Render(0.5f);
                    engine.Present();
                    completed.TrySetResult(engine.ReadOutput());
                }
                catch (Exception error) { completed.TrySetException(error); }
            }
        }
        player.VideoFrameAvailable += Frame;
        player.MediaFailed += (_, args) => completed.TrySetException(new Exception(args.ErrorMessage));
        var clock = new MediaTimelineController();
        player.TimelineController = clock;
        player.Source = MediaSource.CreateFromUri(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)));
        clock.Resume();
        try
        {
            var pixels = await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(pixels.Where((_, i) => i % 4 != 3).Any(c => c > 32), "The decoded picture was black.");
            player.IsVideoFrameServerEnabled = false;
            Assert.False(player.IsVideoFrameServerEnabled);
            Assert.True(clock.State is MediaTimelineControllerState.Running or MediaTimelineControllerState.Stalled);
        }
        finally
        {
            clock.Pause();
            player.VideoFrameAvailable -= Frame;
            lock (gate) { player.IsVideoFrameServerEnabled = false; player.Source = null; }
        }
    }
}
