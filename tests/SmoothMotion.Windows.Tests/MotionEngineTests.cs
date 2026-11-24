using SmoothMotion.Windows;
using Xunit;

namespace SmoothMotion.Windows.Tests;

public class MotionEngineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EndpointsPreserveOriginalsAndCutsNeverCrossfade(bool software)
    {
        using var engine = new MotionEngine(128, 72, presentation: false, softwareDevice: software);
        var a = Enumerable.Repeat(0xFF102040u, 128 * 72).ToArray();
        var b = Enumerable.Repeat(0xFFE0C080u, 128 * 72).ToArray();
        engine.UploadFrame(a); engine.UploadFrame(b);
        engine.Render(0); AssertPixel(engine.ReadOutput(), 0x10, 0x20, 0x40);
        engine.Render(1); AssertPixel(engine.ReadOutput(), 0xE0, 0xC0, 0x80);
        engine.Render(0.25f); AssertPixel(engine.ReadOutput(), 0x10, 0x20, 0x40);
        engine.Render(0.75f); AssertPixel(engine.ReadOutput(), 0xE0, 0xC0, 0x80);
        engine.Reset(); Assert.False(engine.HasPair);
        engine.UploadFrame(a); Assert.False(engine.HasPair);
    }

    [Fact]
    public void ATranslatedTextureMovesToTheIntermediatePosition()
    {
        const int w = 256, h = 144, shift = 8;
        using var engine = new MotionEngine(w, h, presentation: false, softwareDevice: true);
        var random = new Random(42);
        var a = new uint[w * h]; var b = new uint[w * h];
        // Smooth, nonperiodic texture with enough structure to disambiguate the search.
        var noise = Enumerable.Range(0, w * h).Select(_ => random.NextDouble()).ToArray();
        for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x)
        {
            double n = 0;
            for (int dy = -2; dy <= 2; ++dy) for (int dx = -2; dx <= 2; ++dx)
                n += noise[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)];
            var c = (uint)(n / 25 * 255);
            a[y * w + x] = 0xFF000000u | c | (c << 8) | (c << 16);
        }
        for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) b[y * w + x] = a[y * w + Math.Max(0, x - shift)];
        engine.UploadFrame(a); engine.UploadFrame(b); engine.Render(0.5f);
        var result = engine.ReadOutput();
        double warped = 0, blend = 0;
        for (int y = 20; y < h - 20; ++y) for (int x = 20; x < w - 20; ++x)
        {
            var expected = (byte)a[y * w + x - shift / 2];
            warped += Math.Abs(result[(y * w + x) * 4] - expected);
            blend += Math.Abs((((byte)a[y * w + x] + (byte)b[y * w + x]) / 2.0) - expected);
        }
        Assert.True(warped < blend * 0.65, $"Motion error {warped:0}; blend error {blend:0}");
    }

    private static void AssertPixel(byte[] pixels, byte r, byte g, byte b)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(r, pixels[i]); Assert.Equal(g, pixels[i + 1]);
            Assert.Equal(b, pixels[i + 2]); Assert.Equal(255, pixels[i + 3]);
        }
    }

    [Fact]
    public void APausedSeekCanDisplayASingleOriginalFrame()
    {
        using var engine = new MotionEngine(128, 72, presentation: false, softwareDevice: true);
        engine.UploadFrame(Enumerable.Repeat(0xFF103050u, 128 * 72).ToArray());
        Assert.False(engine.HasPair);
        engine.RenderLatest();
        AssertPixel(engine.ReadOutput(), 0x10, 0x30, 0x50);
        engine.Reset();
        engine.UploadFrame(Enumerable.Repeat(0xFF507090u, 128 * 72).ToArray());
        engine.RenderLatest();
        AssertPixel(engine.ReadOutput(), 0x50, 0x70, 0x90);
    }

    [Fact]
    public void FullHdFramesRunThroughTheHardwarePipeline()
    {
        using var engine = new MotionEngine(1920, 1080, presentation: true);
        engine.UploadFrame(Enumerable.Repeat(0xFF103050u, 1920 * 1080).ToArray());
        engine.UploadFrame(Enumerable.Repeat(0xFF103050u, 1920 * 1080).ToArray());
        engine.Render(0.5f);
        engine.Present();
        AssertPixel(engine.ReadOutput(), 0x10, 0x30, 0x50);
    }
}
