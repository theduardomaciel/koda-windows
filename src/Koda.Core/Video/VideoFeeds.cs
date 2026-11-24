using System.Globalization;
using System.Xml.Linq;

namespace Koda.Core.Video;

/// <summary>
/// A channel's public Atom feed (<c>/feeds/videos.xml?channel_id=</c>), the source of the feed
/// for channels followed on this PC. It carries real upload times, which is what lets fifteen
/// channels merge into one list in order; it carries no durations.
/// </summary>
public static class VideoFeeds
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    public sealed record Entry(Video Video, DateTimeOffset Published);

    /// <summary>The feed's videos, Shorts left out, as the rest of video mode leaves them out.
    /// A feed that doesn't parse gives nothing.</summary>
    public static Entry[] ChannelFeed(string xml, LocalSubscription channel, DateTimeOffset now)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (Exception)
        {
            return [];
        }

        var entries = new List<Entry>();
        foreach (var entry in document.Root?.Elements(Atom + "entry") ?? [])
        {
            var id = entry.Element(Yt + "videoId")?.Value;
            var title = entry.Element(Atom + "title")?.Value;
            if (string.IsNullOrEmpty(id) || title is null) continue;
            var link = entry.Element(Atom + "link")?.Attribute("href")?.Value ?? "";
            if (link.Contains("/shorts/", StringComparison.Ordinal)) continue;
            if (!DateTimeOffset.TryParse(entry.Element(Atom + "published")?.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var published)) continue;

            var group = entry.Element(Media + "group");
            var views = group?.Element(Media + "community")?.Element(Media + "statistics")?.Attribute("views")?.Value;
            var author = entry.Element(Atom + "author")?.Element(Atom + "name")?.Value;
            var name = string.IsNullOrWhiteSpace(channel.Name) ? author ?? "" : channel.Name;
            var video = new Video(id, title, new ChannelRef(channel.ChannelId, name, channel.Avatar),
                Views: long.TryParse(views, out var count) ? ViewsText(count) : null,
                Published: Ago(published, now));
            entries.Add(new Entry(video, published));
        }

        return [.. entries];
    }

    /// <summary>"1.2M views", as YouTube's own feeds write it.</summary>
    public static string ViewsText(long count) => count switch
    {
        1 => "1 view",
        < 1_000 => $"{count} views",
        < 1_000_000 => $"{Short(count / 1_000d)}K views",
        < 1_000_000_000 => $"{Short(count / 1_000_000d)}M views",
        _ => $"{Short(count / 1_000_000_000d)}B views",
    };

    private static string Short(double value) =>
        (value < 10 ? Math.Floor(value * 10) / 10 : Math.Floor(value)).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>"3 days ago", as YouTube's own feeds write it.</summary>
    public static string Ago(DateTimeOffset date, DateTimeOffset now)
    {
        var span = now - date;
        static string Of(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalHours < 1) return Of((int)span.TotalMinutes, "minute");
        if (span.TotalDays < 1) return Of((int)span.TotalHours, "hour");
        if (span.TotalDays < 7) return Of((int)span.TotalDays, "day");
        if (span.TotalDays < 30) return Of((int)(span.TotalDays / 7), "week");
        if (span.TotalDays < 365) return Of(Math.Max(1, (int)(span.TotalDays / 30)), "month");
        return Of((int)(span.TotalDays / 365), "year");
    }
}
