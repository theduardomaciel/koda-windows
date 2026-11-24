using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Koda.Core;

/// <summary>
/// Lyrics kept on disk, one small JSON file per song, so the words open without asking
/// the services again - across launches too, not just this session. The store is a
/// cache: it has a ceiling, and the oldest songs go first when it is reached.
///
/// A file remembers the services that produced it. Asking with a different set of
/// services ignores (and removes) the old answer, so turning a service off is honoured.
/// Every method may be called from any thread. A cache write never breaks the lyrics:
/// a failed write is simply a miss next time.
/// </summary>
public sealed class LyricsStore
{
    /// <summary>The file format, so an upgrade can drop files it cannot read. Version 2
    /// picks the finest-timed answer, so version 1 files may hold a coarser one.</summary>
    private const int Version = 2;

    /// <summary>How much the lyrics cache may keep.</summary>
    private const long MaxBytes = 32 * 1024 * 1024;

    /// <summary>A second ceiling, so thousands of tiny files cannot slow the folder down.</summary>
    private const int MaxFiles = 500;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _writing = new(1, 1);

    public LyricsStore(string directory)
    {
        _directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // Reads and writes fail individually below.
        }
    }

    /// <summary>How much room the cached lyrics take, as the Settings page reports it.</summary>
    public long BytesUsed
    {
        get
        {
            try
            {
                return Files().Sum(file => file.Length);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>How many songs have lyrics kept.</summary>
    public int Count
    {
        get
        {
            try
            {
                return Files().Count();
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>The lyrics kept for a song, when they were found with exactly these services.</summary>
    public async Task<Lyrics?> ReadAsync(
        string songId,
        IReadOnlyList<LyricsSource> sources,
        CancellationToken cancellation = default)
    {
        var file = PathFor(songId);
        string text;
        try
        {
            text = await File.ReadAllTextAsync(file, cancellation).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        SavedLyrics? saved;
        try
        {
            saved = JsonSerializer.Deserialize<SavedLyrics>(text, Options);
        }
        catch (Exception)
        {
            saved = null;
        }

        if (saved is not { Version: Version }
            || saved.Sources is null
            || !saved.Sources.SequenceEqual(sources.Select(source => source.ToString())))
        {
            // A damaged file, or one found with services the listener no longer asks for.
            Delete(file);
            return null;
        }

        if (saved.Plain is { Length: > 0 } plain)
        {
            return Lyrics.Unsynced(plain).WithCredits(Credits(saved.Credits));
        }

        if (saved.Lines is not { Length: > 0 } stored)
        {
            Delete(file);
            return null;
        }

        var lines = new List<LyricLine>(stored.Length);
        foreach (var line in stored)
        {
            lines.Add(new LyricLine(
                line.Id,
                line.Time,
                line.Text ?? string.Empty,
                Words(line.Words),
                Words(line.Background),
                line.End,
                line.Voice));
        }

        return Lyrics.Synced(lines).WithCredits(Credits(saved.Credits));
    }

    /// <summary>Keeps a song's lyrics, replacing an older answer for the same song.</summary>
    public async Task WriteAsync(
        string songId,
        IReadOnlyList<LyricsSource> sources,
        Lyrics lyrics,
        CancellationToken cancellation = default)
    {
        var saved = new SavedLyrics(
            Version,
            sources.Select(source => source.ToString()).ToArray(),
            lyrics.Plain,
            lyrics.Lines
                .Select(line => new SavedLine(
                    line.Id,
                    line.Time,
                    line.Text,
                    line.End,
                    line.Voice,
                    line.Words
                        .Select(word => new SavedWord(word.Text, word.Start, word.End, word.SpaceAfter))
                        .ToArray(),
                    line.Background
                        .Select(word => new SavedWord(word.Text, word.Start, word.End, word.SpaceAfter))
                        .ToArray()))
                .ToArray(),
            lyrics.Credits
                .Select(credit => new SavedCredit(credit.Role, credit.Name, credit.Url))
                .ToArray());

        var file = PathFor(songId);
        var temporary = file + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(saved, Options);

            await _writing.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                // Write beside the real file and move it over: a reader never sees half a file.
                await File.WriteAllTextAsync(temporary, json, cancellation).ConfigureAwait(false);
                File.Move(temporary, file, overwrite: true);
                Trim();
            }
            finally
            {
                _writing.Release();
            }
        }
        catch (Exception)
        {
            // The cache is a convenience: the lyrics are still in memory for this session.
            Delete(temporary);
        }
    }

    /// <summary>Empties the cache, as the Settings page's Clear button asks.</summary>
    public void Clear()
    {
        try
        {
            // Wait for a write already under way, so Clear does not leave the file it
            // was writing behind (a prefetch often lands while Settings is open).
            _writing.Wait();
            try
            {
                foreach (var file in Files())
                {
                    TryDelete(file.FullName);
                }
            }
            finally
            {
                _writing.Release();
            }
        }
        catch (Exception)
        {
            // Nothing kept is better than a crash on a locked folder.
        }
    }

    private static IReadOnlyList<LyricWord> Words(SavedWord[]? words) =>
        words is { Length: > 0 }
            ? words.Select(word => new LyricWord(
                word.Text ?? string.Empty,
                word.Start,
                word.End,
                word.SpaceAfter)).ToList()
            : [];

    private static IReadOnlyList<LyricsCredit> Credits(SavedCredit[]? credits) =>
        credits is { Length: > 0 }
            ? credits
                .Where(credit => !string.IsNullOrWhiteSpace(credit.Name))
                .Select(credit => new LyricsCredit(
                    credit.Role ?? string.Empty,
                    credit.Name ?? string.Empty,
                    credit.Url))
                .ToList()
            : [];

    /// <summary>The oldest files go first, so a long listening session cannot fill the disk.</summary>
    private void Trim()
    {
        var files = Files().OrderBy(file => file.LastWriteTimeUtc).ToList();
        var total = files.Sum(file => file.Length);
        var kept = files.Count;
        for (var index = 0; index < files.Count && (total > MaxBytes || kept > MaxFiles); index++)
        {
            total -= files[index].Length;
            kept--;
            TryDelete(files[index].FullName);
        }
    }

    private IEnumerable<FileInfo> Files()
    {
        var directory = new DirectoryInfo(_directory);
        return directory.Exists ? directory.EnumerateFiles("*.json") : [];
    }

    private string PathFor(string songId)
    {
        // Song ids are YouTube ids in practice, but a local or downloaded song may carry
        // anything: a hash keeps every id a legal, collision-free file name.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(songId)));
        return Path.Combine(_directory, hash[..32] + ".json");
    }

    private void Delete(string file) => TryDelete(file);

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // A file that cannot be removed is a cache miss at worst.
        }
    }

    private sealed record SavedLyrics(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("sources")] string[]? Sources,
        [property: JsonPropertyName("plain")] string? Plain,
        [property: JsonPropertyName("lines")] SavedLine[]? Lines,
        [property: JsonPropertyName("credits")] SavedCredit[]? Credits);

    private sealed record SavedCredit(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("url")] string? Url);

    private sealed record SavedLine(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("time")] double Time,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("end")] double? End,
        [property: JsonPropertyName("voice")] LyricVoice Voice,
        [property: JsonPropertyName("words")] SavedWord[]? Words,
        [property: JsonPropertyName("background")] SavedWord[]? Background);

    private sealed record SavedWord(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("spaceAfter")] bool SpaceAfter);
}
