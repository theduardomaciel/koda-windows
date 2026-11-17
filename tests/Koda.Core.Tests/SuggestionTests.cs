using Koda.Core;
using Koda.Core.Parsing;

namespace Koda.Core.Tests;

/// <summary>
/// The rich side of search suggestions: YouTube returns result-shaped rows
/// beside the query expansions, but they are ranked, not filtered. Only a title
/// that really names the typed query may become a quick answer.
/// </summary>
public class SuggestionTests
{
    [Fact]
    public void CityWallsPromotesTheAudioTrackAndCollapsesItsVideo()
    {
        var suggestions = Pages.Suggestions(Fixtures.Load("search-suggestions-city-walls.json"), "city walls");

        var entity = Assert.Single(suggestions.Entities);
        Assert.Equal(SuggestionKind.Song, entity.Kind);
        Assert.Equal("City Walls", entity.Text);
        Assert.True(entity.Confidence >= SuggestionRanking.Threshold);

        var song = Assert.IsType<MusicItem.SongRecord>(entity.Item).Value;
        Assert.Equal("twenty one pilots", song.ArtistLine);
        Assert.Equal("Breach", song.Album?.Title);
        Assert.False(song.IsVideo);
    }

    [Fact]
    public void BreachPromotesTheAlbumAndNotTheUnrelatedRapSong()
    {
        var suggestions = Pages.Suggestions(Fixtures.Load("search-suggestions-breach.json"), "breach");

        var entity = Assert.Single(suggestions.Entities);
        Assert.Equal(SuggestionKind.Album, entity.Kind);
        Assert.Equal("Breach", entity.Text);
        var album = Assert.IsType<MusicItem.AlbumRecord>(entity.Item).Value;
        Assert.Equal("twenty one pilots", album.ArtistLine);
        Assert.Equal(2025, album.Year);
    }

    [Fact]
    public void ArtistNamePromotesTheArtistAndNotUnrelatedSongs()
    {
        var suggestions = Pages.Suggestions(Fixtures.Load("search-suggestions-twenty-one-pilots.json"), "twenty one pilots");

        var entity = Assert.Single(suggestions.Entities);
        Assert.Equal(SuggestionKind.Artist, entity.Kind);
        Assert.Equal("Twenty One Pilots", entity.Text);
    }

    [Fact]
    public void TrenchPromotesTheAlbumAndNotSongsThatOnlyContainTheWord()
    {
        var suggestions = Pages.Suggestions(Fixtures.Load("search-suggestions-trench.json"), "trench");

        var entity = Assert.Single(suggestions.Entities);
        Assert.Equal(SuggestionKind.Album, entity.Kind);
        Assert.Equal("Trench", entity.Text);
        Assert.DoesNotContain(suggestions.Entities, e => e.Text.Contains("Trench Town", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(suggestions.Entities, e => e.Text.Contains("Trench Coat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void QuerySuggestionsSurviveBesideTheRichRows()
    {
        var suggestions = Pages.Suggestions(Fixtures.Load("search-suggestions-city-walls.json"), "city walls");

        Assert.NotEmpty(suggestions.Queries);
        Assert.Equal("city walls", suggestions.Queries[0]);
        Assert.Contains("city walls twenty one pilots", suggestions.Queries);
    }

    [Fact]
    public void AResponseWithoutRichRowsStillCarriesItsQueries()
    {
        var root = Json.Parse("""
            {"contents":[{"searchSuggestionsSectionRenderer":{"contents":[
              {"searchSuggestionRenderer":{"suggestion":{"runs":[{"text":"trench"}]}}},
              {"searchSuggestionRenderer":{"suggestion":{"runs":[{"text":"trench live"}]}}}
            ]}}]}
            """);

        var suggestions = Pages.Suggestions(root, "trench");

        Assert.Empty(suggestions.Entities);
        Assert.Equal(["trench", "trench live"], suggestions.Queries);
    }

    [Fact]
    public void GenericAndVeryShortQueriesDoNotPromoteAnything()
    {
        var song = new MusicItem.SongRecord(new Song(
            "id", "Love", "someone", [], null, null, null, false, null));

        Assert.Empty(SuggestionRanking.Rank("the", [song]));
        Assert.Empty(SuggestionRanking.Rank("love", [song]));
        Assert.Empty(SuggestionRanking.Rank("lve", [song]));
    }

    [Fact]
    public void AOneCharacterTypoInALongTitleStillMatches()
    {
        var album = new MusicItem.AlbumRecord(new AlbumSummary(
            "MPREb_test", "Blurryface", "twenty one pilots", [], 2015, ReleaseKind.Album, null));

        var entity = Assert.Single(SuggestionRanking.Rank("blurryfac", [album]));
        Assert.Equal("Blurryface", entity.Text);
    }

    [Fact]
    public void NormalizeRemovesCaseAccentsAndPunctuation()
    {
        Assert.Equal("bjork post", SuggestionRanking.Normalize("  Björk — Post! "));
    }

    [Fact]
    public void RankKeepsTheAudioTrackWhenAVideoHasTheSameTitle()
    {
        var song = new MusicItem.SongRecord(new Song(
            "audio", "City Walls", "twenty one pilots", [], null, null, null, false, null));
        var video = new MusicItem.SongRecord(new Song(
            "video", "City Walls", "twenty one pilots", [], null, null, null, true, null));

        var entity = Assert.Single(SuggestionRanking.Rank("city walls", [video, song]));
        Assert.Equal(SuggestionKind.Song, entity.Kind);
        Assert.Equal("audio", Assert.IsType<MusicItem.SongRecord>(entity.Item).Value.Id);
    }
}
