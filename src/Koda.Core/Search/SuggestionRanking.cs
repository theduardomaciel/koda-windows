using System.Globalization;
using System.Text;

namespace Koda.Core;

/// <summary>
/// Decides which of YouTube's rich search suggestions are confident enough to
/// sit above the query suggestions. The endpoint ranks its rows but does not
/// filter them: "trench" also returns "Trench Town" and "Trench Coat", and
/// "breach" returns an unrelated rap song. A rich row that does not match the
/// typed query is worse than a plain text suggestion, so this gate is
/// deliberately conservative.
/// </summary>
public static class SuggestionRanking
{
    /// <summary>The score a title or artist name has to reach to be promoted.</summary>
    public const double Threshold = 0.85;

    /// <summary>How many entity rows the search field may show.</summary>
    public const int MaxEntities = 3;

    private static readonly HashSet<string> GenericQueries =
    [
        "the", "a", "an", "and", "of", "to", "in", "on", "for", "with",
        "live", "love", "music", "song", "songs", "album", "albums",
        "artist", "artists", "best", "remix", "mix", "radio", "playlist",
        "playlists", "video", "videos", "new", "top", "hit", "hits",
    ];

    /// <summary>Promotes the rows that match <paramref name="query"/> closely enough, in display order.</summary>
    public static SearchSuggestion[] Rank(string? query, IEnumerable<MusicItem> items, int limit = MaxEntities)
    {
        var normalized = Normalize(query);
        if (normalized.Length == 0)
        {
            return [];
        }

        var candidates = items
            .OfType<MusicItem>()
            .Select((item, index) => new Candidate(item, index, ScoreNormalized(normalized, item)))
            .Where(candidate => candidate.Score >= Threshold)
            .GroupBy(candidate => DedupeKey(candidate.Item))
            .Select(group => group
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Index)
                .First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .Take(Math.Max(1, limit))
            .ToArray();

        return [.. candidates.Select(candidate => new SearchSuggestion(
            candidate.Item.Title,
            KindOf(candidate.Item),
            candidate.Item,
            candidate.Score))];
    }

    /// <summary>How strongly <paramref name="query"/> names this item, from 0 to 1.</summary>
    public static double Score(string? query, MusicItem item) =>
        ScoreNormalized(Normalize(query), item);

    /// <summary>
    /// Lowercase, accent-free words separated by single spaces. Both the query
    /// and the title go through this, so "Blurry Face" and "blurryface" compare
    /// honestly without making punctuation part of the score.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString();
    }

    private static double ScoreNormalized(string query, MusicItem item)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        var title = Normalize(item.Title);
        if (title.Length == 0)
        {
            return 0;
        }

        var score = TitleScore(query, title);
        if (score <= 0)
        {
            return 0;
        }

        if (item is MusicItem.SongRecord song)
        {
            if (song.Value.Artists.Any(artist => Normalize(artist.Name) == query))
            {
                score += 0.05;
            }

            if (song.Value.IsVideo)
            {
                score -= 0.05;
            }
        }

        var queryTokens = Tokens(query);
        var titleTokens = Tokens(title);
        if (titleTokens.Length - queryTokens.Length > 2)
        {
            score -= 0.08;
        }

        // "the" or "love" is too ambiguous to answer with one song, even when
        // a row happens to use exactly that word as its title. A two-letter
        // query is worse still.
        if (queryTokens.Length == 1 && query.Length < 4)
        {
            score = Math.Min(score, 0.60);
        }
        else if (GenericQueries.Contains(query))
        {
            score = Math.Min(score, 0.70);
        }

        return Math.Clamp(score, 0, 1);
    }

    private static double TitleScore(string query, string title)
    {
        if (title == query)
        {
            return 1.00;
        }

        var queryTokens = Tokens(query);
        var titleTokens = Tokens(title);

        // The same words in another order: "pilots twenty one" is the artist.
        if (queryTokens.Length == titleTokens.Length && AllTokensMatch(queryTokens, titleTokens, prefix: false))
        {
            return 0.90;
        }

        // Abbreviated words, in the same order: "twen one pilo" names the artist.
        if (queryTokens.Length >= 2 && queryTokens.Length <= titleTokens.Length)
        {
            var sameOrder = true;
            for (var i = 0; i < queryTokens.Length; i++)
            {
                if (!titleTokens[i].StartsWith(queryTokens[i], StringComparison.Ordinal))
                {
                    sameOrder = false;
                    break;
                }
            }

            if (sameOrder)
            {
                return 0.86;
            }
        }

        // The same number of words, each an abbreviation: "city wal" for "City Walls".
        if (queryTokens.Length == titleTokens.Length && AllTokensMatch(queryTokens, titleTokens, prefix: true))
        {
            return 0.86;
        }

        // A whole-word substring with extra title words: "breach" in a long
        // "Security Breach" title. Scored below the gate unless it is close.
        if (title.Contains($" {query} ", StringComparison.Ordinal)
            || title.StartsWith(query + " ", StringComparison.Ordinal)
            || title.EndsWith(" " + query, StringComparison.Ordinal))
        {
            return 0.80;
        }

        // One typo in a long title: "blurryfac" still finds "Blurryface".
        if (title.Length >= 6 && query.Length >= 5 && Levenshtein(title, query) <= 1)
        {
            return 0.87;
        }

        return 0;
    }

    private static bool AllTokensMatch(string[] queryTokens, string[] titleTokens, bool prefix)
    {
        var used = new bool[titleTokens.Length];
        foreach (var queryToken in queryTokens)
        {
            var found = false;
            for (var i = 0; i < titleTokens.Length; i++)
            {
                if (used[i])
                {
                    continue;
                }

                var matches = prefix
                    ? titleTokens[i].StartsWith(queryToken, StringComparison.Ordinal)
                    : titleTokens[i] == queryToken;
                if (!matches)
                {
                    continue;
                }

                used[i] = true;
                found = true;
                break;
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string DedupeKey(MusicItem item) => item switch
    {
        MusicItem.SongRecord song =>
            "track|" + Normalize(song.Value.Title) + "|" + Normalize(song.Value.ArtistLine),
        MusicItem.AlbumRecord album =>
            "album|" + Normalize(album.Value.Title) + "|" + Normalize(album.Value.ArtistLine),
        MusicItem.ArtistRecord artist => "artist|" + Normalize(artist.Value.Name),
        MusicItem.PlaylistRecord playlist => "playlist|" + Normalize(playlist.Value.Title),
        _ => item.Id,
    };

    private static SuggestionKind KindOf(MusicItem item) => item switch
    {
        MusicItem.SongRecord { Value.IsVideo: true } => SuggestionKind.Video,
        MusicItem.SongRecord => SuggestionKind.Song,
        MusicItem.AlbumRecord => SuggestionKind.Album,
        MusicItem.ArtistRecord => SuggestionKind.Artist,
        _ => SuggestionKind.Playlist,
    };

    private readonly record struct Candidate(MusicItem Item, int Index, double Score);

    private static int Levenshtein(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
