using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;

namespace Koda.Core;

/// <summary>A service lyrics can come from.</summary>
public enum LyricsSource
{
    /// <summary>Syllable-timed TTML from Better Lyrics' own provider, or lines when that is all it has.</summary>
    BetterLyrics,
    /// <summary>Community-synced lyrics, from word timing down to plain text.</summary>
    Unison,
    /// <summary>Word or line timing from Spicy Lyrics, using the listener's own key.</summary>
    SpicyLyrics,
    /// <summary>Apple Music lyrics from BiniLyrics' lrc.red catalogue.</summary>
    BiniLyrics,
    /// <summary>Word-timed QRC from Better Lyrics' QQ provider.</summary>
    BetterLyricsQQ,
    /// <summary>Word-by-word, with duets and backing vocals.</summary>
    YouLyPlus,
    /// <summary>NetEase's own timing: word when it has it, otherwise lines.</summary>
    NetEase,
    /// <summary>Community timing from SimpMusic: word when it has it, otherwise lines.</summary>
    SimpMusic,
    /// <summary>Line-by-line and plain lyrics, community-made.</summary>
    Lrclib,
    /// <summary>Line-timed LRC from Better Lyrics' KuGou provider.</summary>
    BetterLyricsKugou,
    /// <summary>Line-timed lyrics from KuGou.</summary>
    KuGou,
}

/// <summary>
/// Lyrics from several services, in the listener's priority order. All of them are
/// asked together; the answer with the finest timing wins (a word split into syllables
/// over word timing, over line timing, over plain text), and the priority order only
/// breaks a tie. When a coarser answer is found while a source that can do better is
/// still out, the coarser one waits for it for a short settling period.
///
/// Ported from the KodaCore LyricsClient.swift contract.
/// </summary>
public sealed class LyricsClient : IDisposable
{
    // The other public mirrors were retired (404, 402, 530) or now fail TLS validation.
    private static readonly string[] YouLyPlusMirrors =
    [
        "https://lyricsplus.binimum.org/",
    ];

    private const string BetterLyricsTtml = "https://api.betterlyrics.org/getLyrics";
    private const string BetterLyricsQq = "https://api.betterlyrics.org/qq/getLyrics";
    private const string BetterLyricsKugouUrl = "https://api.betterlyrics.org/kugou/getLyrics";
    private const string BiniLyricsSearch = "https://lrc.red/search.json";
    private const string BiniLyricsSong = "https://lrc.red/s/";
    private const string UnisonLyricsUrl = "https://unison.betterlyrics.org/lyrics";
    private const string SpicySearchUrl = "https://api.spicylyrics.org/v1/tracks/search";
    private const string SpicyLyricsUrl = "https://api.spicylyrics.org/v1/lyrics/";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(9);

    /// <summary>How long a source that can sync better than an answer already found may
    /// hold that answer up.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.2);

    /// <summary>Lyrics already found this session, so reopening the panel or replaying a song is instant.</summary>
    private static readonly ConcurrentDictionary<string, Lyrics> Cache = new(StringComparer.Ordinal);

    /// <summary>The same song asked for twice at once (the player's prefetch and the panel):
    /// both wait on one look, instead of asking every service twice.</summary>
    private readonly ConcurrentDictionary<string, Task<Lyrics?>> _loading = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly LyricsStore? _store;

    /// <param name="http">The session to use; a private one when null.</param>
    /// <param name="store">Where found lyrics are kept between launches; memory only when null.</param>
    public LyricsClient(HttpClient? http = null, LyricsStore? store = null)
    {
        _ownsHttp = http is null;
        _store = store;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 6,
        }) { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>How much room the lyrics cache takes on disk.</summary>
    public long CacheBytes => _store?.BytesUsed ?? 0;

    /// <summary>How many songs have lyrics kept on disk.</summary>
    public int CachedSongs => _store?.Count ?? 0;

    /// <summary>Lyrics for <paramref name="song"/> from the given services: the finest
    /// timing wins, and their order breaks a tie.</summary>
    public async Task<Lyrics?> LyricsFor(
        Song song,
        IReadOnlyList<LyricsSource>? sources = null,
        CancellationToken cancellation = default)
    {
        var wanted = sources ?? Settings.DefaultLyricsSources;
        if (wanted.Count == 0)
        {
            return null;
        }

        var key = song.Id + "|" + string.Join(",", wanted);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // The client is asked for the same song twice in quick succession (the player
        // prefetches when the song starts, the panel asks when it opens). Both share
        // one look, so the services are asked once and the cache is filled once.
        var loading = _loading.GetOrAdd(key, _ => Load(song, wanted, key));
        return await loading.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private async Task<Lyrics?> Load(Song song, IReadOnlyList<LyricsSource> wanted, string key)
    {
        try
        {
            // A song heard before opens from the disk, before any service is asked.
            if (_store is not null
                && await _store.ReadAsync(song.Id, wanted).ConfigureAwait(false) is { } kept)
            {
                Remember(key, kept);
                return kept;
            }

            var found = await Best(song, wanted, CancellationToken.None).ConfigureAwait(false);
            if (found is not null)
            {
                Remember(key, found);
                if (_store is not null)
                {
                    await _store.WriteAsync(song.Id, wanted, found).ConfigureAwait(false);
                }
            }

            return found;
        }
        finally
        {
            _loading.TryRemove(key, out _);
        }
    }

    /// <summary>Keeps the answer for the rest of the session, letting the oldest go past a limit.</summary>
    private static void Remember(string key, Lyrics lyrics)
    {
        Cache[key] = lyrics;
        if (Cache.Count > 60)
        {
            // The cache is a convenience, not a store.
            foreach (var entry in Cache.Take(Cache.Count - 60))
            {
                Cache.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>Forget lyrics found this session, so the next look asks the services again.
    /// The disk keeps what it had: that is what makes a song heard days ago open instantly.</summary>
    public static void ForgetSession() => Cache.Clear();

    /// <summary>Empty both the session's lyrics and the ones kept on disk.</summary>
    public void ClearCache()
    {
        Cache.Clear();
        _store?.Clear();
    }

    // MARK: Choosing

    private async Task<Lyrics?> Best(Song song, IReadOnlyList<LyricsSource> sources, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(Deadline);

        var running = new List<(int Rank, Task<(int Rank, Lyrics? Lyrics)> Task)>(sources.Count);
        for (var rank = 0; rank < sources.Count; rank++)
        {
            var source = sources[rank];
            running.Add((rank, Ask(rank, source, song, deadline.Token)));
        }

        var answers = new SortedDictionary<int, Lyrics>();
        var pending = new HashSet<int>(Enumerable.Range(0, sources.Count));
        Task? settle = null;
        try
        {
            while (pending.Count > 0)
            {
                // Completed answers are still waiting to be recorded, so they join the set.
                var waiting = running
                    .Where(entry => pending.Contains(entry.Rank))
                    .Select(entry => entry.Task)
                    .ToList();
                if (waiting.Count == 0)
                {
                    break;
                }

                var next = Task.WhenAny(waiting);
                if (settle is not null)
                {
                    // A source slower than the settle period does not keep the listener waiting:
                    // the best answer found so far is shown instead.
                    var first = await Task.WhenAny(next, settle).WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (first == settle)
                    {
                        break;
                    }
                }

                var finished = await next.WaitAsync(deadline.Token).ConfigureAwait(false);
                var (rank, lyrics) = await finished.ConfigureAwait(false);
                pending.Remove(rank);
                if (lyrics is not null)
                {
                    answers[rank] = lyrics;
                    settle ??= Task.Delay(Settle);
                }

                // The finest-timed answer wins once no waiting source could beat it.
                if (answers.Count > 0 && !CouldImprove(answers, pending, sources))
                {
                    return BestAnswer(answers)?.Lyrics;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The deadline passed: the best-ranked answer so far is the answer.
        }

        return BestAnswer(answers)?.Lyrics;
    }

    /// <summary>How finely a source can time its lyrics: the providers that can sync
    /// words or syllables can improve on a line-timed answer, however low they rank;
    /// a line-only provider never can.</summary>
    internal static int MaxSyncLevel(LyricsSource source) => source switch
    {
        LyricsSource.Lrclib or LyricsSource.BetterLyricsKugou or LyricsSource.KuGou => 1,
        _ => 3,
    };

    /// <summary>The answer to show: the finest timing first, the higher-ranked source
    /// when two are timed equally.</summary>
    internal static (int Rank, Lyrics Lyrics)? BestAnswer(IEnumerable<KeyValuePair<int, Lyrics>> answers)
    {
        (int Rank, Lyrics Lyrics)? best = null;
        foreach (var pair in answers)
        {
            if (best is not { } current ||
                pair.Value.SyncLevel > current.Lyrics.SyncLevel ||
                (pair.Value.SyncLevel == current.Lyrics.SyncLevel && pair.Key < current.Rank))
            {
                best = (pair.Key, pair.Value);
            }
        }

        return best;
    }

    /// <summary>Whether a source still out could produce a finer answer, or the same
    /// timing from a source the listener ranks higher.</summary>
    internal static bool CouldImprove(
        IEnumerable<KeyValuePair<int, Lyrics>> answers,
        IEnumerable<int> pending,
        IReadOnlyList<LyricsSource> sources)
    {
        if (BestAnswer(answers) is not { } best)
        {
            return pending.Any();
        }

        foreach (var rank in pending)
        {
            var level = MaxSyncLevel(sources[rank]);
            if (level > best.Lyrics.SyncLevel ||
                (level == best.Lyrics.SyncLevel && rank < best.Rank))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<(int Rank, Lyrics? Lyrics)> Ask(int rank, LyricsSource source, Song song, CancellationToken cancellation)
    {
        Lyrics? lyrics = source switch
        {
            LyricsSource.BetterLyrics => await BetterLyrics(song, BetterLyricsTtml, cancellation).ConfigureAwait(false),
            LyricsSource.Unison => await Unison(song, cancellation).ConfigureAwait(false),
            LyricsSource.SpicyLyrics => await Spicy(song, cancellation).ConfigureAwait(false),
            LyricsSource.BiniLyrics => await BiniLyrics(song, cancellation).ConfigureAwait(false),
            LyricsSource.BetterLyricsQQ => await BetterLyrics(song, BetterLyricsQq, cancellation).ConfigureAwait(false),
            LyricsSource.YouLyPlus => await YouLyPlus(song, cancellation).ConfigureAwait(false),
            LyricsSource.NetEase => await NetEase(song, cancellation).ConfigureAwait(false),
            LyricsSource.SimpMusic => await SimpMusic(song, cancellation).ConfigureAwait(false),
            LyricsSource.Lrclib => await Lrclib(song, cancellation).ConfigureAwait(false),
            LyricsSource.BetterLyricsKugou => await BetterLyrics(song, BetterLyricsKugouUrl, cancellation).ConfigureAwait(false),
            LyricsSource.KuGou => await KuGou(song, cancellation).ConfigureAwait(false),
            _ => null,
        };

        return (rank, lyrics);
    }

    // MARK: YouLyPlus

    private async Task<Lyrics?> YouLyPlus(Song song, CancellationToken cancellation)
    {
        var artist = song.ArtistLine;
        if (artist.Length == 0)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>
        {
            ["title"] = song.Title,
            ["artist"] = artist,
        };
        if (song.Album is { } album && album.Title.Length > 0)
        {
            parameters["album"] = album.Title;
        }

        if (song.Duration is { } duration && duration > 0)
        {
            parameters["duration"] = ((int)Math.Round(duration)).ToString();
        }

        var found = await Race(
            YouLyPlusMirrors.Select(mirror => mirror + "v2/lyrics/get").ToList(),
            parameters,
            data =>
            {
                var lines = LyricsFormats.ParseYouLyPlus(Json.Parse(Encoding.UTF8.GetString(data)));
                return lines is null ? null : Lyrics.Synced(lines);
            },
            cancellation).ConfigureAwait(false);
        if (found is not null)
        {
            return found;
        }

        return await Race(
            YouLyPlusMirrors.Select(mirror => mirror + "v1/ttml/get").ToList(),
            parameters,
            DecodeWrapped,
            cancellation).ConfigureAwait(false);
    }

    /// <summary>The first mirror to answer with lyrics; the others are cancelled.</summary>
    private async Task<Lyrics?> Race(
        IReadOnlyList<string> urls,
        IReadOnlyDictionary<string, string> parameters,
        Func<byte[], Lyrics?> decode,
        CancellationToken cancellation)
    {
        using var winner = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var tasks = urls.Select(async url =>
        {
            var data = await Fetch(url, parameters, winner.Token).ConfigureAwait(false);
            return data is null ? null : decode(data);
        }).ToList();

        var completed = await Task.WhenAny(tasks).WaitAsync(winner.Token).ConfigureAwait(false);
        var result = await completed.ConfigureAwait(false);
        if (result is not null)
        {
            winner.Cancel();
            return result;
        }

        // The first answer was nothing; wait for the rest.
        foreach (var task in tasks)
        {
            var lyrics = await task.ConfigureAwait(false);
            if (lyrics is not null)
            {
                winner.Cancel();
                return lyrics;
            }
        }

        return null;
    }

    // MARK: BetterLyrics

    /// <summary>One of Better Lyrics' providers: TTML, QQ's QRC or KuGou's LRC.</summary>
    private async Task<Lyrics?> BetterLyrics(Song song, string endpoint, CancellationToken cancellation)
    {
        var artist = song.ArtistLine;
        if (artist.Length == 0)
        {
            return null;
        }

        // The API's cache key includes every one of these, so they are always sent.
        var parameters = new Dictionary<string, string>
        {
            ["s"] = song.Title,
            ["a"] = artist,
            ["al"] = song.Album?.Title ?? "",
            ["d"] = song.Duration is { } duration && duration > 0
                ? ((int)Math.Round(duration)).ToString(CultureInfo.InvariantCulture)
                : "0",
        };

        if (endpoint == BetterLyricsTtml && song.Id.Length > 0)
        {
            parameters["videoId"] = song.Id;
        }

        var data = await Fetch(endpoint, parameters, cancellation).ConfigureAwait(false);
        return data is null ? null : DecodeWrapped(data);
    }

    // MARK: Spicy Lyrics

    /// <summary>Spicy's search finds the track (it is keyed by a streaming id, not a title),
    /// then one request returns either syllable or line timing. The listener supplies the key.</summary>
    private async Task<Lyrics?> Spicy(Song song, CancellationToken cancellation)
    {
        var key = ForkConfig.SpicyLyricsKey;
        if (string.IsNullOrWhiteSpace(key) || song.ArtistLine.Length == 0)
        {
            return null;
        }

        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key.Trim() };
        var search = new Dictionary<string, string>
        {
            ["title"] = song.Title,
            ["artist"] = song.ArtistLine,
            ["limit"] = "5",
        };
        if (song.Album is { } album && album.Title.Length > 0)
        {
            search["album"] = album.Title;
        }

        if (song.Duration is { } duration && duration > 0)
        {
            search["duration"] = ((int)Math.Round(duration)).ToString(CultureInfo.InvariantCulture);
        }

        if (await Fetch(SpicySearchUrl, search, cancellation, headers: headers).ConfigureAwait(false) is not { } searchData)
        {
            return null;
        }

        Json root;
        try
        {
            root = Json.Parse(Encoding.UTF8.GetString(searchData));
        }
        catch (Exception)
        {
            return null;
        }

        var results = root["Body"]["results"].Array is { Length: > 0 } body ? body : root["results"].Array;
        Json? chosen = null;
        Json? fallback = null;
        foreach (var result in results)
        {
            if (string.Equals(result["tier"].String, "exact", StringComparison.OrdinalIgnoreCase))
            {
                chosen = result;
                break;
            }

            if (fallback is null && CloseEnough(result, song))
            {
                fallback = result;
            }
        }

        chosen ??= fallback;
        if (chosen is not { } track || track["id"].NonEmpty is not { } id)
        {
            return null;
        }

        var url = SpicyLyricsUrl + Uri.EscapeDataString(id);
        if (await Fetch(url, new Dictionary<string, string>(), cancellation, headers: headers).ConfigureAwait(false) is not { } lyricsData)
        {
            return null;
        }

        try
        {
            var lyricsRoot = Json.Parse(Encoding.UTF8.GetString(lyricsData));
            return LyricsFormats.ParseSpicy(lyricsRoot)?.WithCredits(LyricsFormats.SpicyCredits(lyricsRoot));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>An inexact search result is used only when its length is the same recording.</summary>
    private static bool CloseEnough(Json result, Song song) =>
        song.Duration is not { } wanted ||
        result["duration"].Double is not { } seconds ||
        seconds <= 0 ||
        Math.Abs(seconds - wanted) <= 3;

    // MARK: BiniLyrics

    /// <summary>lrc.red's catalogue: search for a matching release, then take its TTML
    /// (or LRC when the release has no word timing).</summary>
    private async Task<Lyrics?> BiniLyrics(Song song, CancellationToken cancellation)
    {
        if (song.ArtistLine.Length == 0)
        {
            return null;
        }

        var search = new Dictionary<string, string> { ["q"] = $"{song.Title} {song.ArtistLine}" };
        if (await Fetch(BiniLyricsSearch, search, cancellation).ConfigureAwait(false) is not { } data)
        {
            return null;
        }

        Json root;
        try
        {
            root = Json.Parse(Encoding.UTF8.GetString(data));
        }
        catch (Exception)
        {
            return null;
        }

        var candidates = root["hits"].Array
            .Where(hit => MatchesBiniLyrics(hit, song))
            .OrderBy(hit => DurationDelta(hit, song))
            .Take(3);

        foreach (var hit in candidates)
        {
            if (hit["isrc"].NonEmpty is not { } isrc)
            {
                continue;
            }

            foreach (var extension in new[] { "ttml", "lrc" })
            {
                var body = await Fetch($"{BiniLyricsSong}{Uri.EscapeDataString(isrc)}.{extension}",
                    new Dictionary<string, string>(), cancellation).ConfigureAwait(false);
                if (body is not null && DecodeWrapped(body) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>A search hit is taken only when its title and one credited artist match,
    /// and its length is close enough to be the same recording.</summary>
    internal static bool MatchesBiniLyrics(Json hit, Song song)
    {
        if (!SameTitle(hit["title"].String, song.Title) ||
            !SharesArtist(hit["artist"].String, song.ArtistLine))
        {
            return false;
        }

        return song.Duration is not { } wanted ||
               hit["duration"].Double is not { } seconds ||
               seconds <= 0 ||
               Math.Abs(seconds - wanted) <= 8;
    }

    private static double DurationDelta(Json hit, Song song) =>
        song.Duration is { } wanted && hit["duration"].Double is { } seconds && seconds > 0
            ? Math.Abs(seconds - wanted)
            : double.MaxValue;

    private static bool SameTitle(string? candidate, string title) =>
        candidate is not null &&
        string.Equals(NormalizeTitle(candidate), NormalizeTitle(title), StringComparison.Ordinal);

    private static bool SharesArtist(string? candidate, string artist)
    {
        if (candidate is null)
        {
            return false;
        }

        var wanted = NormalizeWords(artist);
        if (wanted.Length == 0)
        {
            return false;
        }

        foreach (var credit in candidate.Split([',', '&', '/', ';', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = NormalizeWords(credit);
            if (name.Length == 0)
            {
                continue;
            }

            if (name == wanted ||
                name.StartsWith(wanted + " ", StringComparison.Ordinal) ||
                wanted.StartsWith(name + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Case and punctuation do not decide a match; the words do.</summary>
    private static string NormalizeWords(string text) =>
        string.Join(' ', System.Text.RegularExpressions.Regex
            .Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A release label such as "(Remastered 2011)" is not part of the song's name.</summary>
    private static string NormalizeTitle(string title) =>
        NormalizeWords(System.Text.RegularExpressions.Regex.Replace(
            title, @"[\(\[（「『<《〈＜].*?[\)\]）)」』>》〉＞]", " "));

    /// <summary>TTML or LRC as the body, or inside a JSON object under a key such as <c>ttml</c> or <c>lyrics</c>.</summary>
    internal static Lyrics? DecodeWrapped(byte[] data)
    {
        var body = Encoding.UTF8.GetString(data).Trim();
        if (body.Length == 0)
        {
            return null;
        }

        if (body.StartsWith('<'))
        {
            return LyricsFormats.ParseAny(body);
        }

        Json json;
        try
        {
            json = Json.Parse(body);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var key in new[] { "ttml", "lyrics", "lrc", "content", "text" })
        {
            if (json[key].NonEmpty is { } text && LyricsFormats.ParseAny(text) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // MARK: LRCLIB

    private async Task<Lyrics?> Lrclib(Song song, CancellationToken cancellation)
    {
        var exact = new Dictionary<string, string>
        {
            ["artist_name"] = song.ArtistLine,
            ["track_name"] = song.Title,
        };
        if (song.Album is { } album)
        {
            exact["album_name"] = album.Title;
        }

        if (song.Duration is { } duration)
        {
            exact["duration"] = ((int)Math.Round(duration)).ToString();
        }

        if (await Fetch("https://lrclib.net/api/get", exact, cancellation).ConfigureAwait(false) is { } data &&
            LyricsOf(Json.Parse(Encoding.UTF8.GetString(data))) is { } exactLyrics)
        {
            return exactLyrics;
        }

        var search = new Dictionary<string, string>
        {
            ["artist_name"] = song.ArtistLine,
            ["track_name"] = song.Title,
        };
        if (await Fetch("https://lrclib.net/api/search", search, cancellation).ConfigureAwait(false) is not { } searchData)
        {
            return null;
        }

        Json found;
        try
        {
            found = Json.Parse(Encoding.UTF8.GetString(searchData));
        }
        catch (Exception)
        {
            return null;
        }

        // A different cut of the song (live, extended) is timed for a different recording.
        var candidates = found.Array
            .Where(item => song.Duration is not { } wanted || item["duration"].Double is not { } other ||
                Math.Abs(other - wanted) <= 10)
            .ToList();
        var best = candidates.FirstOrDefault(item => item["syncedLyrics"].NonEmpty is not null);
        if (best.Raw.ValueKind == System.Text.Json.JsonValueKind.Undefined && candidates.Count > 0)
        {
            best = candidates[0];
        }

        return best.Raw.ValueKind == System.Text.Json.JsonValueKind.Undefined ? null : LyricsOf(best);
    }

    internal static Lyrics? LyricsOf(Json item)
    {
        if (item["syncedLyrics"].NonEmpty is { } synced)
        {
            var lines = Lyrics.ParseLrc(synced);
            if (lines.Count > 0)
            {
                return Lyrics.Synced(lines);
            }
        }

        if (item["plainLyrics"].NonEmpty is { } plain)
        {
            return Lyrics.Unsynced(plain);
        }

        return null;
    }

    // MARK: NetEase

    /// <summary>NetEase's public web API: cloud search, then the lyric endpoint's word or
    /// line timing. It needs the site's referer, but no key.</summary>
    private async Task<Lyrics?> NetEase(Song song, CancellationToken cancellation)
    {
        if (song.ArtistLine.Length == 0)
        {
            return null;
        }

        var search = new Dictionary<string, string>
        {
            ["s"] = $"{song.Title} {song.ArtistLine}",
            ["type"] = "1",
            ["limit"] = "10",
            ["offset"] = "0",
        };
        if (await Fetch("https://music.163.com/api/cloudsearch/pc", search, cancellation,
                headers: NetEaseHeaders).ConfigureAwait(false) is not { } searchData)
        {
            return null;
        }

        Json root;
        try
        {
            root = Json.Parse(Encoding.UTF8.GetString(searchData));
        }
        catch (Exception)
        {
            return null;
        }

        Json? best = null;
        var bestDelta = double.MaxValue;
        foreach (var item in root["result"]["songs"].Array)
        {
            var milliseconds = item["dt"].Double ?? item["duration"].Double ?? 0;
            var delta = song.Duration is { } wanted && milliseconds > 0
                ? Math.Abs(milliseconds / 1000 - wanted)
                : 0;
            if (delta > 10 || delta >= bestDelta)
            {
                continue;
            }

            best = item;
            bestDelta = delta;
        }

        if (best is not { } chosen || IdOf(chosen) is not { } id)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>
        {
            ["id"] = id,
            ["lv"] = "1",
            ["kv"] = "1",
            ["tv"] = "-1",
        };
        if (await Fetch("https://music.163.com/api/song/lyric", parameters, cancellation,
                headers: NetEaseHeaders).ConfigureAwait(false) is not { } lyricsData)
        {
            return null;
        }

        try
        {
            var lyrics = Json.Parse(Encoding.UTF8.GetString(lyricsData));
            var text = lyrics["klyric"]["lyric"].NonEmpty ?? lyrics["lrc"]["lyric"].NonEmpty;
            return text is null ? null : LyricsFormats.ParseAny(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly Dictionary<string, string> NetEaseHeaders = new()
    {
        ["Referer"] = "https://music.163.com/",
        ["Origin"] = "https://music.163.com",
    };

    // MARK: SimpMusic

    private async Task<Lyrics?> SimpMusic(Song song, CancellationToken cancellation)
    {
        if (song.Id.Length == 0)
        {
            return null;
        }

        var url = "https://api-lyrics.simpmusic.org/v1/" + Uri.EscapeDataString(song.Id);
        if (await Fetch(url, new Dictionary<string, string>(), cancellation,
                userAgent: "SimpMusicLyrics/1.0").ConfigureAwait(false) is not { } data)
        {
            return null;
        }

        Json root;
        try
        {
            root = Json.Parse(Encoding.UTF8.GetString(data));
        }
        catch (Exception)
        {
            return null;
        }

        if (!string.Equals(root["type"].String, "success", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var items = root["data"].Array;
        if (items.Length == 0)
        {
            return null;
        }

        Json? best = null;
        var bestDelta = double.MaxValue;
        foreach (var item in items)
        {
            var seconds = item["durationSeconds"].Double ?? 0;
            var delta = song.Duration is { } wanted && seconds > 0 ? Math.Abs(seconds - wanted) : 0;
            if (delta > 12 || delta >= bestDelta)
            {
                continue;
            }

            best = item;
            bestDelta = delta;
        }

        best ??= items[0];
        foreach (var key in new[] { "richSyncLyrics", "syncedLyrics", "plainLyric" })
        {
            if (best.Value[key].NonEmpty is { } text && LyricsFormats.ParseAny(text) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // MARK: KuGou

    private async Task<Lyrics?> KuGou(Song song, CancellationToken cancellation)
    {
        var keyword = $"{CleanTitle(song.Title)} - {CleanArtist(song.ArtistLine)}";

        // The song catalogue knows the exact hash, which is a better match than a text search.
        var search = new Dictionary<string, string>
        {
            ["version"] = "9108",
            ["plat"] = "0",
            ["pagesize"] = "8",
            ["showtype"] = "0",
            ["keyword"] = keyword,
        };
        if (await Fetch("https://mobileservice.kugou.com/api/v3/search/song", search, cancellation).ConfigureAwait(false) is { } data)
        {
            Json root;
            try
            {
                root = Json.Parse(Encoding.UTF8.GetString(data));
            }
            catch (Exception)
            {
                root = Json.Null;
            }

            foreach (var item in root["data"]["info"].Array)
            {
                var seconds = item["duration"].Double ?? 0;
                if (song.Duration is { } wanted && seconds > 0 && Math.Abs(seconds - wanted) > 8)
                {
                    continue;
                }

                if (item["hash"].NonEmpty is { } hash &&
                    await KuGouLyrics(new Dictionary<string, string> { ["hash"] = hash }, cancellation).ConfigureAwait(false) is { } found)
                {
                    return found;
                }
            }
        }

        var fallback = new Dictionary<string, string> { ["keyword"] = keyword };
        if (song.Duration is { } duration && duration > 0)
        {
            fallback["duration"] = ((int)Math.Round(duration * 1000)).ToString(CultureInfo.InvariantCulture);
        }

        return await KuGouLyrics(fallback, cancellation).ConfigureAwait(false);
    }

    /// <summary>The KuGou lyrics service: search for a candidate, then download its base64 LRC.</summary>
    private async Task<Lyrics?> KuGouLyrics(Dictionary<string, string> search, CancellationToken cancellation)
    {
        var parameters = new Dictionary<string, string>(search)
        {
            ["ver"] = "1",
            ["man"] = "yes",
            ["client"] = "pc",
        };
        if (await Fetch("https://lyrics.kugou.com/search", parameters, cancellation).ConfigureAwait(false) is not { } searchData)
        {
            return null;
        }

        Json candidate;
        try
        {
            candidate = Json.Parse(Encoding.UTF8.GetString(searchData))["candidates"].Array.FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }

        if (candidate.Raw.ValueKind != System.Text.Json.JsonValueKind.Object ||
            IdOf(candidate) is not { } id || candidate["accesskey"].NonEmpty is not { } accessKey)
        {
            return null;
        }

        var download = new Dictionary<string, string>
        {
            ["fmt"] = "lrc",
            ["charset"] = "utf8",
            ["client"] = "pc",
            ["ver"] = "1",
            ["id"] = id,
            ["accesskey"] = accessKey,
        };
        if (await Fetch("https://lyrics.kugou.com/download", download, cancellation).ConfigureAwait(false) is not { } lyricsData)
        {
            return null;
        }

        try
        {
            var content = Json.Parse(Encoding.UTF8.GetString(lyricsData))["content"].NonEmpty;
            if (content is null)
            {
                return null;
            }

            return LyricsFormats.ParseAny(Encoding.UTF8.GetString(Convert.FromBase64String(content)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CleanTitle(string title) =>
        System.Text.RegularExpressions.Regex
            .Replace(title, "[（(「『<《〈＜].*?[）)」』>》〉＞]", "")
            .Trim();

    private static string CleanArtist(string artist) =>
        artist.Replace(", ", "、").Replace(" & ", "、").Replace(".", "").Trim();

    // MARK: Unison

    /// <summary>Unison is keyed by video first, then by song and artist.</summary>
    private async Task<Lyrics?> Unison(Song song, CancellationToken cancellation)
    {
        if (song.Id.Length > 0 &&
            await UnisonLyrics(new Dictionary<string, string> { ["v"] = song.Id }, cancellation).ConfigureAwait(false) is { } byVideo)
        {
            return byVideo;
        }

        if (song.ArtistLine.Length == 0)
        {
            return null;
        }

        var search = new Dictionary<string, string>
        {
            ["song"] = song.Title,
            ["artist"] = song.ArtistLine,
        };
        if (song.Album is { } album && album.Title.Length > 0)
        {
            search["album"] = album.Title;
        }

        if (song.Duration is { } duration && duration > 0)
        {
            search["duration"] = ((int)Math.Round(duration)).ToString(CultureInfo.InvariantCulture);
        }

        return await UnisonLyrics(search, cancellation).ConfigureAwait(false);
    }

    private async Task<Lyrics?> UnisonLyrics(Dictionary<string, string> parameters, CancellationToken cancellation)
    {
        if (await Fetch(UnisonLyricsUrl, parameters, cancellation).ConfigureAwait(false) is not { } data)
        {
            return null;
        }

        try
        {
            var root = Json.Parse(Encoding.UTF8.GetString(data));
            if (root["success"].Bool != true)
            {
                return null;
            }

            var text = root["data"]["lyrics"].NonEmpty;
            return text is null
                ? null
                : LyricsFormats.ParseAny(text)?.WithCredits(LyricsFormats.UnisonCredits(root["data"]));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Provider ids arrive as numbers or strings.</summary>
    private static string? IdOf(Json item)
    {
        if (item["id"].String is { Length: > 0 } text)
        {
            return text;
        }

        return item["id"].Double is { } number ? ((long)number).ToString(CultureInfo.InvariantCulture) : null;
    }

    // MARK: Network

    private async Task<byte[]?> Fetch(
        string url,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellation,
        string? userAgent = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var query = string.Join("&", parameters
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var separator = url.Contains('?') ? '&' : '?';
        var full = query.Length == 0 ? url : $"{url}{separator}{query}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, full);
            request.Headers.TryAddWithoutValidation("User-Agent",
                userAgent ?? $"Koda for Windows ({ForkConfig.RepoUrl}; {ForkConfig.IssuesUrl})");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, application/xml, text/plain, */*");
            if (headers is not null)
            {
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
            return data.Length == 0 ? null : data;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

