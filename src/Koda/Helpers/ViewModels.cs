using System.Collections.Generic;
using System.Linq;
using Koda.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Koda.Helpers;

/// <summary>A cover on a shelf: what it says under it, and whether it is drawn round (artists).</summary>
public sealed record CardItem(MusicItem Item, string Title, string Subtitle, Artwork? Artwork, bool IsRound)
{
    public static CardItem From(MusicItem item) => item switch
    {
        MusicItem.SongRecord s => new(item, s.Value.Title, s.Value.ArtistLine, s.Value.Artwork, false),
        MusicItem.AlbumRecord a => new(item, a.Value.Title, AlbumLine(a.Value), a.Value.Artwork, false),
        MusicItem.ArtistRecord r => new(item, r.Value.Name, r.Value.Subtitle ?? "Artist", r.Value.Artwork, true),
        MusicItem.PlaylistRecord p => new(item, p.Value.Title, p.Value.Subtitle ?? "Playlist", p.Value.Artwork, false),
        MusicItem.UserPlaylistRecord u => new(item, u.Value.Name,
            $"Your playlist · {(u.Value.Songs.Length == 1 ? "1 song" : $"{u.Value.Songs.Length} songs")}", u.Value.Songs.FirstOrDefault(s => s.Artwork is not null)?.Artwork, false),
        _ => new(item, item.Title, "", item.Artwork, false),
    };

    /// <summary>What a screen reader says for the card: a list item with no name of its own is
    /// read by its item's text, which for a record would be every field.</summary>
    public override string ToString() => Subtitle.Length == 0 ? Title : $"{Title}, {Subtitle}";

    public static CardItem From(AlbumSummary album) => From(new MusicItem.AlbumRecord(album));

    public static CardItem From(ArtistSummary artist) => From(new MusicItem.ArtistRecord(artist));

    public static CardItem From(PlaylistSummary playlist) => From(new MusicItem.PlaylistRecord(playlist));

    public static CardItem From(UserPlaylist playlist) => From(new MusicItem.UserPlaylistRecord(playlist));

    public static string AlbumLine(AlbumSummary album)
    {
        var parts = new List<string>();
        if (album.Kind is { } kind) parts.Add(kind == ReleaseKind.Ep ? "EP" : kind.ToString());
        if (album.Year is { } year) parts.Add(year.ToString());
        if (album.ArtistLine.Length > 0) parts.Add(album.ArtistLine);
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// One row in the title bar's search suggestions. Query rows and rich music
/// rows share one template so their titles line up; the glyph, detail line and
/// trailing action tell them apart.
/// </summary>
public sealed record SuggestionRow(
    string Text,
    string Title,
    string Detail,
    string KindGlyph,
    Artwork? Artwork,
    CornerRadius ArtRadius,
    int ArtSize,
    int RowHeight,
    string TrailingGlyph,
    MusicItem? Item,
    YouTubeLink? Link,
    string? Query,
    bool ShowsDivider,
    string AutomationName,
    string HelpText)
{
    private const string SearchGlyph = "\uE721";
    private const string ClockGlyph = "\uE81C";
    private const string LinkGlyph = "\uE71B";
    private const string PlayGlyph = "\uE768";
    private const string ForwardGlyph = "\uE72A";
    private const string ChevronGlyph = "\uE76C";
    private const string SongGlyph = "\uE8D6";
    private const string AlbumGlyph = "\uE93C";
    private const string ArtistGlyph = "\uE77B";
    private const string VideoGlyph = "\uE714";

    public bool HasDetail => Detail.Length > 0;

    public bool HasTrailing => TrailingGlyph.Length > 0;

    public string ToolTip => Detail.Length > 0 ? $"{Title} · {Detail}" : Title;

    /// <summary>A confident search result: a track, album, artist or playlist.</summary>
    public static SuggestionRow From(MusicItem item, string query)
    {
        var title = item.Title;
        var detail = DetailOf(item);
        var action = item switch
        {
            MusicItem.SongRecord { Value.IsVideo: true } => "Press Enter to play the video",
            MusicItem.SongRecord => "Press Enter to play",
            _ => "Press Enter to open",
        };

        return new SuggestionRow(
            Text: title,
            Title: title,
            Detail: detail,
            KindGlyph: GlyphOf(item),
            Artwork: item.Artwork,
            ArtRadius: item is MusicItem.ArtistRecord ? new CornerRadius(20) : new CornerRadius(6),
            ArtSize: 40,
            RowHeight: 56,
            TrailingGlyph: item is MusicItem.SongRecord ? PlayGlyph : ChevronGlyph,
            Item: item,
            Link: null,
            Query: query,
            ShowsDivider: false,
            AutomationName: Automation(title, detail, action),
            HelpText: action);
    }

    /// <summary>A query expansion from YouTube.</summary>
    public static SuggestionRow QueryRow(string query) => new(
        Text: query,
        Title: query,
        Detail: "",
        KindGlyph: SearchGlyph,
        Artwork: null,
        ArtRadius: new CornerRadius(14),
        ArtSize: 28,
        RowHeight: 44,
        TrailingGlyph: "",
        Item: null,
        Link: null,
        Query: query,
        ShowsDivider: false,
        AutomationName: Automation(query, "", "Press Enter to search"),
        HelpText: "Press Enter to search");

    /// <summary>A recent search, shown when the field is empty.</summary>
    public static SuggestionRow Recent(string query) => new(
        Text: query,
        Title: query,
        Detail: "",
        KindGlyph: ClockGlyph,
        Artwork: null,
        ArtRadius: new CornerRadius(6),
        ArtSize: 28,
        RowHeight: 44,
        TrailingGlyph: "",
        Item: null,
        Link: null,
        Query: query,
        ShowsDivider: false,
        AutomationName: Automation(query, "", "Press Enter to search again"),
        HelpText: "Press Enter to search again");

    /// <summary>The way out of the quick answers and into the full result page.</summary>
    public static SuggestionRow SeeAll(string query) => new(
        Text: "See all results",
        Title: "See all results",
        Detail: $"“{query}”",
        KindGlyph: SearchGlyph,
        Artwork: null,
        ArtRadius: new CornerRadius(14),
        ArtSize: 28,
        RowHeight: 44,
        TrailingGlyph: ForwardGlyph,
        Item: null,
        Link: null,
        Query: query,
        ShowsDivider: false,
        AutomationName: Automation("See all results", $"“{query}”", "Press Enter to open the full results"),
        HelpText: "Press Enter to open the full results");

    /// <summary>A pasted YouTube or YouTube Music link.</summary>
    public static SuggestionRow LinkRow(YouTubeLink link) => new(
        Text: link.OpenTitle,
        Title: link.OpenTitle,
        Detail: "YouTube or YouTube Music link",
        KindGlyph: LinkGlyph,
        Artwork: null,
        ArtRadius: new CornerRadius(6),
        ArtSize: 28,
        RowHeight: 44,
        TrailingGlyph: ChevronGlyph,
        Item: null,
        Link: link,
        Query: null,
        ShowsDivider: false,
        AutomationName: Automation(link.OpenTitle, "YouTube or YouTube Music link", "Press Enter to open"),
        HelpText: "Press Enter to open");

    public SuggestionRow WithDivider() => this with { ShowsDivider = true };

    private static string GlyphOf(MusicItem item) => item switch
    {
        MusicItem.SongRecord { Value.IsVideo: true } => VideoGlyph,
        MusicItem.SongRecord => SongGlyph,
        MusicItem.AlbumRecord => AlbumGlyph,
        MusicItem.ArtistRecord => ArtistGlyph,
        _ => AlbumGlyph,
    };

    private static string DetailOf(MusicItem item) => item switch
    {
        MusicItem.SongRecord song => Join(
            song.Value.IsVideo ? "Video" : "Song",
            song.Value.ArtistLine,
            song.Value.Album?.Title),
        MusicItem.AlbumRecord album => Join(
            album.Value.Kind switch
            {
                ReleaseKind.Ep => "EP",
                ReleaseKind.Single => "Single",
                _ => "Album",
            },
            album.Value.ArtistLine,
            album.Value.Year?.ToString()),
        MusicItem.ArtistRecord artist => Join("Artist", artist.Value.Subtitle ?? ""),
        MusicItem.PlaylistRecord playlist => Join("Playlist", playlist.Value.Subtitle ?? ""),
        MusicItem.UserPlaylistRecord playlist => Join(
            "Your playlist",
            playlist.Value.Songs.Length == 1 ? "1 song" : $"{playlist.Value.Songs.Length} songs"),
        _ => "",
    };

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string Automation(string title, string detail, string action) =>
        detail.Length > 0 ? $"{title}. {detail}. {action}." : $"{title}. {action}.";
}

/// <summary>One shelf as drawn: its cards, and its songs as the queue a song card plays into.</summary>
public sealed record ShelfView(string Title, string? Strapline, CardItem[] Cards)
{
    public Song[] Songs { get; } = [.. Cards.Select(c => c.Item).OfType<MusicItem.SongRecord>().Select(s => s.Value)];

    public bool HasStrapline => !string.IsNullOrEmpty(Strapline);

    public static ShelfView From(Shelf shelf) => new(shelf.Title, shelf.Strapline, [.. shelf.Items.Select(CardItem.From)]);

    public static ShelfView Of(string title, IEnumerable<MusicItem> items) => new(title, null, [.. items.Select(CardItem.From)]);
}

/// <summary>A numbered track row (album and playlist tables).</summary>
public sealed record TrackRow(int Number, Song Song)
{
    public string Title => Song.Title;
    public string ArtistLine => Song.ArtistLine;
    public string AlbumTitle => Song.Album?.Title ?? "";
    public double? Duration => Song.Duration;
    public Artwork? Artwork => Song.Artwork;
    public bool IsVideo => Song.IsVideo;
}

/// <summary>A day in History: a heading row, then one row per play under it.</summary>
public sealed record HistoryDayRow(DateTimeOffset Day, int Plays)
{
    public string Label => Format.Day(Day);
    public string Detail => Format.Plays(Plays);
}

/// <summary>One play in History, with the day's songs so playing from here queues the day.</summary>
public sealed record HistoryPlayRow(PlayEvent Event, Song[] DaySongs, int Index)
{
    public Song Song => Event.Song;
    public string Title => Song.Title;
    public string ArtistLine => Song.ArtistLine;
    public string Subtitle => $"{Song.ArtistLine} · {Format.Time(Event.Date)}";
    public Artwork? Artwork => Song.Artwork;
    public bool IsVideo => Song.IsVideo;
    public double? Duration => Song.Duration;
}

/// <summary>A song in the Stats' all-time list, with its rank and play count.</summary>
public sealed record RankedSongRow(int Rank, SongStats Stats)
{
    public Song Song => Stats.Song;
    public string RankText => Rank.ToString();
    public string Title => Song.Title;
    public string ArtistLine => Song.ArtistLine;
    public string Subtitle => $"{Song.ArtistLine} · {Format.Plays(Stats.Plays)}";
    public Artwork? Artwork => Song.Artwork;
    public bool IsVideo => Song.IsVideo;
    public double? Duration => Song.Duration;
}

/// <summary>A lead artist in the Stats' all-time list, with its rank and totals.</summary>
public sealed record TopArtistRow(int Rank, string Name, ArtistRef? Ref, Artwork? Artwork, double Seconds, int Plays)
{
    public string RankText => Rank.ToString();
    public string Detail => $"{Format.Listening(Seconds)} · {Format.Plays(Plays)}";
    public bool HasArtist => Ref?.Id is not null;
}

/// <summary>History's two row shapes in one flat list: a day's heading, and a play.</summary>
public sealed partial class HistoryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Day { get; set; }
    public DataTemplate? Play { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) =>
        (item is HistoryDayRow ? Day : Play)!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>A mood or genre on Browse: its name on a quiet wash of its own colour.</summary>
public sealed record MoodCard(Mood Mood, string Title, Microsoft.UI.Xaml.Media.Brush Background);

