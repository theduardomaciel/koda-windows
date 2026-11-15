using Koda.Core;

namespace Koda.Core.Tests;

public sealed class LyricsMotionTests
{
    [Fact]
    public void TheSpringStartsAtOneAndSettles()
    {
        Assert.Equal(1, LyricsMotion.Spring(0, 1.45, 0.40), 4);
        // The value the Spicy keyframe table has at 2.5% of the settle.
        Assert.Equal(0.9374, LyricsMotion.Spring(0.041, 1.45, 0.40), 3);
        Assert.InRange(Math.Abs(LyricsMotion.Spring(1.64, 1.45, 0.40)), 0, 0.01);
    }

    [Fact]
    public void AWordRestsThenPeaksAndSettlesBack()
    {
        var rest = LyricsMotion.Word(0, 1.0, 0);
        Assert.Equal(LyricsMotion.WordRestScale, rest.Scale, 4);
        Assert.Equal(LyricsMotion.WordRestY, rest.Y, 4);

        var liftPeak = LyricsMotion.Word(LyricsMotion.RiseDuration(1.0), 1.0, 0);
        Assert.Equal(LyricsMotion.WordPeakY, liftPeak.Y, 3);
        Assert.InRange(liftPeak.Scale, 1.04, LyricsMotion.WordPeakScale);

        var scalePeak = LyricsMotion.Word(
            LyricsMotion.RiseDuration(1.0) + LyricsMotion.ScaleLagSeconds,
            1.0,
            0);
        Assert.Equal(LyricsMotion.WordPeakScale, scalePeak.Scale, 3);

        var normal = LyricsMotion.Word(3.0, 1.0, 0);
        Assert.Equal(1, normal.Scale, 4);
        Assert.Equal(0, normal.Y, 4);
        Assert.Equal(0, normal.X, 4);
    }

    [Fact]
    public void TheOuterSyllablePushesOutwardAtItsPeak()
    {
        var first = LyricsMotion.Word(1.0, 1.0, -LyricsMotion.WordSpread);
        Assert.True(first.X < 0);

        var last = LyricsMotion.Word(1.0, 1.0, LyricsMotion.WordSpread);
        Assert.True(last.X > 0);
    }

    [Fact]
    public void AHeldLetterSwellsAsItsWavePasses()
    {
        const double duration = 1.2;
        const int count = 4;
        const int index = 2;
        var delay = LyricsMotion.LetterWaveDelay(duration, count, index);
        var wave = LyricsMotion.LetterWaveDuration(duration, count);
        var peak = LyricsMotion.Letter(delay + wave * 0.44, duration, index, count);

        Assert.InRange(peak.Glow, 0.95, 1.0);
        Assert.True(peak.Scale > 1.11);
        Assert.True(peak.X > 0); // the right half of the syllable moves right
    }

    [Fact]
    public void AHeldLetterRestsBeforeAndAfterTheWave()
    {
        var before = LyricsMotion.Letter(-0.1, 1.0, 0, 4);
        Assert.Equal(LyricsMotion.LetterRestScale, before.Scale, 4);
        Assert.Equal(0, before.Glow, 4);

        var after = LyricsMotion.Letter(5.0, 1.0, 0, 4);
        Assert.Equal(1, after.Scale, 4);
        Assert.Equal(0, after.Y, 4);
    }

    [Theory]
    [InlineData("hold", 4)]
    [InlineData("café", 4)]
    [InlineData("1234", 4)]
    public void SafeLatinWordsSplitIntoLetters(string text, int count)
    {
        var letters = LyricsMotion.WaveLetters(text);
        Assert.NotNull(letters);
        Assert.Equal(count, letters!.Count);
    }

    [Theory]
    [InlineData("everybody")] // nine letters: a long word, not a held syllable
    [InlineData("cafe\u0301")] // a combining accent would be split from its base
    [InlineData("مرحبا")] // Arabic needs word-level shaping
    [InlineData("こんにちは")] // CJK
    [InlineData("🙂🙂")] // surrogate pairs
    public void UnsafeScriptsAndLongWordsAreNotSplit(string text)
    {
        Assert.Null(LyricsMotion.WaveLetters(text));
    }

    [Fact]
    public void TheHeldThresholdIsSevenHundredMilliseconds()
    {
        Assert.True(LyricsMotion.IsHeldSyllable(new LyricWord("hold", 0, 0.7)));
        Assert.False(LyricsMotion.IsHeldSyllable(new LyricWord("hold", 0, 0.699)));
    }

    [Fact]
    public void TheCubicBezierEndsAreExact()
    {
        Assert.Equal(0, LyricsMotion.CubicBezier(0, 0.33, 0, 0.58, 1), 4);
        Assert.Equal(1, LyricsMotion.CubicBezier(1, 0.33, 0, 0.58, 1), 4);
        Assert.InRange(LyricsMotion.CubicBezier(0.5, 0.33, 0, 0.58, 1), 0, 1);
    }
}
