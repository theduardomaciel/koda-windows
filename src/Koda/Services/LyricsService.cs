using System.IO;
using Koda.Core;

namespace Koda.Services;

/// <summary>
/// The lyrics of a song, from one place: the panel asks for them, and the player asks
/// for them as soon as a song starts, so the words are usually already there by the time
/// anyone opens the panel. The client keeps what it found this session, and the store
/// keeps it between launches, so a song heard days ago opens without a request.
/// </summary>
public static class LyricsService
{
    private static readonly LyricsClient Client = new(
        store: new LyricsStore(Path.Combine(DataPaths.Root, "lyrics")));

    /// <summary>The lyrics of a song, or null when no service has them. The services the
    /// listener turned off are not asked.</summary>
    public static Task<Lyrics?> For(Song song) => Client.LyricsFor(song, Settings.Current.LyricsSources);

    /// <summary>Ask for the lyrics before anyone wants them.</summary>
    public static void Prefetch(Song song)
    {
        _ = For(song);
    }

    /// <summary>Forget the lyrics found this session, so the next look asks the services again.</summary>
    public static void Forget() => LyricsClient.ForgetSession();

    /// <summary>How much room the lyrics cache takes on disk.</summary>
    public static long CacheBytes => Client.CacheBytes;

    /// <summary>How many songs have their lyrics kept on disk.</summary>
    public static int CachedSongs => Client.CachedSongs;

    /// <summary>Empty the session's lyrics and the ones kept on disk.</summary>
    public static void ClearCache() => Client.ClearCache();
}
