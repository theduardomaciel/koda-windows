using Koda.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Koda.Helpers;

/// <summary>
/// Bolds the part of a suggestion title that matched the typed query. The
/// match ranges come from <see cref="FuzzySearch"/>, so abbreviations and
/// one-character typos light up the same characters that made the row rank.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(Highlight), new PropertyMetadata("", OnQueryChanged));

    private static readonly DependencyProperty RegisteredProperty = DependencyProperty.RegisterAttached(
        "Registered", typeof(bool), typeof(Highlight), new PropertyMetadata(false));

    // TextBlock.Inlines and Text are separate stores. Keep the bound string so a
    // later Text change can still be re-highlighted even if Inlines is what the
    // renderer currently reads.
    private static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(Highlight), new PropertyMetadata(""));

    public static string GetQuery(DependencyObject d) => (string)d.GetValue(QueryProperty);

    public static void SetQuery(DependencyObject d, string value) => d.SetValue(QueryProperty, value);

    private static void OnQueryChanged(DependencyObject d, DependencyPropertyChangedEventArgs args)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        if (!(bool)block.GetValue(RegisteredProperty))
        {
            block.SetValue(RegisteredProperty, true);
            block.RegisterPropertyChangedCallback(TextBlock.TextProperty, (sender, _) =>
            {
                if (sender is TextBlock changed)
                {
                    Apply(changed, GetQuery(changed));
                }
            });
        }

        Apply(block, args.NewValue as string);
    }

    private static void Apply(TextBlock block, string? query)
    {
        var text = block.Text;
        if (string.IsNullOrEmpty(text))
        {
            text = (string)block.GetValue(SourceProperty);
        }
        else
        {
            block.SetValue(SourceProperty, text);
        }

        if (string.IsNullOrEmpty(text))
        {
            block.Inlines.Clear();
            return;
        }

        var match = string.IsNullOrWhiteSpace(query) ? null : FuzzySearch.Match(text, query);
        if (match is null || match.Ranges.Count == 0)
        {
            block.Inlines.Clear();
            block.Inlines.Add(new Run { Text = text });
            return;
        }

        block.Inlines.Clear();
        var cursor = 0;
        foreach (var range in match.Ranges.OrderBy(r => r.Start))
        {
            var start = Math.Clamp(range.Start, 0, text.Length);
            var length = Math.Clamp(range.Length, 0, text.Length - start);
            if (start < cursor || length == 0)
            {
                continue;
            }

            if (start > cursor)
            {
                block.Inlines.Add(new Run { Text = text[cursor..start] });
            }

            block.Inlines.Add(new Run
            {
                Text = text.Substring(start, length),
                FontWeight = FontWeights.SemiBold,
            });
            cursor = start + length;
        }

        if (cursor < text.Length)
        {
            block.Inlines.Add(new Run { Text = text[cursor..] });
        }
    }
}
