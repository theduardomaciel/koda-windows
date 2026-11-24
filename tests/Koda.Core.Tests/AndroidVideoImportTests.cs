using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Koda.Core;
using Koda.Core.Video;

namespace Koda.Core.Tests;

/// <summary>
/// Video mode's side of the Android import: local subscriptions, the watch history and where
/// each video got to, video playlists, and the hidden videos and blocked channels. The zip is
/// built here in the shape BackupTransfer.kt writes: each store a JSON array kept as a string.
/// </summary>
public class AndroidVideoImportTests
{
    private const string Channel = "UCuAXFkgsw1L7xaCfnd5JJOw";
    private const string Other = "UCBR8-60-B28hp2BmDPdntcQ";

    private static byte[] Backup()
    {
        var subscriptions = new JsonArray(
            new JsonObject { ["channelId"] = Channel, ["name"] = "Rick Astley", ["avatarUrl"] = "https://yt3.ggpht.com/a.jpg", ["handle"] = "@rick", ["subscribedAt"] = 1_700_000_000_000 },
            new JsonObject { ["channelId"] = "@notanid", ["name"] = "Unresolved" },
            new JsonObject { ["channelId"] = Other, ["name"] = "YouTube", ["avatarUrl"] = null, ["handle"] = "null", ["subscribedAt"] = 1_710_000_000_000 });
        var history = new JsonArray(
            new JsonObject { ["videoId"] = "dQw4w9WgXcQ", ["title"] = "Never Gonna Give You Up", ["channelName"] = "Rick Astley", ["channelId"] = Channel, ["thumbnailUrl"] = "https://i.ytimg.com/vi/dQw4w9WgXcQ/hq720.jpg", ["duration"] = 213, ["viewCount"] = "1.6B views", ["uploadedDate"] = "15 years ago", ["isLive"] = false, ["watchedProgress"] = 0.5 },
            new JsonObject { ["videoId"] = "9bZkp7q19f0", ["title"] = "Gangnam Style", ["channelName"] = "officialpsy", ["channelId"] = null, ["duration"] = 252, ["viewCount"] = "5B views" },
            new JsonObject { ["videoId"] = "jNQXAC9IVRw", ["title"] = "Me at the zoo", ["channelName"] = "jawed", ["duration"] = 19 },
            new JsonObject { ["videoId"] = "short", ["title"] = "Bad id" });
        var resume = new JsonArray(
            new JsonObject { ["videoId"] = "9bZkp7q19f0", ["positionMs"] = 60_000, ["durationMs"] = 252_000, ["updatedAt"] = 1 });
        var blocked = new JsonArray(new JsonObject { ["channelId"] = Other, ["name"] = "YouTube", ["avatarUrl"] = "", ["blockedAt"] = 1 });
        var hidden = new JsonArray(new JsonObject { ["videoId"] = "kJQP7kiw5Fk", ["title"] = "Despacito", ["hiddenAt"] = 1 });

        var profileData = new JsonObject
        {
            ["other"] = new JsonObject { ["subscriptions"] = "[]" },
            ["p1"] = new JsonObject
            {
                ["subscriptions"] = subscriptions.ToJsonString(),
                ["watchHistory"] = history.ToJsonString(),
                ["removedFromHistory"] = new JsonArray("jNQXAC9IVRw"),
                ["resumePositions"] = resume.ToJsonString(),
                ["blockedChannels"] = blocked.ToJsonString(),
                ["hiddenVideos"] = hidden.ToJsonString(),
                ["likedSongs"] = null,
            },
        };
        var playlist = new JsonObject
        {
            ["id"] = "localvp_WL",
            ["name"] = "Watch Later",
            ["createdAt"] = 1_700_000_000_000,
            ["videos"] = new JsonArray(
                new JsonObject { ["videoId"] = "dQw4w9WgXcQ", ["title"] = "Never Gonna Give You Up", ["channelName"] = "Rick Astley", ["channelId"] = Channel, ["channelIcon"] = "https://yt3.ggpht.com/a.jpg", ["thumbnail"] = "https://i.ytimg.com/vi/dQw4w9WgXcQ/hq720.jpg", ["duration"] = 213, ["viewCount"] = "1.6B views", ["uploaded"] = "15 years ago" },
                new JsonObject { ["videoId"] = "dQw4w9WgXcQ", ["title"] = "Duplicate" }),
        };

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

            Add("manifest.json", """{ "format": "koda-backup", "formatVersion": 2, "createdAt": 1700000000000 }""");
            Add("profiles.json", """{ "activeProfileId": "p1", "profiles": [] }""");
            Add("profile-data.json", profileData.ToJsonString());
            Add("files/video_playlists/localvp_WL.json", playlist.ToJsonString());
        }

        return output.ToArray();
    }

    [Fact]
    public void LocalSubscriptionsComeFromTheProfileInUse()
    {
        var backup = AndroidBackup.Read(Backup());

        // A handle that never resolved to a UC id can't be fetched, so it is left out.
        Assert.Equal([Channel, Other], backup.Subscriptions.Select(s => s.ChannelId));
        Assert.Equal("@rick", backup.Subscriptions[0].Handle);
        Assert.NotNull(backup.Subscriptions[0].Avatar);
        Assert.Null(backup.Subscriptions[1].Handle);
        Assert.Null(backup.Subscriptions[1].Avatar);
    }

    [Fact]
    public void WatchHistoryKeepsWhereEachVideoGotTo()
    {
        var backup = AndroidBackup.Read(Backup());

        // One was removed from history on the phone, one has no usable id.
        Assert.Equal(["dQw4w9WgXcQ", "9bZkp7q19f0"], backup.Watched.Select(v => v.Id));
        Assert.Equal(213, backup.Watched[0].Duration);
        Assert.Equal(Channel, backup.Watched[0].Channel?.Id);
        Assert.Null(backup.Watched[1].Channel?.Id);
        Assert.Equal("officialpsy", backup.Watched[1].Channel?.Name);

        // A resume point wins; otherwise the fraction watched, times the length.
        Assert.Equal(60, backup.VideoProgress["9bZkp7q19f0"].Position);
        Assert.Equal(106.5, backup.VideoProgress["dQw4w9WgXcQ"].Position);
    }

    [Fact]
    public void VideoPlaylistsAndTheBlocklistComeAcross()
    {
        var backup = AndroidBackup.Read(Backup());

        var playlist = Assert.Single(backup.VideoPlaylists);
        Assert.Equal("Watch Later", playlist.Name);
        Assert.Single(playlist.Videos);
        Assert.Equal("15 years ago", playlist.Videos[0].Published);
        Assert.Equal(Other, Assert.Single(backup.BlockedChannels).Id);
        Assert.Equal(["kJQP7kiw5Fk"], backup.HiddenVideos);
    }

    [Fact]
    public void ImportingTwiceAddsNothingTwice()
    {
        var library = new Library(null);
        var backup = AndroidBackup.Read(Backup());

        var first = library.ImportAndroid(backup);
        var second = library.ImportAndroid(backup);

        Assert.Equal(2, first.Subscriptions);
        Assert.Equal(2, first.Watched);
        Assert.Equal(1, first.VideoPlaylists);
        Assert.Equal(2, first.Blocked);
        Assert.True(second.IsEmpty);
        Assert.True(library.IsSubscribed(Channel));
        Assert.Equal(60, library.ProgressOf("9bZkp7q19f0")?.Position);
        Assert.True(library.IsUnwanted(new Video.Video("kJQP7kiw5Fk", "Despacito")));
        Assert.True(library.IsUnwanted(new Video.Video("abcdefghijk", "x", new ChannelRef(Other, "YouTube"))));
    }

    [Fact]
    public void WhatIsAlreadyWatchedHereStaysOnTop()
    {
        var library = new Library(null);
        library.RecordWatch(new Video.Video("aaaaaaaaaaa", "Here"));

        library.ImportAndroid(AndroidBackup.Read(Backup()));

        Assert.Equal(["aaaaaaaaaaa", "dQw4w9WgXcQ", "9bZkp7q19f0"], library.Watched.Select(v => v.Id));
    }

    [Fact]
    public void LocalSubscriptionsSurviveARestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "koda-tests-" + Guid.NewGuid());
        try
        {
            var library = new Library(folder);
            library.ImportAndroid(AndroidBackup.Read(Backup()));

            var reopened = new Library(folder);

            Assert.Equal(2, reopened.Subscriptions.Length);
            Assert.Single(reopened.VideoPlaylists);
            Assert.Single(reopened.BlockedChannels);
            Assert.Equal(1, reopened.HiddenVideoCount);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AChannelFeedListsItsUploadsWithoutShorts()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns:yt="http://www.youtube.com/xml/schemas/2015" xmlns:media="http://search.yahoo.com/mrss/" xmlns="http://www.w3.org/2005/Atom">
             <entry>
              <yt:videoId>dQw4w9WgXcQ</yt:videoId>
              <title>Never Gonna Give You Up</title>
              <link rel="alternate" href="https://www.youtube.com/watch?v=dQw4w9WgXcQ"/>
              <author><name>Rick Astley</name></author>
              <published>2026-11-12T10:00:00+00:00</published>
              <media:group><media:community><media:statistics views="1234567"/></media:community></media:group>
             </entry>
             <entry>
              <yt:videoId>abcdefghijk</yt:videoId>
              <title>A short</title>
              <link rel="alternate" href="https://www.youtube.com/shorts/abcdefghijk"/>
              <published>2026-11-13T10:00:00+00:00</published>
             </entry>
            </feed>
            """;
        var now = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

        var entries = VideoFeeds.ChannelFeed(xml, new LocalSubscription(Channel, "Rick Astley"), now);

        var entry = Assert.Single(entries);
        Assert.Equal("dQw4w9WgXcQ", entry.Video.Id);
        Assert.Equal(Channel, entry.Video.Channel?.Id);
        Assert.Equal("1.2M views", entry.Video.Views);
        Assert.Equal("3 days ago", entry.Video.Published);
    }

    [Fact]
    public void AFeedThatIsNotXmlGivesNothing()
    {
        Assert.Empty(VideoFeeds.ChannelFeed("<html", new LocalSubscription(Channel, "x"), DateTimeOffset.Now));
    }
}
