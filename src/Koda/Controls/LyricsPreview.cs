using System;
using System.Collections.Generic;
using Koda.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Koda.Controls;

/// <summary>
/// A small still sample of the lyrics look, for the Settings page. It shows one line at
/// the point the sweep has reached, the line after it dimmed, and the backing vocals, all
/// at the size, gap, weight, typeface and dimming the panel will use. It is a preview,
/// not the panel: no timer, no scrolling, and nothing else has to know it exists.
/// </summary>
public sealed class LyricsPreview : Grid
{
    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color Unsung = Color.FromArgb(0x61, 0xFF, 0xFF, 0xFF);
    private static readonly Color ExpressiveSung = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);
    private static readonly Color ExpressiveUnsung = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);

    private readonly StackPanel _lines = new();

    public LyricsPreview()
    {
        Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x14, 0x14, 0x16)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24, 18, 24, 20),
            Height = 300,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _lines,
        });

        Loaded += (_, _) =>
        {
            App.Settings.Changed += OnSettingsChanged;
            Refresh();
        };
        Unloaded += (_, _) => App.Settings.Changed -= OnSettingsChanged;
        Refresh();
    }

    private void OnSettingsChanged(Settings settings) => Refresh();

    /// <summary>Rebuilds the sample from the settings as they are now.</summary>
    public void Refresh()
    {
        var settings = App.Settings;
        _lines.Children.Clear();
        _lines.Spacing = settings.LyricsLineGap;

        var caption = new TextBlock
        {
            Text = "PREVIEW",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 120,
            Foreground = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 0, 4),
        };
        _lines.Children.Add(caption);

        _lines.Children.Add(Line("And all we ever wanted", settings, current: true));
        if (settings.BackingVocals != BackingVocalsMode.Hidden)
        {
            var text = settings.BackingVocals == BackingVocalsMode.WithoutParentheses
                ? "oh, oh"
                : "(oh, oh)";
            _lines.Children.Add(Line(text, settings, current: false, backing: true));
        }

        _lines.Children.Add(Line("Was sunshine and honesty", settings, current: false));
    }

    private static FrameworkElement Line(string text, Settings settings, bool current, bool backing = false)
    {
        var classic = settings.LyricsRenderer == LyricsRenderer.Classic;
        var expressive = settings.LyricsRenderer == LyricsRenderer.Expressive;

        if (expressive && current && !backing)
        {
            return ExpressiveLine(text, settings);
        }

        var size = backing ? settings.LyricsFontSize * 0.7 : settings.LyricsFontSize;
        var dim = backing ? 0.7 : current ? 1 : settings.LyricsIdleOpacity;

        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Opacity = dim,
        };
        StyleText(block, settings, size, backing);

        if (expressive)
        {
            block.Foreground = new SolidColorBrush(current ? ExpressiveSung : ExpressiveUnsung);
            if (!current && !backing)
            {
                // An upcoming line sits a little small and low, like every unsung word
                // in the real panel.
                block.RenderTransform = new CompositeTransform
                {
                    ScaleX = 0.95,
                    ScaleY = 0.95,
                    TranslateY = size * 0.01,
                };
                block.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            }
        }
        else
        {
            block.Foreground = current && !classic ? Sweep(settings) : new SolidColorBrush(White);
        }

        return block;
    }

    /// <summary>The Expressive line, caught while the last word is a held syllable: the
    /// word itself is at the top of its bounce and its letters are part way through the
    /// wave. This is a still: the panel does the real motion.</summary>
    private static FrameworkElement ExpressiveLine(string text, Settings settings)
    {
        var size = settings.LyricsFontSize;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wrap = new WordWrapPanel
        {
            WrapWidth = 520,
            Inset = 0,
            RowSpacing = 4,
        };

        for (var index = 0; index < words.Length; index++)
        {
            var word = words[index];
            var letters = index == words.Length - 1 ? LyricsMotion.WaveLetters(word) : null;
            if (letters is null)
            {
                var block = new TextBlock
                {
                    Text = word,
                    Foreground = new SolidColorBrush(ExpressiveSung),
                    Margin = new Thickness(0, 0, size * 0.25, 0),
                    TextWrapping = TextWrapping.NoWrap,
                };
                StyleText(block, settings, size, soft: false);
                wrap.Add(block, joinsPrevious: false);
                continue;
            }

            var motion = LyricsMotion.Word(1.2, 1.2, 0);
            var wordPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                RenderTransform = new CompositeTransform
                {
                    ScaleX = motion.Scale,
                    ScaleY = motion.Scale,
                    TranslateY = motion.Y * size,
                },
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            };

            for (var letterIndex = 0; letterIndex < letters.Count; letterIndex++)
            {
                var letter = LyricsMotion.Letter(1.2, 1.2, letterIndex, letters.Count);
                var block = new TextBlock
                {
                    Text = letters[letterIndex],
                    Foreground = new SolidColorBrush(ExpressiveSung),
                    RenderTransform = new CompositeTransform
                    {
                        ScaleX = letter.Scale,
                        ScaleY = letter.Scale,
                        TranslateX = letter.X * size,
                        TranslateY = letter.Y * size,
                    },
                    RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                    TextWrapping = TextWrapping.NoWrap,
                };
                StyleText(block, settings, size, soft: false);
                wordPanel.Children.Add(block);
            }

            wrap.Add(wordPanel, joinsPrevious: false);
        }

        return wrap;
    }

    private static void StyleText(TextBlock block, Settings settings, double size, bool soft)
    {
        var classic = settings.LyricsRenderer == LyricsRenderer.Classic;
        var expressive = settings.LyricsRenderer == LyricsRenderer.Expressive;
        block.FontFamily = new FontFamily(settings.LyricsFont);
        block.FontSize = size;
        block.FontWeight = soft
            ? expressive ? FontWeights.SemiBold : FontWeights.Medium
            : classic ? FontWeights.Normal : FontWeights.Bold;
        block.CharacterSpacing = expressive ? 0 : 20;
        block.LineHeight = Math.Round(size * 1.35 * settings.LyricsLineSpacing);
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    /// <summary>The current line, caught part way through: white behind, the soft front
    /// of the fill where it has reached, and the unsung words a step lighter.</summary>
    private static LinearGradientBrush Sweep(Settings settings)
    {
        const double front = 0.55;
        var feather = Math.Max(0.02, 0.08 + 0.55 * settings.LyricsSoftness);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        brush.GradientStops.Add(new GradientStop { Color = White, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = White, Offset = Math.Max(0, front - feather) });
        brush.GradientStops.Add(new GradientStop { Color = Unsung, Offset = Math.Min(1, front) });
        brush.GradientStops.Add(new GradientStop { Color = Unsung, Offset = 1 });
        return brush;
    }
}
