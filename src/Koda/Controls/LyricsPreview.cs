using System;
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

    private static TextBlock Line(string text, Settings settings, bool current, bool backing = false)
    {
        var classic = settings.LyricsRenderer == LyricsRenderer.Classic;
        var size = backing ? settings.LyricsFontSize * 0.7 : settings.LyricsFontSize;
        var dim = backing ? 0.7 : current ? 1 : settings.LyricsIdleOpacity;

        var block = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(settings.LyricsFont),
            FontSize = size,
            FontWeight = backing
                ? FontWeights.Medium
                : classic ? FontWeights.Normal : FontWeights.Bold,
            CharacterSpacing = 20,
            LineHeight = Math.Round(size * 1.35 * settings.LyricsLineSpacing),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap,
            Opacity = dim,
        };

        block.Foreground = current && !classic ? Sweep(settings) : new SolidColorBrush(White);
        return block;
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
