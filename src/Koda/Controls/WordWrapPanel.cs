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

    /// <summary>The room kept clear on the right for the scrollbar.</summary>
    public double Inset { get; set; } = 24;

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
                y += rowHeight + RowSpacing;
                x = 0;
                rowHeight = 0;
            }

            x += unitWidth;
            rowHeight = Math.Max(rowHeight, unitHeight);
            widest = Math.Max(widest, x);
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
        var x = AlignRight ? Math.Max(0, right - width) : 0;
        for (var part = start; part < end; part++)
        {
            var size = Children[part].DesiredSize;
            Children[part].Arrange(new Rect(x, y, size.Width, size.Height));
            _placements[Children[part]] = (x, y, size.Width);
            x += size.Width;
        }
    }
}
