using System;
using System.Collections.Generic;
using Koda.Playback;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using UIPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Koda.Controls;

/// <summary>Opt-in measurements; native presentation never gets invented FPS values.</summary>
public sealed partial class VideoDiagnosticsPanel : Grid
{
    private readonly Dictionary<string, TextBlock> _values = [];
    private readonly TextBlock _status = Text("Waiting for video", 12), _rates = Text("Waiting for samples", 12);
    private readonly Canvas _chart = new() { Height = 72, IsHitTestVisible = false };
    private readonly List<(double Time, double Input, double Output)> _history = [];
    private readonly SolidColorBrush _input = new(ColorHelper.FromArgb(255, 255, 203, 128));
    private readonly SolidColorBrush _output = new(ColorHelper.FromArgb(255, 99, 217, 255));
    private string? _video;
    private double _lastSample;
    private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    public event Action? CloseRequested;

    public VideoDiagnosticsPanel()
    {
        RequestedTheme = ElementTheme.Dark;
        Background = new SolidColorBrush(ColorHelper.FromArgb(242, 24, 24, 24));
        BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 86, 86, 86));
        BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(6); Padding = new Thickness(12);
        HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(12); MaxWidth = 420;
        AutomationProperties.SetName(this, "Stats for nerds");
        var content = new StackPanel { Spacing = 7 };
        var header = new Grid();
        var title = Text("Stats for nerds", 14);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.Margin = new Thickness(0, 0, 40, 0);
        title.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(title);
        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetName(close, "Close stats for nerds");
        close.Click += (_, _) => CloseRequested?.Invoke(); header.Children.Add(close); content.Children.Add(header);
        content.Children.Add(_status);
        var table = new Grid { ColumnSpacing = 10, RowSpacing = 2 };
        table.ColumnDefinitions.Add(new() { Width = new GridLength(100) });
        table.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var name in new[] { "Video", "Picture", "Format", "Playback", "Stream bitrate", "Buffer fill", "FPS limits", "Frames", "Intermediate", "Missed / skipped", "Submit / delay", "GPU" })
        {
            int row = table.RowDefinitions.Count; table.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var label = Text(name, 11); label.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 195, 195, 195));
            var value = Text("—", 11); value.IsTextSelectionEnabled = true;
            Grid.SetRow(label, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
            table.Children.Add(label); table.Children.Add(value); _values[name] = value;
        }
        content.Children.Add(_rates);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        var inputLabel = Text("Captured input", 11); inputLabel.Foreground = _input;
        var outputLabel = Text("Accepted output", 11); outputLabel.Foreground = _output;
        legend.Children.Add(inputLabel); legend.Children.Add(outputLabel); content.Children.Add(legend);
        content.Children.Add(_chart);
        content.Children.Add(table);
        content.Children.Add(Text("Last 30 seconds · 0–120 fps. Output counts accepted Presents; physical scanouts and native FPS are not exposed. Intermediate renders may use originals at scene cuts.", 10));
        Children.Add(new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled
        });
        _chart.SizeChanged += (_, _) => DrawGraph();
    }
    private static TextBlock Text(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = new SolidColorBrush(Colors.White),
        TextWrapping = TextWrapping.Wrap
    };
    internal void Update(VideoDiagnostics info)
    {
        if (_video != info.VideoId) { _history.Clear(); _video = info.VideoId; _lastSample = 0; }
        var m = info.Motion; var c = m.Counters;
        _status.Text = $"Smooth motion: {m.Status}";
        Set("Video", info.VideoId);
        Set("Picture", $"{info.Width} × {info.Height} · {(info.SourceFps > 0 ? $"{info.SourceFps:0.##} fps source" : "source FPS unavailable")}");
        Set("Format", info.Format);
        Set("Playback", $"{info.State} · {info.Speed:0.##}× · {info.Position:0.0} / {info.Duration:0.0} s");
        Set("Stream bitrate", info.StreamBitrate > 0 ? $"{info.StreamBitrate / 1_000_000.0:0.00} Mb/s (nominal)" : "Unavailable");
        Set("Buffer fill", double.IsFinite(info.BufferFill) ? $"{info.BufferFill:P0} (Windows)" : "Unavailable from this source");
        var display = double.IsFinite(m.DisplayHz) ? $"{m.DisplayHz:0.##} Hz display" : "display unknown (60 fps safety limit)";
        Set("FPS limits", $"{m.RequestedFps} requested · {m.TargetFps:0.##} target · {display}");
        Set("Frames", $"{c.Captured:N0} captured · {c.Presented:N0} presented");
        Set("Intermediate", $"{c.Intermediate:N0} renders · {c.IntermediateFps:0.0}/s");
        Set("Missed / skipped", $"{c.MissedTicks:N0} output ticks · {c.CaptureDrops:N0} input captures");
        Set("Submit / delay", $"{c.SubmitCpuMs:0.00} ms CPU · {m.AudioDelayMs:0.0} ms audio delay");
        Set("GPU", m.Gpu);
        _rates.Text = m.Active ? $"Measured: {c.InputFps:0.0} input → {c.OutputFps:0.0} output fps"
            : "Measured output: — (native playback or paused)";
        var now = Now;
        if (now - _lastSample < 0.4) return;
        _lastSample = now;
        _history.Add((now, m.Active ? c.InputFps : double.NaN, m.Active ? c.OutputFps : double.NaN));
        _history.RemoveAll(p => p.Time < now - 30); DrawGraph();
    }
    private void Set(string key, string value) => _values[key].Text = value;
    internal void ReportError() => _status.Text = "Video statistics are unavailable. Playback continues.";
    private void DrawGraph()
    {
        _chart.Children.Clear();
        double width = _chart.ActualWidth, height = _chart.Height;
        if (width <= 0) return;
        foreach (int rate in new[] { 0, 60, 120 })
        {
            var y = height - rate / 120.0 * height;
            _chart.Children.Add(new Line
            {
                X1 = 24,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 66, 66, 66)),
                StrokeThickness = 1
            });
            var label = Text(rate.ToString(), 9);
            Canvas.SetTop(label, Math.Clamp(y - 6, 0, height - 12));
            _chart.Children.Add(label);
        }
        var now = Now;
        DrawSeries(true, _input); DrawSeries(false, _output);
        void DrawSeries(bool input, SolidColorBrush stroke)
        {
            var geometry = new PathGeometry(); PathFigure? figure = null;
            foreach (var sample in _history)
            {
                double value = input ? sample.Input : sample.Output;
                if (double.IsNaN(value)) { figure = null; continue; }
                var point = new Point(24 + Math.Clamp(1 - (now - sample.Time) / 30, 0, 1) * Math.Max(0, width - 24),
                    height - Math.Clamp(value / 120, 0, 1) * height);
                if (figure is null) { figure = new PathFigure { StartPoint = point, IsClosed = false }; geometry.Figures.Add(figure); }
                else figure.Segments.Add(new LineSegment { Point = point });
            }
            _chart.Children.Add(new UIPath { Data = geometry, Stroke = stroke, StrokeThickness = 1.5 });
        }
    }
}
