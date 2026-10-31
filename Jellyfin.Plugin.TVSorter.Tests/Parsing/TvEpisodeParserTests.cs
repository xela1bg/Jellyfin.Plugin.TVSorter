using Jellyfin.Plugin.TVSorter.Services;
using Xunit;

namespace Jellyfin.Plugin.TVSorter.Tests.Parsing;

public sealed class TvEpisodeParserTests
{
    private readonly TvEpisodeParser _parser = new();

    [Theory]
    [InlineData("Show.Name.S01E01.1080p.mkv", 1, 1, null)]
    [InlineData("Show.Name.S01E123.mkv", 1, 123, null)]
    [InlineData("Show.Name.S01E01-E02.mkv", 1, 1, 2)]
    [InlineData("Show.Name.S01E01E02.mkv", 1, 1, 2)]
    [InlineData("Show.Name.S01E01-02.mkv", 1, 1, 2)]
    [InlineData("Show.Name.2x05.mkv", 2, 5, null)]
    [InlineData("Show.Name.S00E01.mkv", 0, 1, null)]
    public void ParsesSupportedEpisodeMarkers(string fileName, int season, int episode, int? secondEpisode)
    {
        var parsed = _parser.ParseSeasonEpisode(fileName);

        Assert.True(parsed.HasValue);
        Assert.Equal((season, (int?)episode, secondEpisode), parsed.Value);
    }

    [Theory]
    [InlineData("Show.Name.Season.01.Pack.mkv", 1)]
    [InlineData("Show.Name.Season 1 Pack.mkv", 1)]
    [InlineData("Show.Name.S01.COMPLETE.1080p.mkv", 1)]
    public void ParsesSeasonOnlyForms(string text, int season)
    {
        var parsed = _parser.ParseSeasonEpisode(text);

        Assert.True(parsed.HasValue);
        Assert.Equal((season, (int?)null, (int?)null), parsed.Value);
    }

    [Fact]
    public void FilenameMarkerTakesPrecedenceOverFolderMarker()
    {
        var parsed = _parser.Parse("Show.S02E04.mkv", "Show.S01", "Collection", "Show");

        Assert.Equal(2, parsed.Season);
        Assert.Equal(4, parsed.Episode);
        Assert.Equal("Show", parsed.ShowName);
    }

    [Fact]
    public void EpisodeNamedDirectoryProvidesShowNameWhenNoTopLevelHintExists()
    {
        var parsed = _parser.Parse("Episode.mkv", "The.Show.S03E07", "Root", null);

        Assert.Equal("The Show", parsed.ShowName);
        Assert.Equal(3, parsed.Season);
        Assert.Equal(7, parsed.Episode);
    }

    [Fact]
    public void TopLevelShowHintWinsOverEpisodeDirectoryName()
    {
        var parsed = _parser.Parse("Episode.mkv", "S03E07", "The.Show", "The Show");

        Assert.Equal("The Show", parsed.ShowName);
        Assert.Equal(3, parsed.Season);
        Assert.Equal(7, parsed.Episode);
    }

    [Fact]
    public void LooseFileUsesSeasonOneFallbackAndOriginalFileName()
    {
        var parsed = _parser.Parse("Pilot.Final.mkv", "My.Show", null, "My Show");

        Assert.Equal("My Show", parsed.ShowName);
        Assert.Equal(1, parsed.Season);
        Assert.Null(parsed.Episode);
        Assert.True(parsed.UsedSeasonFallback);
        Assert.Equal("Pilot.Final.mkv", parsed.GetFileName("Pilot.Final.mkv"));
    }

    [Fact]
    public void RootLevelLooseFileUsesPythonTopLevelHintFromItsOwnName()
    {
        var parsed = _parser.Parse("Pilot.Final.mkv", "Series", null, _parser.CleanShowName("Pilot.Final.mkv"));

        Assert.Equal("Pilot Final Mkv", parsed.ShowName);
        Assert.Equal(1, parsed.Season);
    }

    [Fact]
    public void CleansReleaseAndBracketTagsIncludingTgxStylePrefix()
    {
        Assert.Equal("Show Name", _parser.CleanShowName("Show.Name.S01.COMPLETE.1080p.WEB-DL"));
        Assert.Equal("Show Name", _parser.CleanShowName("Show.Name.[1080p].[TGx]"));
    }

    [Fact]
    public void DocumentsInvariantNetTitleCasingDifferenceForAllUppercaseWords()
    {
        Assert.Equal("SHOW", _parser.CleanShowName("SHOW"));
    }

    [Fact]
    public void PreservesPythonTitleCaseForAccentedLatinNames()
    {
        Assert.Equal("\u00C9pisode", _parser.CleanShowName("\u00E9pisode"));
    }

    [Fact]
    public void NamesParsedDestinationLikePython()
    {
        var parsed = _parser.Parse("Show.Name.S01E02.MKV", "Pack", null, "Show Name");

        Assert.Equal("Season 01", parsed.SeasonDirectory);
        Assert.Equal("Show Name S01E02.mkv", parsed.GetFileName("Show.Name.S01E02.MKV"));
    }

    [Theory]
    [InlineData("Show.sample.mkv")]
    [InlineData("Show.Trailer.mp4")]
    public void RejectsSampleAndTrailerNames(string fileName)
    {
        Assert.False(_parser.IsVideo(fileName));
    }

    [Fact]
    public void KeepsPythonVideoExtensionSet()
    {
        foreach (var extension in new[] { ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".m2ts", ".wmv", ".mov" })
        {
            Assert.True(_parser.IsVideo("episode" + extension));
        }

        Assert.False(_parser.IsVideo("episode.webm"));
    }

    [Theory]
    [InlineData("screens")]
    [InlineData("SAMPLES")]
    [InlineData(".hidden")]
    public void SkipsPythonIgnoredDirectoryNames(string directoryName)
    {
        Assert.True(_parser.ShouldSkipDirectory(directoryName));
    }
}