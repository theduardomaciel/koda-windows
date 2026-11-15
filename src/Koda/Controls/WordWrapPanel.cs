using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Koda.Controls;

/// <summary>
/// The words of one lyric line, wrapped against the width XAML actually gives the panel.
/// It is a normal panel, so a sidebar, monitor or zoom change re-measures it without any
/// size event or cached width in the lyrics code.
///
/// A word the provider split into syllables carries <c>joinsPrevious</c>: the panel will not
/// start a row between its parts. The inset keeps the words clear of the scrollbar.
/// </summary>
internal sealed class WordWrapPanel : Panel
{
    private readonly List<bool> _joins = [];

    /// <summary>The width to wrap against. Set from the lyrics panel's real width; a
    /// ScrollViewer can measure its content with an unbounded width, which would otherwise
    /// wrap correctly on screen but report a one-row height and overlap the next line.</summary>
    public double WrapWidth
    {
        get => _wrapWidth;
        set
        {
            if (Math.Abs(_wrapWidth - value) < 0.01)
            {
                return;
            }

            _wrapWidth = value;
            InvalidateMeasure();
        }
    }

    private double _wrapWidth;

    /// <summary>The space between wrapped visual lines.</summary>
    public double RowSpacing { get; set; }

    /// <summary>The room kept clear on the right for the scrollbar.</summary>
    public double Inset { get; set; } = 24;

    public void Add(FrameworkElement child, bool joinsPrevious)
    {
        Children.Add(child);
        _joins.Add(joinsPrevious);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var bounded = WrapWidth > 0 ? WrapWidth : availableSize.Width;
        var limit = double.IsInfinity(bounded)
            ? double.PositiveInfinity
            : Math.Max(0, bounded - Inset);

        var x = 0.0;
        var y = 0.0;
        var rowHeight = 0.0;
        var widest = 0.0;

        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;
            var joins = index < _joins.Count && _joins[index];
            if (x > 0 && !joins && x + size.Width > limit)
            {
                y += rowHeight + RowSpacing;
                x = 0;
                rowHeight = 0;
            }

            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
            widest = Math.Max(widest, x);
        }

        return new Size(
            double.IsInfinity(limit) ? widest : Math.Min(widest, limit),
            y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bounded = WrapWidth > 0 ? WrapWidth : finalSize.Width;
        var limit = Math.Max(0, bounded - Inset);
        var x = 0.0;
        var y = 0.0;
        var rowHeight = 0.0;

        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            var size = child.DesiredSize;
            var joins = index < _joins.Count && _joins[index];
            if (x > 0 && !joins && x + size.Width > limit)
            {
                y += rowHeight + RowSpacing;
                x = 0;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return finalSize;
    }
}
