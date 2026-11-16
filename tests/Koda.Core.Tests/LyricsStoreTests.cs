using Koda.Core;

namespace Koda.Core.Tests;

/// <summary>
/// The lyrics kept on disk: a song's words survive a relaunch, and an answer found with
/// services the listener no longer asks for is never handed back.
/// </summary>
public class LyricsStoreTests
{
    private static Lyrics SampleLyrics() => Lyrics.Synced(
    [
        new LyricLine(
            0,
            12.5,
            "Hello world",
            [
                new LyricWord("Hello", 12.5, 13.0),
                new LyricWord("world", 13.0, 13.6),
            ],
            [new LyricWord("(hello)", 12.5, 13.4)],
            13.6,
            LyricVoice.Lead),
        new LyricLine(
            1,
            20.0,
            "Again",
            [new LyricWord("Again", 20.0, 21.0)],
            [],
            21.0,
            LyricVoice.Duet),
    ]);

    private static LyricsStore Store() =>
        new(Path.Combine(Fixtures.Scratch, $"lyrics-{Guid.NewGuid():N}"));

    [Fact]
    public async Task LyricsSurviveTheStore()
    {
        var store = Store();
        var sources = new[] { LyricsSource.YouLyPlus, LyricsSource.Lrclib };

        await store.WriteAsync("song0000001", sources, SampleLyrics());
        var back = await store.ReadAsync("song0000001", sources);

        Assert.NotNull(back);
        Assert.True(back!.IsSynced);
        Assert.Equal(2, back.Lines.Count);
        Assert.Equal("Hello world", back.Lines[0].Text);
        Assert.Equal(12.5, back.Lines[0].Time, 3);
        Assert.Equal(13.6, back.Lines[0].End ?? -1, 3);
        Assert.Equal(LyricVoice.Duet, back.Lines[1].Voice);
        Assert.Equal(["Hello", "world"], back.Lines[0].Words.Select(word => word.Text));
        Assert.Equal(13.0, back.Lines[0].Words[0].End, 3);
        Assert.Equal("(hello)", back.Lines[0].Background[0].Text);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task PlainLyricsSurviveTheStore()
    {
        var store = Store();
        var sources = new[] { LyricsSource.Lrclib };

        await store.WriteAsync("song0000002", sources, Lyrics.Unsynced("one\ntwo"));
        var back = await store.ReadAsync("song0000002", sources);

        Assert.NotNull(back);
        Assert.Equal("one\ntwo", back!.Plain);
        Assert.False(back.IsSynced);
    }

    [Fact]
    public async Task CreditsSurviveTheStore()
    {
        var store = Store();
        var sources = new[] { LyricsSource.SpicyLyrics };
        var lyrics = SampleLyrics().WithCredits([
            new LyricsCredit("Lyrics from", "Spicy Lyrics", "https://spicylyrics.org/"),
            new LyricsCredit("Submitted by", "spikerko", "https://spicylyrics.org/uid/1"),
        ]);

        await store.WriteAsync("song0000007", sources, lyrics);
        var back = await store.ReadAsync("song0000007", sources);

        Assert.NotNull(back);
        Assert.Equal(["Spicy Lyrics", "spikerko"], back!.Credits.Select(credit => credit.Name));
        Assert.All(back.Credits, credit => Assert.NotNull(credit.Url));
    }

    [Fact]
    public async Task ADifferentSetOfServicesIsAMiss()
    {
        var store = Store();
        await store.WriteAsync("song0000003", [LyricsSource.YouLyPlus], SampleLyrics());

        Assert.Null(await store.ReadAsync("song0000003", [LyricsSource.Lrclib]));
        // The stale answer goes with the miss, so the disk does not keep it forever.
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task ClearEmptiesTheCacheAndItsSize()
    {
        var store = Store();
        await store.WriteAsync("song0000004", [LyricsSource.Lrclib], SampleLyrics());
        await store.WriteAsync("song0000005", [LyricsSource.Lrclib], SampleLyrics());

        Assert.Equal(2, store.Count);
        Assert.True(store.BytesUsed > 0);

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.BytesUsed);
        Assert.Null(await store.ReadAsync("song0000004", [LyricsSource.Lrclib]));
    }

    [Fact]
    public async Task ADamagedFileIsAMiss()
    {
        var directory = Path.Combine(Fixtures.Scratch, $"lyrics-{Guid.NewGuid():N}");
        var store = new LyricsStore(directory);
        var sources = new[] { LyricsSource.Lrclib };
        await store.WriteAsync("song0000006", sources, SampleLyrics());

        foreach (var kept in Directory.GetFiles(directory))
        {
            File.WriteAllText(kept, "{ not json");
        }

        Assert.Null(await store.ReadAsync("song0000006", sources));
    }
}
