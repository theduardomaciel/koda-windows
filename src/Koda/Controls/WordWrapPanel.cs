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
    private readonly Dictionary<UIElement, (double X, double Y, double Width)> _placements = [];

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

    /// <summary>Whether the wrapped lines end at the same right edge, for a duet line that
    /// belongs on the right of the panel.</summary>
    public bool AlignRight
    {
        get => _alignRight;
        set
        {
            if (_alignRight == value)
            {
                return;
            }

            _alignRight = value;
            InvalidateArrange();
        }
    }

    private bool _alignRight;

    /// <summary>The space kept clear on the right for the scrollbar.</summary>
    public double Inset { get; set; } = 24;

    /// <summary>The empty space after the last word of a visual row. Right-aligned rows
    /// ignore it when they end at the row edge, so the glyphs and the hover box's padding
    /// stay symmetric instead of carrying a trailing word gap on the right.</summary>
    private static double TrailingGap(UIElement child) =>
        child is FrameworkElement element ? element.Margin.Right : 0;

    public void Add(FrameworkElement child, bool joinsPrevious)
    {
        Children.Add(child);
        _joins.Add(joinsPrevious);
    }

    /// <summary>Empties the panel and its join flags together. A panel reused by the
    /// virtualized lyric rows must not keep the previous line's joins at the same indices.</summary>
    public void ClearWords()
    {
        Children.Clear();
        _joins.Clear();
        _placements.Clear();
    }

    /// <summary>The arrange position and width of a child in the last layout pass. The
    /// lyrics panel uses it to compensate the visual gap left by a scaled word.</summary>
    public bool TryGetPlacement(UIElement child, out (double X, double Y, double Width) placement) =>
        _placements.TryGetValue(child, out placement);

    /// <summary>Fills the placement map immediately after children are added, without
    /// waiting for the next layout pass. The first rendered frame of a promoted row would
    /// otherwise have uncompensated word gaps for one frame.</summary>
    public void RefreshPlacements()
    {
        _placements.Clear();
        var bounded = WrapWidth;
        var limit = bounded > 0
            ? Math.Max(0, bounded - Inset)
            : double.PositiveInfinity;
        var right = double.IsPositiveInfinity(limit) ? 0 : Math.Min(bounded, limit);

        var units = new List<(int Start, int Count, double Width, double Height)>();
        for (var index = 0; index < Children.Count;)
        {
            var start = index;
            var width = 0.0;
            var height = 0.0;
            do
            {
                var child = Children[index];
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
                index++;
            }
            while (index < Children.Count && index < _joins.Count && _joins[index]);

            units.Add((start, index - start, width, height));
        }

        var y = 0.0;
        var rowStart = 0;
        var rowWidth = 0.0;
        var rowHeight = 0.0;
        for (var unit = 0; unit <= units.Count; unit++)
        {
            var wraps = unit == units.Count
                || (rowWidth > 0 && rowWidth + units[unit].Width > limit);
            if (!wraps)
            {
                rowWidth += units[unit].Width;
                rowHeight = Math.Max(rowHeight, units[unit].Height);
                continue;
            }

            var x = AlignRight
                ? Math.Max(0, right - (rowWidth - (unit > 0 ? TrailingGap(Children[unit - 1]) : 0)))
                : 0;
            for (var placed = rowStart; placed < unit; placed++)
            {
                var (childStart, count, _, _) = units[placed];
                for (var childIndex = childStart; childIndex < childStart + count; childIndex++)
                {
                    var size = Children[childIndex].DesiredSize;
                    _placements[Children[childIndex]] = (x, y, size.Width);
                    x += size.Width;
                }
            }

            y += rowHeight + RowSpacing;
            rowStart = unit;
            rowWidth = unit < units.Count ? units[unit].Width : 0;
            rowHeight = unit < units.Count ? units[unit].Height : 0;
        }
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

        for (var index = 0; index < Children.Count;)
        {
            // A word split into syllables is one unit: the row break must go before the
            // whole word, or its tail is pushed past the edge and clipped.
            var start = index;
            var unitWidth = 0.0;
            var unitHeight = 0.0;
            do
            {
                var child = Children[index];
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                unitWidth += child.DesiredSize.Width;
                unitHeight = Math.Max(unitHeight, child.DesiredSize.Height);
                index++;
            }
            while (index < Children.Count && index < _joins.Count && _joins[index]);

            if (x > 0 && x + unitWidth > limit)
            {
                // The row ends with the word before this unit. Its trailing gap is empty,
                // so it must not widen the panel (and the hover box behind the line).
                widest = Math.Max(widest, x - (start > 0 ? TrailingGap(Children[start - 1]) : 0));
                y += rowHeight + RowSpacing;
                x = 0;
                rowHeight = 0;
            }

            x += unitWidth;
            rowHeight = Math.Max(rowHeight, unitHeight);
        }

        if (Children.Count > 0)
        {
            widest = Math.Max(widest, x - TrailingGap(Children[Children.Count - 1]));
        }

        return new Size(
            double.IsInfinity(limit) ? widest : Math.Min(widest, limit),
            y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _placements.Clear();
        var bounded = WrapWidth > 0 ? WrapWidth : finalSize.Width;
        var limit = Math.Max(0, bounded - Inset);
        var right = Math.Min(finalSize.Width, limit);

        var rowStart = 0;
        var rowWidth = 0.0;
        var rowHeight = 0.0;
        var y = 0.0;

        for (var index = 0; index < Children.Count;)
        {
            var start = index;
            var unitWidth = 0.0;
            var unitHeight = 0.0;
            do
            {
                var size = Children[index].DesiredSize;
                unitWidth += size.Width;
                unitHeight = Math.Max(unitHeight, size.Height);
                index++;
            }
            while (index < Children.Count && index < _joins.Count && _joins[index]);

            if (rowWidth > 0 && rowWidth + unitWidth > limit)
            {
                PlaceRow(rowStart, start, rowWidth, rowHeight, y, right);
                y += rowHeight + RowSpacing;
                rowStart = start;
                rowWidth = 0;
                rowHeight = 0;
            }

            rowWidth += unitWidth;
            rowHeight = Math.Max(rowHeight, unitHeight);
        }

        PlaceRow(rowStart, Children.Count, rowWidth, rowHeight, y, right);
        return finalSize;
    }

    /// <summary>Places one visual line of words, ending at the same right edge when the
    /// line belongs on the right.</summary>
    private void PlaceRow(int start, int end, double width, double height, double y, double right)
    {
        var trailing = end > start ? TrailingGap(Children[end - 1]) : 0;
        var x = AlignRight ? Math.Max(0, right - (width - trailing)) : 0;
        for (var part = start; part < end; part++)
        {
            var size = Children[part].DesiredSize;
            Children[part].Arrange(new Rect(x, y, size.Width, size.Height));
            _placements[Children[part]] = (x, y, size.Width);
            x += size.Width;
        }
    }
}
