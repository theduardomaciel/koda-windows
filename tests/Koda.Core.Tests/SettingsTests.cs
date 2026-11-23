using Koda.Core;

namespace Koda.Core.Tests;

/// <summary>
/// The settings: what the listener chose, kept in one file, with defaults that hold
/// when the file is missing or damaged.
/// </summary>
public class SettingsTests
{
    [Fact]
    public void ADamagedFileLeavesTheDefaults()
    {
        var file = Path.Combine(Fixtures.Scratch, $"settings-broken-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ this is not json");

        var settings = Settings.Load(file);

        Assert.True(settings.CloseToTray);
        Assert.True(settings.Autoplay);
        Assert.Equal(100, settings.Volume);
        Assert.Equal(11, settings.LyricsSources.Count);
    }

    [Fact]
    public void AMissingFileLeavesTheDefaults()
    {
        var settings = Settings.Load(Path.Combine(Fixtures.Scratch, $"settings-gone-{Guid.NewGuid():N}.json"));

        Assert.True(settings.CloseToTray);
        Assert.Equal(100, settings.Volume);
    }

    [Fact]
    public void LosslessDownloadsHaveNoServerUntilOneIsTypedIn()
    {
        var settings = Settings.Load(Path.Combine(Fixtures.Scratch, $"settings-lossless-{Guid.NewGuid():N}.json"));

        Assert.Null(settings.LosslessServerUrl);
        Assert.Null(settings.LosslessServerSecret);

        // Turned on with nowhere to download from, it stays the ordinary download.
        settings.LosslessDownloads = true;
        Assert.False(settings.UsesLosslessServer);

        settings.LosslessServerUrl = "https://example.com/a/token/";
        Assert.True(settings.UsesLosslessServer);
    }

    [Theory]
    [InlineData("https://example.com/a/token/", true)]
    [InlineData("http://192.168.1.4:8080/a/token", true)]
    [InlineData("  https://example.com/  ", true)]
    [InlineData("example.com/a/token", false)]
    [InlineData("ftp://example.com/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ALosslessServerIsAnHttpAddress(string? url, bool valid) =>
        Assert.Equal(valid, LosslessServer.IsValidUrl(url));

    [Fact]
    public void TheChoicesSurviveASaveAndLoad()
    {
        var settings = new Settings
        {
            CloseToTray = false,
            Autoplay = false,
            Volume = 42,
            LyricsMotion = false,
            BackingVocals = BackingVocalsMode.Hidden,
            SavesListeningHistory = false,
            SavesSearchHistory = false,
            Theme = Theme.Dark,
            LyricsFontSize = 26,
            NormalizesVolume = false,
            Crossfades = false,
            CrossfadeSeconds = 9,
            SharesOnDiscord = true,
            CacheLimit = CacheLimit.Large,
            DisabledLyricsSources = [LyricsSource.BetterLyrics],
        };

        var text = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(text);

        Assert.NotNull(back);
        Assert.False(back!.CloseToTray);
        Assert.False(back.Autoplay);
        Assert.Equal(42, back.Volume);
        Assert.False(back.LyricsMotion);
        Assert.Equal(BackingVocalsMode.Hidden, back.BackingVocals);
        Assert.False(back.SavesListeningHistory);
        Assert.False(back.SavesSearchHistory);
        Assert.Equal(Theme.Dark, back.Theme);
        Assert.Equal(26, back.LyricsFontSize);
        Assert.False(back.NormalizesVolume);
        Assert.False(back.Crossfades);
        Assert.Equal(9, back.CrossfadeSeconds);
        Assert.True(back.SharesOnDiscord);
        Assert.Equal(CacheLimit.Large, back.CacheLimit);
        Assert.False(back.IsEnabled(LyricsSource.BetterLyrics));
        Assert.True(back.IsEnabled(LyricsSource.YouLyPlus));
    }

    [Fact]
    public void AnUnsavedChangeStillNotifies()
    {
        var settings = new Settings();
        var heard = 0;
        Settings? last = null;
        settings.Changed += changed =>
        {
            heard++;
            last = changed;
        };

        settings.Update(s => s.Volume = 42, save: false);

        Assert.Equal(42, settings.Volume);
        Assert.Equal(1, heard);
        Assert.Same(settings, last);
    }

    [Fact]
    public void TheLastServiceCannotBeTurnedOff()
    {
        var settings = new Settings();

        // The lyrics need somewhere to come from.
        foreach (var source in settings.LyricsSources.SkipLast(1).ToList())
        {
            settings.SetEnabled(source, false);
        }

        Assert.Single(settings.LyricsSources);
        Assert.True(settings.IsEnabled(LyricsSource.KuGou));
    }

    [Fact]
    public void TheServicesAreAskedInTheOrderTheyWereSaved()
    {
        var settings = new Settings
        {
            LyricsSourceOrder = [LyricsSource.Lrclib, LyricsSource.YouLyPlus, LyricsSource.BetterLyrics],
            DisabledLyricsSources = [LyricsSource.YouLyPlus],
        };

        Assert.Equal([LyricsSource.Lrclib, LyricsSource.BetterLyrics], settings.LyricsSources);
    }

    [Fact]
    public void TheSourcesStartInBetterLyricsOrder()
    {
        var order = new Settings().LyricsSourceOrder;

        Assert.Equal(LyricsSource.BetterLyrics, order[0]);
        Assert.Contains(LyricsSource.Unison, order);
        Assert.Contains(LyricsSource.SpicyLyrics, order);
        Assert.Contains(LyricsSource.BiniLyrics, order);
        Assert.Contains(LyricsSource.BetterLyricsQQ, order);
        Assert.Contains(LyricsSource.BetterLyricsKugou, order);
    }

    [Fact]
    public void ReorderingTheServicesKeepsEveryOne()
    {
        var settings = new Settings();
        var moved = settings.LyricsSourceOrder[0];

        settings.SetLyricsSourceOrder(settings.LyricsSourceOrder.Skip(1).Append(moved));

        Assert.Equal(moved, settings.LyricsSourceOrder[^1]);
        Assert.Equal(11, settings.LyricsSourceOrder.Count);
    }

    [Fact]
    public void AServiceAddedLaterJoinsTheEndOfTheOrder()
    {
        // A file saved before a service existed still asks for it.
        var file = Path.Combine(Fixtures.Scratch, $"settings-order-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "lyricsSourceOrderVersion": 2, "lyricsSourceOrder": [ "Lrclib" ] }""");

        var settings = Settings.Load(file);

        Assert.Equal(11, settings.LyricsSourceOrder.Count);
        Assert.Equal(LyricsSource.Lrclib, settings.LyricsSourceOrder[0]);
    }

    [Fact]
    public void AnOldOrderTakesTheBestFirstOrderOnce()
    {
        var file = Path.Combine(Fixtures.Scratch, $"settings-old-order-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "lyricsSourceOrder": [ "YouLyPlus", "BetterLyrics", "Lrclib" ] }""");

        var settings = Settings.Load(file);

        Assert.Equal(Settings.CurrentLyricsSourceOrderVersion, settings.LyricsSourceOrderVersion);
        Assert.Equal(LyricsSource.BetterLyrics, settings.LyricsSourceOrder[0]);
        Assert.Equal(LyricsSource.Unison, settings.LyricsSourceOrder[1]);
        Assert.Equal(LyricsSource.SpicyLyrics, settings.LyricsSourceOrder[2]);
        Assert.Equal(LyricsSource.BiniLyrics, settings.LyricsSourceOrder[3]);
        Assert.Equal(11, settings.LyricsSourceOrder.Count);
    }

    [Fact]
    public void TheTitleBarSidebarToggleDefaultsOffAndIsRemembered()
    {
        Assert.False(new Settings().ShowsTitleBarSidebarToggle);

        var file = Path.Combine(Fixtures.Scratch, $"settings-sidebar-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "showsTitleBarSidebarToggle": true }""");

        Assert.True(Settings.Load(file).ShowsTitleBarSidebarToggle);
    }

    [Fact]
    public void ALyricsPresetMovesEveryLook()
    {
        var settings = new Settings();
        settings.ApplyLyricsPreset(LyricsPreset.Expressive);

        Assert.Equal(LyricsRenderer.Expressive, settings.LyricsRenderer);
        Assert.Equal(0.90, settings.LyricsLineSpacing, 2);
        Assert.Equal(0.20, settings.LyricsSoftness, 2);
        Assert.Equal(0.30, settings.LyricsIdleOpacity, 2);
        Assert.Equal(1.0, settings.LyricsGlowIntensity, 2);
        Assert.Equal("Segoe UI Variable Display", settings.LyricsFont);

        settings.ApplyLyricsPreset(LyricsPreset.Minimal);

        Assert.Equal(LyricsRenderer.Classic, settings.LyricsRenderer);
        Assert.Equal(0, settings.LyricsGlowIntensity);
        Assert.False(settings.LyricsMotion);
    }

    [Fact]
    public void TheOldLyricsSizeBecomesAPointSize()
    {
        var file = Path.Combine(Fixtures.Scratch, $"settings-old-size-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "lyricsSize": "Large" }""");

        var settings = Settings.Load(file);

        Assert.Equal(24, settings.LyricsFontSize);
        // The old name reads as nothing, so it is never written back.
        Assert.Null(settings.LegacyLyricsSize);
    }

    [Theory]
    [InlineData(10, 16)]
    [InlineData(28, 28)]
    [InlineData(90, 64)]
    public void TheLyricsSizeStaysBetweenItsSliderEnds(double asked, double kept) =>
        Assert.Equal(kept, new Settings { LyricsFontSize = asked }.LyricsFontSize);

    [Fact]
    public void AFileWithoutTheNewerChoicesTakesTheirDefaults()
    {
        var file = Path.Combine(Fixtures.Scratch, $"settings-older-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "volume": 70 }""");

        var settings = Settings.Load(file);

        Assert.Equal(70, settings.Volume);
        Assert.True(settings.NormalizesVolume);
        Assert.True(settings.Crossfades);
        Assert.Equal(5, settings.CrossfadeSeconds);
        Assert.False(settings.SharesOnDiscord);
        Assert.Equal(36, settings.LyricsFontSize);
        Assert.Equal(0.15, settings.LyricsIdleOpacity);
        Assert.Equal(32, settings.LyricsLineGap);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(40, 12)]
    [InlineData(6.4, 6)]
    [InlineData(double.NaN, 5)]
    public void TheOverlapStaysBetweenOneAndTwelveSeconds(double asked, double kept)
    {
        var settings = new Settings { CrossfadeSeconds = asked };

        Assert.Equal(kept, settings.CrossfadeSeconds);
    }
}
