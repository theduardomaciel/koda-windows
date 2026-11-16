using System.Globalization;
using System.Text;

namespace Koda.Core;

/// <summary>The transform of one word during the Spicy bounce. X and Y are in ems of the
/// word's own text size; scale is a multiplier.</summary>
public readonly record struct WordMotion(double Scale, double X, double Y);

/// <summary>The transform and glow of one letter during the held-syllable wave. X and Y
/// are in ems of the word's text size; glow runs from 0 (none) to 1 (full).</summary>
public readonly record struct LetterMotion(double Scale, double X, double Y, double Glow);

/// <summary>
/// The motion model behind the Expressive lyrics look, translated from the Spicy
/// Better Lyrics theme. It is pure math on the song clock: every value is a function of
/// the time since the word started, so pausing, seeking and replaying need no animation
/// objects or cleanup.
///
/// The two motions are:
/// - every sung word rises and swells from rest to a peak, then settles on a damped
///   spring (1.45 Hz / 0.40 for the lift, 0.88 Hz / 0.64 for the scale);
/// - a held syllable (700 ms or more) is split into letters, each of which waves as the
///   highlight reaches it and all of which spring back to normal when the syllable ends.
/// </summary>
public static class LyricsMotion
{
    /// <summary>A word held at least this long is a held syllable.</summary>
    public const double LongWordSeconds = 0.7;

    /// <summary>More letters than this are a long word rather than a held syllable, and
    /// are never split; the word itself still bounces.</summary>
    public const int MaxWaveLetters = 8;

    /// <summary>A resting word sits a little small and a little low.</summary>
    public const double WordRestScale = 0.95;
    public const double WordRestY = 0.01;

    /// <summary>The peak of the word bounce.</summary>
    public const double WordPeakScale = 1.0505;
    public const double WordPeakY = -0.034;

    /// <summary>How far the outer syllables of a split word push apart at the seam.</summary>
    public const double WordSpread = 0.02;

    /// <summary>A resting letter of a held syllable sits a little small and low inside
    /// its word, as Spicy does: word rest 0.95 and letter rest 0.95 compose.</summary>
    public const double LetterRestScale = 0.95;
    public const double LetterRestY = 0.02;

    /// <summary>Scales the wave's swell and lift; Spicy's RefreshedEmphasis strength.</summary>
    public const double LetterStrength = 1.4;

    /// <summary>How far the outermost letters of a held syllable spread outward.</summary>
    public const double LetterSpread = 0.03;

    /// <summary>The rise lasts the word's own length plus a little, and at least this long.</summary>
    public const double RiseMinSeconds = 0.28;
    public const double RiseExtraSeconds = 0.06;

    /// <summary>The scale spring peaks this much after the lift spring.</summary>
    public const double ScaleLagSeconds = 0.09;

    /// <summary>How long the springs take to settle.</summary>
    public const double SettleSeconds = 1.64;

    /// <summary>The word lift spring.</summary>
    public const double WordLiftFrequency = 1.45;
    public const double WordLiftDamping = 0.40;

    /// <summary>The word scale spring, also used by a held syllable's letters.</summary>
    public const double WordScaleFrequency = 0.88;
    public const double WordScaleDamping = 0.64;

    /// <summary>The instrumental dots breathe on this cycle.</summary>
    public const double DotsBreatheSeconds = 2.4;

    /// <summary>The wave's sample table, every 4% from 4% to 92%, as in the CSS. The
    /// value drives the outward spread, the scale swell and the glow together.</summary>
    private static readonly double[] Wave =
    [
        0.011, 0.065, 0.176, 0.331, 0.515, 0.690, 0.818, 0.892, 0.942, 0.977,
        1.000, 1.000, 0.962, 0.896, 0.816, 0.728, 0.641, 0.553, 0.456, 0.372,
        0.295, 0.228, 0.172,
    ];

    /// <summary>The vertical part of the same table: the lift peaks earlier than the swell.</summary>
    private static readonly double[] WaveY =
    [
        -0.0007, -0.0044, -0.0117, -0.0220, -0.0335, -0.0445, -0.0499, -0.0488,
        -0.0447, -0.0393, -0.0339, -0.0289, -0.0227, -0.0157, -0.0093, -0.0042,
        -0.0008, 0.0010, 0.0020, 0.0023, 0.0019, 0.0012, 0.0002,
    ];

    /// <summary>Whether a timed word is a held syllable worth splitting into letters.</summary>
    public static bool IsHeldSyllable(LyricWord word) =>
        word.Duration >= LongWordSeconds && !string.IsNullOrWhiteSpace(word.Text);

    /// <summary>
    /// Splits a held syllable into the letters the wave can animate. Returns null when
    /// splitting would harm the text: fewer than two or more than eight glyphs, a script
    /// that needs word-level shaping (Arabic, Indic, CJK, Hebrew, Thai), a combining mark,
    /// an emoji, or a character outside the Latin/Greek/Cyrillic ranges. Those words keep
    /// the word-level bounce and are drawn as one run.
    /// </summary>
    public static IReadOnlyList<string>? WaveLetters(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var letters = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var glyph = elements.GetTextElement();
            if (glyph.Length == 0 || !IsSafeGlyph(glyph))
            {
                return null;
            }

            letters.Add(glyph);
        }

        return letters.Count is >= 2 and <= MaxWaveLetters ? letters : null;
    }

    /// <summary>How long a word takes to rise to its peak.</summary>
    public static double RiseDuration(double wordDuration) =>
        Math.Max(RiseMinSeconds, wordDuration + RiseExtraSeconds);

    /// <summary>How long one letter's wave lasts.</summary>
    public static double LetterWaveDuration(double wordDuration, int letterCount)
    {
        if (letterCount <= 0)
        {
            return 0;
        }

        return wordDuration / letterCount * 2 + 0.55;
    }

    /// <summary>
    /// When one letter's wave starts. Spicy aligns the wave peak with the highlight edge
    /// reaching the letter; the 0.46 is the wave's peak position in its own run.
    /// </summary>
    public static double LetterWaveDelay(double wordDuration, int letterCount, int index)
    {
        var slot = wordDuration / Math.Max(1, letterCount);
        var wave = LetterWaveDuration(wordDuration, letterCount);
        return Math.Clamp(
            slot * (index + 1) / 1.2
            + wordDuration / 6
            - wave * 0.46,
            0,
            100);
    }

    /// <summary>
    /// The word bounce. <paramref name="seconds"/> is the time since the word started;
    /// <paramref name="pushX"/> is the syllable's outward push in ems (0 for a single
    /// word, negative for the first syllable of a split word, positive for the last).
    /// </summary>
    public static WordMotion Word(double seconds, double wordDuration, double pushX)
    {
        if (seconds <= 0)
        {
            return new WordMotion(WordRestScale, 0, WordRestY);
        }

        var rise = RiseDuration(wordDuration);
        var scaleRise = rise + ScaleLagSeconds;

        double x;
        double y;
        if (seconds < rise)
        {
            var lift = CubicBezier(seconds / rise, 0.33, 0, 0.58, 1);
            y = WordRestY + (WordPeakY - WordRestY) * lift;
            x = pushX * lift;
        }
        else if (seconds < rise + SettleSeconds)
        {
            var spring = Spring(seconds - rise, WordLiftFrequency, WordLiftDamping);
            y = WordPeakY * spring;
            x = pushX * spring;
        }
        else
        {
            y = 0;
            x = 0;
        }

        double scale;
        if (seconds < scaleRise)
        {
            var swell = CubicBezier(seconds / scaleRise, 0.37, 0, 0.63, 1);
            scale = WordRestScale + (WordPeakScale - WordRestScale) * swell;
        }
        else if (seconds < scaleRise + SettleSeconds)
        {
            var spring = Spring(seconds - scaleRise, WordScaleFrequency, WordScaleDamping);
            scale = 1 + (WordPeakScale - 1) * spring;
        }
        else
        {
            scale = 1;
        }

        return new WordMotion(scale, x, y);
    }

    /// <summary>
    /// One letter of a held syllable. Before its wave the letter rests small and low; the
    /// wave follows its highlight; once the wave is over, the letters spring back to
    /// normal together from the end of the syllable.
    /// </summary>
    public static LetterMotion Letter(double seconds, double wordDuration, int index, int letterCount)
    {
        var rest = new LetterMotion(LetterRestScale, 0, LetterRestY, 0);
        if (letterCount < 2 || index < 0 || index >= letterCount)
        {
            return rest;
        }

        var waveDuration = LetterWaveDuration(wordDuration, letterCount);
        var delay = LetterWaveDelay(wordDuration, letterCount, index);
        var waveEnd = delay + waveDuration;

        if (seconds < delay)
        {
            return rest;
        }

        if (seconds <= waveEnd)
        {
            var progress = Math.Clamp((seconds - delay) / waveDuration, 0, 1);
            var (spread, lift) = WaveSample(progress);
            var spreadX = (index - (letterCount - 1) / 2.0) * LetterSpread;
            return new LetterMotion(
                LetterRestScale + spread * 0.1222 * LetterStrength,
                spreadX * spread,
                LetterRestY + lift * LetterStrength,
                spread);
        }

        // The wave has ended. Before the syllable does, the letter waits at rest; after
        // it, the whole syllable springs back to normal together.
        if (seconds < wordDuration)
        {
            return rest;
        }

        var age = seconds - wordDuration;
        if (age >= SettleSeconds)
        {
            return new LetterMotion(1, 0, 0, 0);
        }

        var spring = Spring(age, WordScaleFrequency, WordScaleDamping);
        return new LetterMotion(
            1 - (1 - LetterRestScale) * spring,
            0,
            LetterRestY * spring,
            0);
    }

    /// <summary>
    /// A damped spring with zero initial velocity, normalized to 1 at t = 0.
    /// This is the curve Spicy's settle keyframes were sampled from.
    /// </summary>
    public static double Spring(double seconds, double frequency, double damping)
    {
        if (seconds <= 0)
        {
            return 1;
        }

        var omega = 2 * Math.PI * frequency;
        var damped = omega * Math.Sqrt(Math.Max(0, 1 - damping * damping));
        if (damped < 1e-9)
        {
            return Math.Exp(-omega * seconds);
        }

        return Math.Exp(-damping * omega * seconds)
            * (Math.Cos(damped * seconds)
                + damping * omega / damped * Math.Sin(damped * seconds));
    }

    /// <summary>Evaluates a CSS cubic-bezier(x1, y1, x2, y2) at x in [0, 1].</summary>
    public static double CubicBezier(double x, double x1, double y1, double x2, double y2)
    {
        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        // The x curve is monotonic for the control points used here: bisect it, then read
        // the y curve at the same parameter.
        var low = 0.0;
        var high = 1.0;
        for (var i = 0; i < 24; i++)
        {
            var mid = (low + high) / 2;
            if (Bezier(mid, x1, x2) < x)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return Bezier((low + high) / 2, y1, y2);
    }

    private static double Bezier(double t, double first, double second)
    {
        var inverse = 1 - t;
        return 3 * inverse * inverse * t * first
            + 3 * inverse * t * t * second
            + t * t * t;
    }

    private static (double Spread, double Lift) WaveSample(double progress)
    {
        if (progress <= 0 || progress >= 1)
        {
            return (0, 0);
        }

        var start = 0.04;
        var step = 0.04;
        if (progress <= start)
        {
            var part = progress / start;
            return (Wave[0] * part, WaveY[0] * part);
        }

        if (progress >= 0.92)
        {
            var part = (1 - progress) / 0.08;
            return (Wave[^1] * part, WaveY[^1] * part);
        }

        var position = (progress - start) / step;
        var index = (int)Math.Floor(position);
        var fraction = position - index;
        var next = Math.Min(index + 1, Wave.Length - 1);
        return (
            Wave[index] + (Wave[next] - Wave[index]) * fraction,
            WaveY[index] + (WaveY[next] - WaveY[index]) * fraction);
    }

    private static bool IsSafeGlyph(string glyph)
    {
        foreach (var rune in glyph.EnumerateRunes())
        {
            var value = rune.Value;
            var safe =
                value is >= 0x0041 and <= 0x005A // A-Z
                or >= 0x0061 and <= 0x007A // a-z
                or >= 0x0030 and <= 0x0039 // 0-9
                or 0x0027 or 0x2019 // apostrophes
                or >= 0x00C0 and <= 0x024F // Latin-1 letters and Latin Extended-A/B
                or >= 0x1E00 and <= 0x1EFF // Latin Extended Additional
                or >= 0x0370 and <= 0x03FF // Greek
                or >= 0x1F00 and <= 0x1FFF // Greek Extended
                or >= 0x0400 and <= 0x052F; // Cyrillic
            if (!safe)
            {
                return false;
            }
        }

        return true;
    }
}
