using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Koda.Core.Video;
using VideoModel = Koda.Core.Video.Video;

namespace Koda.Core;

/// <summary>A playlist the listener made.</summary>
public sealed record UserPlaylist(Guid Id, string Name, Song[] Songs, DateTimeOffset Created, DateTimeOffset Modified)
{
    public static UserPlaylist Create(string name, Song[] songs, DateTimeOffset created) =>
        new(Guid.NewGuid(), name, songs, created, created);

    public double Duration => Songs.Sum(s => s.Duration ?? 0);
}

/// <summary>How much one song has been played.</summary>
public sealed record SongStats(Song Song, int Plays, double Seconds, DateTimeOffset FirstPlayed, DateTimeOffset LastPlayed)
{
    public static SongStats New(Song song, DateTimeOffset at) => new(song, 0, 0, at, at);
}

/// <summary>One play, with when it happened: the dated history behind the History page.</summary>
public sealed record PlayEvent(Guid Id, Song Song, DateTimeOffset Date)
{
    public static PlayEvent New(Song song, DateTimeOffset at) => new(Guid.NewGuid(), song, at);
}

/// <summary>What an Android import added, to tell the listener.</summary>
public sealed record AndroidImportSummary(int LikedSongs = 0, int Plays = 0, int Playlists = 0, int Saved = 0, int Searches = 0,
    int Subscriptions = 0, int Watched = 0, int VideoPlaylists = 0, int Blocked = 0)
{
    public bool IsEmpty => LikedSongs + Plays + Playlists + Saved + Searches + Subscriptions + Watched + VideoPlaylists + Blocked == 0;
}

public sealed class LibraryBackupException() : Exception("This file isn't a Koda library backup.");

/// <summary>
/// What the listener asked Koda to suggest less: songs, and artists whose songs they don't
/// want offered. Applies to what Koda suggests (Home, mixes, radio and autoplay), never to
/// what the listener picks themselves. The Swift <c>NotInterested</c>, field for field.
/// </summary>
public sealed class NotInterested
{
    /// <summary>Newest first.</summary>
    public List<Song> Songs { get; set; } = [];
    public List<ArtistRef> Artists { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Songs.Count == 0 && Artists.Count == 0;

    public bool Hides(Song song)
    {
        if (Songs.Exists(s => s.Id == song.Id)) return true;
        if (Artists.Count == 0) return false;
        // Linked artists by channel id; a byline YouTube didn't link is matched on the whole line.
        var credited = song.Artists.Length == 0 ? [new ArtistRef(null, song.ArtistLine)] : song.Artists;
        return credited.Any(Hides);
    }

    public bool Hides(ArtistRef artist) => Artists.Exists(a => Same(a, artist));

    /// <summary>The same artist: by channel id when both have one, otherwise by name.</summary>
    public static bool Same(ArtistRef a, ArtistRef b)
    {
        if (a.Id is { } x && b.Id is { } y) return x == y;
        return string.Compare(a.Name, b.Name, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) == 0;
    }

    public NotInterested Copy() => new() { Songs = [.. Songs], Artists = [.. Artists] };
}

/// <summary>
/// What the listener keeps: songs they liked, albums, playlists
/// and artists they saved, playlists they made, what they played and searched,
/// and how long they listened. One JSON file, written after each change.
/// Ported from KodaCore Library.swift. The Swift class is @MainActor; this one
/// takes a lock instead, so it may be called from any thread, and raises
/// <see cref="Changed"/> after each change (off the caller's lock).
/// </summary>
public sealed class Library
{
    private const int RecentLimit = 200;
    private const int SearchLimit = 20;
    private const int HistoryLimit = 3000;
    private const int WatchedLimit = 200;
    /// <summary>The iOS backup format name, kept so one backup restores on either platform.</summary>
    private const string BackupFormat = "koda-ios-library";

    private readonly object _lock = new();
    private string? _file;
    private string? _historyFile;
    private readonly List<PlayEvent> _history = [];
    private Snapshot _s = new();
    /// <summary>Listening time arrives every few seconds; it is written with the next change or <see cref="Flush"/>.</summary>
    private bool _hasUnsavedListening;
    private int _historySaveVersion;

    public event Action? Changed;

    public Library(string? directory)
    {
        _file = directory is null ? null : Path.Combine(directory, "library.json");
        _historyFile = directory is null ? null : Path.Combine(directory, "history.json");
        Load();
        LoadHistory();
    }

    /// <summary>The folder the library and its history are open on, for the channel folder
    /// reconciliation.</summary>
    public string OpenFolder => _file is null ? "" : Path.GetDirectoryName(_file) ?? "";

    /// <summary>The library of another profile takes over: this one is written out, the other
    /// read in, and everything drawn from it redraws.</summary>
    public void Open(string directory)
    {
        Flush();
        WriteHistoryNow();
        lock (_lock)
        {
            // A history write still waiting belongs to the library just closed.
            _historySaveVersion++;
            _file = Path.Combine(directory, "library.json");
            _historyFile = Path.Combine(directory, "history.json");
            _s = new Snapshot();
            _history.Clear();
            _hasUnsavedListening = false;
            Load();
            LoadHistory();
        }

        Changed?.Invoke();
    }

    // MARK: Reading

    public Song[] Liked { get { lock (_lock) return [.. _s.Liked]; } }
    public AlbumSummary[] Albums { get { lock (_lock) return [.. _s.Albums]; } }
    public PlaylistSummary[] Playlists { get { lock (_lock) return [.. _s.Playlists]; } }
    public ArtistSummary[] Artists { get { lock (_lock) return [.. _s.Artists]; } }
    /// <summary>Most recent first, one entry per song.</summary>
    public Song[] Recent { get { lock (_lock) return [.. _s.Recent]; } }
    public string[] Searches { get { lock (_lock) return [.. _s.Searches]; } }
    /// <summary>Playlists made here, newest first.</summary>
    public UserPlaylist[] UserPlaylists { get { lock (_lock) return [.. _s.UserPlaylists]; } }
    /// <summary>Play counts and listening time per song, by video id.</summary>
    public IReadOnlyDictionary<string, SongStats> SongStats { get { lock (_lock) return new Dictionary<string, SongStats>(_s.SongStats); } }
    /// <summary>Seconds listened per day, keyed yyyy-MM-dd in the device's time zone.</summary>
    public IReadOnlyDictionary<string, double> DailyListening { get { lock (_lock) return new Dictionary<string, double>(_s.DailyListening); } }
    public IReadOnlyDictionary<string, double> LyricsOffsets { get { lock (_lock) return new Dictionary<string, double>(_s.LyricsOffsets); } }
    /// <summary>Every play, newest first, under the day it happened.</summary>
    public PlayEvent[] History { get { lock (_lock) return [.. _history]; } }

    // MARK: Songs

    public bool IsLiked(Song song) { lock (_lock) return _s.Liked.Exists(s => s.Id == song.Id); }

    public void ToggleLike(Song song) => Edit(s =>
    {
        var index = s.Liked.FindIndex(x => x.Id == song.Id);
        if (index >= 0) s.Liked.RemoveAt(index);
        else s.Liked.Insert(0, song);
    });

    /// <summary>Remembers that a song was played: in the recently played list, and in the stats.</summary>
    public void RecordPlay(Song song, DateTimeOffset? at = null)
    {
        // The switch says history, Quick picks and stats alike; when it is off,
        // a play leaves no trace at all.
        if (!Settings.Current.SavesListeningHistory) return;

        var date = at ?? DateTimeOffset.Now;
        Edit(s =>
        {
            s.Recent.RemoveAll(x => x.Id == song.Id);
            s.Recent.Insert(0, song);
            if (s.Recent.Count > RecentLimit) s.Recent.RemoveRange(RecentLimit, s.Recent.Count - RecentLimit);

            var stats = s.SongStats.TryGetValue(song.Id, out var held) ? held : global::Koda.Core.SongStats.New(song, date);
            s.SongStats[song.Id] = stats with { Song = song, Plays = stats.Plays + 1, LastPlayed = date };
        });

        lock (_lock)
        {
            _history.Insert(0, PlayEvent.New(song, date));
            if (_history.Count > HistoryLimit)
            {
                _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
            }
        }

        ScheduleHistorySave();
    }

    /// <summary>Credit <paramref name="seconds"/> of listening to <paramref name="song"/> on the day of <paramref name="at"/>.</summary>
    public void AddListening(double seconds, Song song, DateTimeOffset? at = null, TimeZoneInfo? zone = null)
    {
        if (seconds <= 0) return;
        if (!Settings.Current.SavesListeningHistory) return;

        var date = at ?? DateTimeOffset.Now;
        lock (_lock)
        {
            var stats = _s.SongStats.TryGetValue(song.Id, out var held) ? held : global::Koda.Core.SongStats.New(song, date);
            _s.SongStats[song.Id] = stats with { Seconds = stats.Seconds + seconds };
            var key = DayKey(date, zone);
            _s.DailyListening[key] = _s.DailyListening.GetValueOrDefault(key) + seconds;
            _hasUnsavedListening = true;
        }
    }

    /// <summary>Write listening time gathered since the last save.</summary>
    public void Flush()
    {
        bool dirty;
        lock (_lock) dirty = _hasUnsavedListening || _history.Count > 0;
        if (dirty)
        {
            Edit(_ => { });
            WriteHistoryNow();
        }
    }

    public void ClearHistory()
    {
        Edit(s =>
        {
            s.Recent.Clear();
            s.Watched.Clear();
            s.VideoProgress.Clear();
        });
        lock (_lock)
        {
            _history.Clear();
        }

        ScheduleHistorySave();
    }

    // MARK: Videos

    /// <summary>Channels followed on this PC without an account, newest first.</summary>
    public LocalSubscription[] Subscriptions { get { lock (_lock) return [.. _s.Subscriptions]; } }

    public bool IsSubscribed(string? channelId)
    {
        if (channelId is null) return false;
        lock (_lock) return _s.Subscriptions.Exists(c => c.ChannelId == channelId);
    }

    /// <summary>Follow a channel on this PC, or stop following it.</summary>
    public void SetSubscribed(ChannelRef channel, bool subscribed, DateTimeOffset? at = null)
    {
        if (channel.Id is not { } id) return;
        Edit(s =>
        {
            s.Subscriptions.RemoveAll(c => c.ChannelId == id);
            if (subscribed) s.Subscriptions.Insert(0, new LocalSubscription(id, channel.Name, channel.Avatar, null, at ?? DateTimeOffset.Now));
        });
    }

    /// <summary>Video playlists kept on this PC, newest first.</summary>
    public VideoPlaylist[] VideoPlaylists { get { lock (_lock) return [.. _s.VideoPlaylists]; } }

    public VideoPlaylist? VideoPlaylist(Guid id) { lock (_lock) return _s.VideoPlaylists.Find(p => p.Id == id); }

    public VideoPlaylist CreateVideoPlaylist(string name, IEnumerable<VideoModel>? videos = null, DateTimeOffset? at = null)
    {
        var playlist = global::Koda.Core.Video.VideoPlaylist.Create(name, [.. (videos ?? []).DistinctBy(v => v.Id)], at ?? DateTimeOffset.Now);
        Edit(s => s.VideoPlaylists.Insert(0, playlist));
        return playlist;
    }

    public void RenameVideoPlaylist(Guid id, string name) => Edit(s =>
    {
        var at = s.VideoPlaylists.FindIndex(p => p.Id == id);
        if (at >= 0) s.VideoPlaylists[at] = s.VideoPlaylists[at] with { Name = name };
    });

    public void DeleteVideoPlaylist(Guid id) => Edit(s => s.VideoPlaylists.RemoveAll(p => p.Id == id));

    /// <summary>Add a video to the end of a playlist; false when it was there already.</summary>
    public bool AddToVideoPlaylist(VideoModel video, Guid id)
    {
        var added = false;
        Edit(s =>
        {
            var at = s.VideoPlaylists.FindIndex(p => p.Id == id);
            if (at < 0 || s.VideoPlaylists[at].Videos.Any(v => v.Id == video.Id)) return;
            s.VideoPlaylists[at] = s.VideoPlaylists[at] with { Videos = [.. s.VideoPlaylists[at].Videos, video] };
            added = true;
        });
        return added;
    }

    public void RemoveFromVideoPlaylist(string videoId, Guid id) => Edit(s =>
    {
        var at = s.VideoPlaylists.FindIndex(p => p.Id == id);
        if (at >= 0) s.VideoPlaylists[at] = s.VideoPlaylists[at] with { Videos = [.. s.VideoPlaylists[at].Videos.Where(v => v.Id != videoId)] };
    });

    /// <summary>Channels whose videos are kept out of every list.</summary>
    public ChannelRef[] BlockedChannels { get { lock (_lock) return [.. _s.BlockedChannels]; } }

    public int HiddenVideoCount { get { lock (_lock) return _s.HiddenVideos.Count; } }

    /// <summary>Whether a video is kept out of lists: hidden itself, or its channel blocked.</summary>
    public bool IsUnwanted(VideoModel video)
    {
        lock (_lock)
        {
            return _s.HiddenVideos.Contains(video.Id)
                || (video.Channel?.Id is { } channel && _s.BlockedChannels.Exists(c => c.Id == channel));
        }
    }

    /// <summary>The videos worth offering: those not hidden and not from a blocked channel.</summary>
    public VideoModel[] Offerable(IEnumerable<VideoModel> videos)
    {
        lock (_lock)
        {
            if (_s.HiddenVideos.Count == 0 && _s.BlockedChannels.Count == 0) return [.. videos];
            var hidden = _s.HiddenVideos.ToHashSet();
            var blocked = _s.BlockedChannels.Select(c => c.Id).OfType<string>().ToHashSet();
            return [.. videos.Where(v => !hidden.Contains(v.Id) && !(v.Channel?.Id is { } c && blocked.Contains(c)))];
        }
    }

    public void HideVideo(string videoId) => Edit(s =>
    {
        if (!s.HiddenVideos.Contains(videoId)) s.HiddenVideos.Insert(0, videoId);
    });

    public void Block(ChannelRef channel) => Edit(s =>
    {
        if (channel.Id is { } id && !s.BlockedChannels.Exists(c => c.Id == id)) s.BlockedChannels.Insert(0, channel);
    });

    public void Unblock(string channelId) => Edit(s => s.BlockedChannels.RemoveAll(c => c.Id == channelId));

    /// <summary>Everything hidden and every blocked channel comes back.</summary>
    public void ClearVideoBlocklist() => Edit(s =>
    {
        s.HiddenVideos.Clear();
        s.BlockedChannels.Clear();
    });

    /// <summary>Videos watched, most recent first, one entry each: Continue Watching, and what a
    /// signed-out video Home is built from.</summary>
    public VideoModel[] Watched
    {
        get
        {
            lock (_lock) return [.. _s.Watched];
        }
    }

    /// <summary>How far each watched video got, by id: the bars on its frame and where it starts again.</summary>
    public VideoProgress? ProgressOf(string videoId)
    {
        lock (_lock) return _s.VideoProgress.TryGetValue(videoId, out var progress) ? progress : null;
    }

    /// <summary>Files a video as watched, newest first, and forgets the oldest past the limit.</summary>
    public void RecordWatch(VideoModel video) => Edit(s =>
    {
        s.Watched.RemoveAll(v => v.Id == video.Id);
        s.Watched.Insert(0, video);
        if (s.Watched.Count > WatchedLimit)
        {
            foreach (var dropped in s.Watched.Skip(WatchedLimit)) s.VideoProgress.Remove(dropped.Id);
            s.Watched.RemoveRange(WatchedLimit, s.Watched.Count - WatchedLimit);
        }
    });

    public void RemoveWatched(string id) => Edit(s =>
    {
        s.Watched.RemoveAll(v => v.Id == id);
        s.VideoProgress.Remove(id);
    });

    /// <summary>Where a watched video got to. Only for one in <see cref="Watched"/>: the bar
    /// belongs to its entry there.</summary>
    public void SetVideoProgress(double position, double duration, string videoId)
    {
        if (duration <= 0 || !double.IsFinite(position) || !double.IsFinite(duration)) return;
        Edit(s =>
        {
            if (!s.Watched.Exists(v => v.Id == videoId)) return;
            var progress = new VideoProgress(Math.Clamp(position, 0, duration), duration);
            if (s.VideoProgress.TryGetValue(videoId, out var old) && old == progress) return;
            s.VideoProgress[videoId] = progress;
        });
    }

    /// <summary>Forget one play. A song with no play left in the history leaves Recently Played too.</summary>
    public void RemoveFromHistory(Guid id)
    {
        string? songId;
        bool remains;
        lock (_lock)
        {
            var index = _history.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                return;
            }

            songId = _history[index].Song.Id;
            _history.RemoveAt(index);
            remains = _history.Exists(e => e.Song.Id == songId);
        }

        if (!remains)
        {
            Edit(s => s.Recent.RemoveAll(song => song.Id == songId));
        }

        ScheduleHistorySave();
    }

    /// <summary>Forget play counts and listening time; history and likes stay.</summary>
    public void ClearStats() => Edit(s =>
    {
        s.SongStats.Clear();
        s.DailyListening.Clear();
    });

    public int PlaysOf(Song song) { lock (_lock) return _s.SongStats.TryGetValue(song.Id, out var st) ? st.Plays : 0; }

    // MARK: Collections

    public bool IsSavedAlbum(string id) { lock (_lock) return _s.Albums.Exists(a => a.Id == id); }
    public bool IsSavedPlaylist(string id) { lock (_lock) return _s.Playlists.Exists(p => p.Id == id); }
    public bool IsSavedArtist(string id) { lock (_lock) return _s.Artists.Exists(a => a.Id == id); }

    public void ToggleSave(AlbumSummary album) => Edit(s => Toggle(s.Albums, album, a => a.Id));
    public void ToggleSave(PlaylistSummary playlist) => Edit(s => Toggle(s.Playlists, playlist, p => p.Id));
    public void ToggleSave(ArtistSummary artist) => Edit(s => Toggle(s.Artists, artist, a => a.Id));

    private static void Toggle<T>(List<T> list, T item, Func<T, string> id)
    {
        var index = list.FindIndex(x => id(x) == id(item));
        if (index >= 0) list.RemoveAt(index);
        else list.Insert(0, item);
    }

    /// <summary>
    /// Bring in what a signed-in account holds, without telling <see cref="Changed"/>: it came from
    /// there. What the account has and the library lacks is added in the account's order ahead of
    /// the rest; nothing here is removed, so a like made offline and not yet sent isn't lost.
    /// </summary>
    public void MergeAccount(IReadOnlyList<Song> liked, IReadOnlyList<AlbumSummary> albums,
        IReadOnlyList<PlaylistSummary> playlists, IReadOnlyList<ArtistSummary> artists) => EditQuietly(s =>
    {
        s.Liked = Merged(liked, s.Liked, song => song.Id);
        s.Albums = Merged(albums, s.Albums, album => album.Id);
        s.Playlists = Merged(playlists, s.Playlists, playlist => playlist.Id);
        s.Artists = Merged(artists, s.Artists, artist => artist.Id);
    });

    /// <summary>Remove what the account no longer holds, after <see cref="MergeAccount"/>, when the
    /// library mirrors the account: likes and saves undone on another device go here too.</summary>
    public void DropMissing(IReadOnlySet<string>? liked, IReadOnlySet<string>? albums,
        IReadOnlySet<string>? playlists, IReadOnlySet<string>? artists) => EditQuietly(s =>
    {
        if (liked is not null) s.Liked.RemoveAll(song => !liked.Contains(song.Id));
        if (albums is not null) s.Albums.RemoveAll(album => !albums.Contains(album.Id));
        if (playlists is not null) s.Playlists.RemoveAll(playlist => !playlists.Contains(playlist.Id));
        if (artists is not null) s.Artists.RemoveAll(artist => !artists.Contains(artist.Id));
    });

    private static List<T> Merged<T>(IReadOnlyList<T> incoming, List<T> list, Func<T, string> id)
    {
        var known = list.Select(id).ToHashSet(StringComparer.Ordinal);
        var result = incoming.Where(item => !known.Contains(id(item))).ToList();
        result.AddRange(list);
        return result;
    }

    /// <summary>
    /// Put the liked songs in the account's order after a sync. A like made here that has not
    /// reached the account yet stays at the top, and anything only ever liked here follows the
    /// account's songs, so a reorder never loses a like.
    /// </summary>
    public void OrderLiked(IReadOnlyList<Song> accountOrder, IReadOnlySet<string>? pendingLikes = null) =>
        EditQuietly(s =>
        {
            var current = s.Liked.ToDictionary(song => song.Id, StringComparer.Ordinal);
            var accountIds = accountOrder.Select(song => song.Id).ToHashSet(StringComparer.Ordinal);
            var ordered = new List<Song>(s.Liked.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (pendingLikes is not null)
            {
                foreach (var song in s.Liked.Where(song => pendingLikes.Contains(song.Id) && !accountIds.Contains(song.Id)))
                {
                    if (seen.Add(song.Id)) ordered.Add(song);
                }
            }

            foreach (var song in accountOrder)
            {
                if (seen.Add(song.Id)) ordered.Add(current.GetValueOrDefault(song.Id, song));
            }

            foreach (var song in s.Liked)
            {
                if (seen.Add(song.Id)) ordered.Add(song);
            }

            s.Liked = ordered;
        });

    // MARK: Playlists made here

    public UserPlaylist? UserPlaylist(Guid id) { lock (_lock) return _s.UserPlaylists.Find(p => p.Id == id); }

    public UserPlaylist CreatePlaylist(string name, IEnumerable<Song>? songs = null, DateTimeOffset? at = null)
    {
        var trimmed = name.Trim();
        var playlist = global::Koda.Core.UserPlaylist.Create(trimmed.Length == 0 ? "New Playlist" : trimmed,
            Unique(songs ?? []), at ?? DateTimeOffset.Now);
        Edit(s => s.UserPlaylists.Insert(0, playlist));
        return playlist;
    }

    public void RenamePlaylist(Guid id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        EditPlaylist(id, p => p with { Name = trimmed });
    }

    public void DeletePlaylist(Guid id) => Edit(s => s.UserPlaylists.RemoveAll(p => p.Id == id));

    /// <summary>Append the songs the playlist does not already hold; returns how many were added.</summary>
    public int AddToPlaylist(IEnumerable<Song> songs, Guid id)
    {
        var added = 0;
        EditPlaylist(id, p =>
        {
            var known = p.Songs.Select(s => s.Id).ToHashSet();
            var fresh = Unique(songs).Where(s => !known.Contains(s.Id)).ToArray();
            added = fresh.Length;
            return p with { Songs = [.. p.Songs, .. fresh] };
        });
        return added;
    }

    public void RemoveSongs(IEnumerable<int> offsets, Guid id) => EditPlaylist(id, p =>
    {
        var songs = p.Songs.ToList();
        foreach (var index in offsets.Distinct().OrderByDescending(i => i))
        {
            if (index >= 0 && index < songs.Count) songs.RemoveAt(index);
        }
        return p with { Songs = [.. songs] };
    });

    /// <summary>The SwiftUI move: the songs at <paramref name="source"/> land before the song at <paramref name="destination"/>.</summary>
    public void MoveSongs(IEnumerable<int> source, int destination, Guid id) => EditPlaylist(id, p =>
    {
        var songs = p.Songs.ToList();
        var valid = source.Distinct().Where(i => i >= 0 && i < songs.Count).OrderBy(i => i).ToArray();
        if (valid.Length == 0) return p;
        var moving = valid.Select(i => songs[i]).ToArray();
        var insertAt = Math.Min(Math.Max(destination, 0), songs.Count) - valid.Count(i => i < destination);
        foreach (var index in valid.Reverse()) songs.RemoveAt(index);
        songs.InsertRange(insertAt, moving);
        return p with { Songs = [.. songs] };
    });

    private void EditPlaylist(Guid id, Func<UserPlaylist, UserPlaylist> change) => Edit(s =>
    {
        var index = s.UserPlaylists.FindIndex(p => p.Id == id);
        if (index < 0) return;
        s.UserPlaylists[index] = change(s.UserPlaylists[index]) with { Modified = DateTimeOffset.Now };
    });

    private static Song[] Unique(IEnumerable<Song> songs)
    {
        var seen = new HashSet<string>();
        return [.. songs.Where(s => seen.Add(s.Id))];
    }

    // MARK: Lyrics timing

    public double LyricsOffset(string songId) { lock (_lock) return _s.LyricsOffsets.GetValueOrDefault(songId); }

    /// <summary>Rounded to a tenth of a second; zero removes the entry.</summary>
    public void SetLyricsOffset(double offset, string songId) => Edit(s =>
    {
        var rounded = Math.Round(offset * 10, MidpointRounding.AwayFromZero) / 10;
        if (rounded == 0) s.LyricsOffsets.Remove(songId);
        else s.LyricsOffsets[songId] = rounded;
    });

    // MARK: Search history

    public void RecordSearch(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return;

        // Off, what is searched for is not remembered.
        if (!Settings.Current.SavesSearchHistory) return;

        Edit(s =>
        {
            s.Searches.RemoveAll(q => string.Equals(q, trimmed, StringComparison.OrdinalIgnoreCase));
            s.Searches.Insert(0, trimmed);
            if (s.Searches.Count > SearchLimit) s.Searches.RemoveRange(SearchLimit, s.Searches.Count - SearchLimit);
        });
    }

    public void RemoveSearch(string query) => Edit(s => s.Searches.RemoveAll(q => q == query));

    public void ClearSearches() => Edit(s => s.Searches.Clear());

    // MARK: Days

    public static string DayKey(DateTimeOffset date, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(date, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Seconds listened on the day of <paramref name="date"/>.</summary>
    public double ListeningOn(DateTimeOffset date, TimeZoneInfo? zone = null)
    {
        lock (_lock) return _s.DailyListening.GetValueOrDefault(DayKey(date, zone));
    }

    /// <summary>Days in a row with any listening, ending today, or yesterday while today has none yet.</summary>
    public int Streak(DateTimeOffset? asOf = null, TimeZoneInfo? zone = null)
    {
        var day = asOf ?? DateTimeOffset.Now;
        if (ListeningOn(day, zone) == 0) day = day.AddDays(-1);
        var count = 0;
        while (ListeningOn(day, zone) > 0)
        {
            count++;
            day = day.AddDays(-1);
        }
        return count;
    }

    // MARK: Not interested

    /// <summary>The songs and artists the listener asked Koda to suggest less, newest first.</summary>
    public NotInterested NotInterested { get { lock (_lock) return _s.NotInterested.Copy(); } }

    /// <summary>Whether a song is kept out of what Koda suggests: itself, or an artist on it.</summary>
    public bool IsUnwanted(Song song) { lock (_lock) return _s.NotInterested.Hides(song); }

    public bool IsUnwanted(ArtistRef artist) { lock (_lock) return _s.NotInterested.Hides(artist); }

    /// <summary>Songs that may be offered: without the ones the listener asked not to hear.</summary>
    public Song[] Offerable(IEnumerable<Song> songs)
    {
        lock (_lock)
        {
            var hidden = _s.NotInterested;
            return hidden.IsEmpty ? [.. songs] : [.. songs.Where(s => !hidden.Hides(s))];
        }
    }

    public void Hide(Song song) => Edit(s =>
    {
        if (!s.NotInterested.Songs.Exists(x => x.Id == song.Id)) s.NotInterested.Songs.Insert(0, song);
    });

    public void Hide(ArtistRef artist) => Edit(s =>
    {
        if (!s.NotInterested.Hides(artist)) s.NotInterested.Artists.Insert(0, artist);
    });

    public void Unhide(Song song) => Edit(s => s.NotInterested.Songs.RemoveAll(x => x.Id == song.Id));

    public void Unhide(ArtistRef artist) => Edit(s => s.NotInterested.Artists.RemoveAll(x => NotInterested.Same(x, artist)));

    // MARK: Storage

    /// <summary>The file's shape, field for field the Swift Snapshot, so a backup reads on either platform.</summary>
    private sealed class Snapshot
    {
        public List<Song> Liked { get; set; } = [];
        public List<AlbumSummary> Albums { get; set; } = [];
        public List<PlaylistSummary> Playlists { get; set; } = [];
        public List<ArtistSummary> Artists { get; set; } = [];
        public List<Song> Recent { get; set; } = [];
        public List<string> Searches { get; set; } = [];
        public List<UserPlaylist> UserPlaylists { get; set; } = [];
        public Dictionary<string, SongStats> SongStats { get; set; } = [];
        public Dictionary<string, double> DailyListening { get; set; } = [];
        public Dictionary<string, double> LyricsOffsets { get; set; } = [];
        /// <summary>Videos watched, most recent first; and how far each got, by id.</summary>
        public List<VideoModel> Watched { get; set; } = [];
        public Dictionary<string, VideoProgress> VideoProgress { get; set; } = [];
        /// <summary>The newest play taken from a Koda for Android backup, so importing the same
        /// backup, or a later one that repeats its history, never counts a play twice.</summary>
        public DateTimeOffset? AndroidPlaysImportedThrough { get; set; }
        public NotInterested NotInterested { get; set; } = new();
        /// <summary>Video mode's own: channels followed without an account, playlists of videos,
        /// and what is kept out of its lists. Windows only; the Mac's file has none of them.</summary>
        public List<LocalSubscription> Subscriptions { get; set; } = [];
        public List<VideoPlaylist> VideoPlaylists { get; set; } = [];
        public List<ChannelRef> BlockedChannels { get; set; } = [];
        public List<string> HiddenVideos { get; set; } = [];
    }

    private sealed class Backup
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public DateTimeOffset Exported { get; set; }
        public Snapshot? Library { get; set; }
        /// <summary>Version 2 adds the dated plays; version 1 files read from Recent and the stats.</summary>
        public List<PlayEvent>? History { get; set; }
    }

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new Iso8601Seconds() },
    };

    private void Load()
    {
        if (_file is null) return;
        try
        {
            if (!File.Exists(_file)) return;
            if (JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_file), Options) is { } snapshot)
                _s = Normalized(snapshot);
        }
        catch (Exception)
        {
            // A damaged file starts an empty library rather than breaking the app.
        }
    }

    /// <summary>The dated history lives beside the library, as it does on the Mac.</summary>
    private void LoadHistory()
    {
        if (_historyFile is null) return;
        try
        {
            if (!File.Exists(_historyFile)) return;
            if (JsonSerializer.Deserialize<List<PlayEvent?>>(File.ReadAllText(_historyFile), Options) is { } events)
            {
                _history.AddRange(events.OfType<PlayEvent>().OrderByDescending(e => e.Date));
                if (_history.Count > HistoryLimit)
                {
                    _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
                }
            }
        }
        catch (Exception)
        {
            // A damaged history costs the page, never the library.
        }
    }

    /// <summary>Write the history a moment later, once, for the newest state.</summary>
    private void ScheduleHistorySave()
    {
        if (_historyFile is null) return;
        int version;
        lock (_lock) version = ++_historySaveVersion;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                string? text;
                lock (_lock)
                {
                    if (version != _historySaveVersion) return;
                    text = JsonSerializer.Serialize(_history, Options);
                }

                WriteHistory(text);
            }
            catch (Exception)
            {
                // The previous file stays; the next play tries again.
            }
        });
    }

    private void WriteHistoryNow()
    {
        if (_historyFile is null) return;
        string text;
        lock (_lock)
        {
            _historySaveVersion++;
            text = JsonSerializer.Serialize(_history, Options);
        }

        WriteHistory(text);
    }

    private void WriteHistory(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyFile!)!);
            var temp = _historyFile + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, _historyFile!, overwrite: true);
        }
        catch (Exception)
        {
            // The previous file stays.
        }
    }

    /// <summary>A file from an older version lacks the newer keys; each missing one starts empty.</summary>
    private static Snapshot Normalized(Snapshot s)
    {
        s.Liked ??= [];
        s.Albums ??= [];
        s.Playlists ??= [];
        s.Artists ??= [];
        s.Recent ??= [];
        s.Searches ??= [];
        s.UserPlaylists ??= [];
        s.SongStats ??= [];
        s.DailyListening ??= [];
        s.LyricsOffsets ??= [];
        s.Watched ??= [];
        s.VideoProgress ??= [];
        s.NotInterested ??= new();
        s.NotInterested.Songs ??= [];
        s.NotInterested.Artists ??= [];
        s.Subscriptions ??= [];
        s.VideoPlaylists ??= [];
        s.BlockedChannels ??= [];
        s.HiddenVideos ??= [];
        s.Subscriptions.RemoveAll(c => c is null || string.IsNullOrEmpty(c.ChannelId));
        s.VideoPlaylists.RemoveAll(p => p is null);
        for (var i = 0; i < s.VideoPlaylists.Count; i++)
        {
            if (s.VideoPlaylists[i].Videos is null) s.VideoPlaylists[i] = s.VideoPlaylists[i] with { Videos = [] };
        }
        s.BlockedChannels.RemoveAll(c => c?.Id is null);
        return s;
    }

    private void Edit(Action<Snapshot> change)
    {
        lock (_lock)
        {
            change(_s);
            SaveLocked();
        }
        // Each listener on its own: one that throws (a page torn down mid-call) must not keep
        // the change from the others, such as the player bar's like button.
        foreach (var listener in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)listener)();
            }
            catch (Exception)
            {
                // A listener that throws must not undo the change or silence the rest.
            }
        }
    }

    /// <summary>A change that came from the account, not the listener: saved, but not announced.</summary>
    private void EditQuietly(Action<Snapshot> change)
    {
        lock (_lock)
        {
            change(_s);
            SaveLocked();
        }
    }

    /// <summary>Write the library under the lock; called by every change.</summary>
    private void SaveLocked()
    {
        _hasUnsavedListening = false;
        if (_file is null) return;
        string? text = null;
        try
        {
            text = JsonSerializer.Serialize(_s, Options);
        }
        catch (Exception)
        {
            text = null;
        }
        if (text is not null) WriteAtomically(text);
    }

    private void WriteAtomically(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file!)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, _file!, overwrite: true);
        }
        catch (Exception)
        {
            // The previous file stays; the next change tries again.
        }
    }

    // MARK: Backup

    /// <summary>Everything in the library as one JSON file, for keeping or moving to another machine.</summary>
    public byte[] BackupData(DateTimeOffset? at = null)
    {
        lock (_lock)
        {
            var options = new JsonSerializerOptions(Options) { WriteIndented = true };
            return JsonSerializer.SerializeToUtf8Bytes(
                new Backup
                {
                    Format = BackupFormat,
                    Version = 2,
                    Exported = at ?? DateTimeOffset.Now,
                    Library = _s,
                    History = [.. _history],
                }, options);
        }
    }

    /// <summary>Replace the library with a backup. Nothing changes unless the whole file reads as one.</summary>
    public void Restore(byte[] data)
    {
        Backup? backup;
        try
        {
            backup = JsonSerializer.Deserialize<Backup>(data, Options);
        }
        catch (Exception)
        {
            throw new LibraryBackupException();
        }
        if (backup is not { Format: BackupFormat, Library: { } library }) throw new LibraryBackupException();
        var restored = Normalized(library);
        Edit(s =>
        {
            s.Liked = restored.Liked;
            s.Albums = restored.Albums;
            s.Playlists = restored.Playlists;
            s.Artists = restored.Artists;
            s.Recent = restored.Recent;
            s.Searches = restored.Searches;
            s.UserPlaylists = restored.UserPlaylists;
            s.SongStats = restored.SongStats;
            s.DailyListening = restored.DailyListening;
            s.LyricsOffsets = restored.LyricsOffsets;
            s.AndroidPlaysImportedThrough = restored.AndroidPlaysImportedThrough;
            s.NotInterested = restored.NotInterested;
            s.Subscriptions = restored.Subscriptions;
            s.VideoPlaylists = restored.VideoPlaylists;
            s.BlockedChannels = restored.BlockedChannels;
            s.HiddenVideos = restored.HiddenVideos;
        });

        // A version 1 backup has no dated history: the plays it knows of become one each,
        // at their last play, exactly as the Mac restores them.
        var history = backup.History is { Count: > 0 } dated
            ? dated.Where(e => e.Song is not null).OrderByDescending(e => e.Date).Take(HistoryLimit).ToList()
            : restored.Recent
                .Where(song => restored.SongStats.ContainsKey(song.Id))
                .Select(song => PlayEvent.New(song, restored.SongStats[song.Id].LastPlayed))
                .OrderByDescending(e => e.Date)
                .ToList();
        lock (_lock)
        {
            _history.Clear();
            _history.AddRange(history);
        }

        ScheduleHistorySave();
    }

    // MARK: Koda for Android

    /// <summary>
    /// Add a Koda for Android backup to the library. Nothing here is replaced: likes, saved
    /// albums and playlists join what's already saved, plays add to the stats and history,
    /// and a playlist already here under the same name with the same songs isn't added again.
    /// </summary>
    public AndroidImportSummary ImportAndroid(AndroidBackup backup, TimeZoneInfo? zone = null)
    {
        var summary = new AndroidImportSummary();
        var historyAdds = new List<PlayEvent>();
        Edit(s =>
        {
            var likedIds = s.Liked.Select(x => x.Id).ToHashSet();
            var newLikes = backup.Liked.Where(x => !likedIds.Contains(x.Id)).ToArray();
            s.Liked.AddRange(newLikes);
            var saved = 0;
            foreach (var album in backup.Albums.Where(a => !s.Albums.Exists(x => x.Id == a.Id)))
            {
                s.Albums.Insert(0, album);
                saved++;
            }
            foreach (var playlist in backup.SavedPlaylists.Where(p => !s.Playlists.Exists(x => x.Id == p.Id)))
            {
                s.Playlists.Insert(0, playlist);
                saved++;
            }

            var playlists = 0;
            foreach (var playlist in backup.Playlists)
            {
                var ids = playlist.Songs.Select(x => x.Id).ToArray();
                if (s.UserPlaylists.Exists(p => p.Name == playlist.Name && p.Songs.Select(x => x.Id).SequenceEqual(ids))) continue;
                s.UserPlaylists.Add(global::Koda.Core.UserPlaylist.Create(playlist.Name, playlist.Songs, playlist.Created));
                playlists++;
            }
            s.UserPlaylists = [.. s.UserPlaylists.OrderByDescending(p => p.Created)];

            var through = s.AndroidPlaysImportedThrough;
            var fresh = backup.Plays.Where(p => through is null || p.Date > through).ToArray();
            foreach (var play in fresh)
            {
                var stats = s.SongStats.TryGetValue(play.Song.Id, out var held) ? held : global::Koda.Core.SongStats.New(play.Song, play.Date);
                s.SongStats[play.Song.Id] = stats with
                {
                    Plays = stats.Plays + 1,
                    Seconds = stats.Seconds + play.Seconds,
                    FirstPlayed = stats.FirstPlayed < play.Date ? stats.FirstPlayed : play.Date,
                    LastPlayed = stats.LastPlayed > play.Date ? stats.LastPlayed : play.Date,
                };
                var key = DayKey(play.Date, zone);
                s.DailyListening[key] = s.DailyListening.GetValueOrDefault(key) + play.Seconds;
                historyAdds.Add(PlayEvent.New(play.Song, play.Date));
            }
            if (fresh.Length > 0) s.AndroidPlaysImportedThrough = fresh[^1].Date;

            // Recently played, newest first across both: each song where it was last played.
            var lastPlayed = new Dictionary<string, (Song Song, DateTimeOffset At)>();
            foreach (var song in s.Recent)
                lastPlayed[song.Id] = (song, s.SongStats.TryGetValue(song.Id, out var st) ? st.LastPlayed : DateTimeOffset.MinValue);
            foreach (var play in fresh)
            {
                var known = lastPlayed.TryGetValue(play.Song.Id, out var entry);
                if (!known || entry.At < play.Date) lastPlayed[play.Song.Id] = (known ? entry.Song : play.Song, play.Date);
            }
            s.Recent = [.. lastPlayed.Values.OrderByDescending(v => v.At).Take(RecentLimit).Select(v => v.Song)];

            var knownSearches = s.Searches.Select(q => q.ToLowerInvariant()).ToHashSet();
            var before = s.Searches.Count;
            s.Searches = [.. s.Searches.Concat(backup.Searches.Where(q => !knownSearches.Contains(q.ToLowerInvariant()))).Take(SearchLimit)];

            var (subscriptions, watched, videoPlaylists, blocked) = ImportAndroidVideo(s, backup);
            summary = new AndroidImportSummary(newLikes.Length, fresh.Length, playlists, saved, s.Searches.Count - before,
                subscriptions, watched, videoPlaylists, blocked);
        });

        if (historyAdds.Count > 0)
        {
            lock (_lock)
            {
                _history.AddRange(historyAdds);
                _history.Sort((a, b) => b.Date.CompareTo(a.Date));
                if (_history.Count > HistoryLimit)
                {
                    _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
                }
            }

            ScheduleHistorySave();
        }

        return summary;
    }

    /// <summary>Video mode's side of an Android import, merged the same way: nothing here is
    /// replaced, and the same backup twice adds nothing twice.</summary>
    private static (int Subscriptions, int Watched, int Playlists, int Blocked) ImportAndroidVideo(Snapshot s, AndroidBackup backup)
    {
        var subscriptions = 0;
        foreach (var channel in backup.Subscriptions)
        {
            if (s.Subscriptions.Exists(c => c.ChannelId == channel.ChannelId)) continue;
            s.Subscriptions.Add(channel);
            subscriptions++;
        }
        s.Subscriptions = [.. s.Subscriptions.OrderByDescending(c => c.Subscribed ?? DateTimeOffset.MinValue)];

        // The phone's history has no dates Windows keeps, so what is here stays on top and the
        // phone's follows in its own order; where a video got to comes across unless it is
        // already known here.
        var watched = 0;
        foreach (var video in backup.Watched)
        {
            if (s.Watched.Exists(v => v.Id == video.Id)) continue;
            if (s.Watched.Count >= WatchedLimit) break;
            s.Watched.Add(video);
            if (backup.VideoProgress.TryGetValue(video.Id, out var progress)) s.VideoProgress.TryAdd(video.Id, progress);
            watched++;
        }

        var playlists = 0;
        foreach (var playlist in backup.VideoPlaylists)
        {
            var ids = playlist.Videos.Select(v => v.Id).ToArray();
            if (s.VideoPlaylists.Exists(p => p.Name == playlist.Name && p.Videos.Select(v => v.Id).SequenceEqual(ids))) continue;
            s.VideoPlaylists.Add(global::Koda.Core.Video.VideoPlaylist.Create(playlist.Name, playlist.Videos, playlist.Created));
            playlists++;
        }
        s.VideoPlaylists = [.. s.VideoPlaylists.OrderByDescending(p => p.Created)];

        var blocked = 0;
        foreach (var channel in backup.BlockedChannels)
        {
            if (s.BlockedChannels.Exists(c => c.Id == channel.Id)) continue;
            s.BlockedChannels.Add(channel);
            blocked++;
        }
        foreach (var id in backup.HiddenVideos)
        {
            if (s.HiddenVideos.Contains(id)) continue;
            s.HiddenVideos.Add(id);
            blocked++;
        }

        return (subscriptions, watched, playlists, blocked);
    }

    /// <summary>Dates as Swift's .iso8601 reads and writes them: UTC, whole seconds.</summary>
    private sealed class Iso8601Seconds : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                // Swift's default Date encoding: seconds since 2001-01-01.
                return new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(reader.GetDouble());
            }
            return DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}
