using Jellyfin.Plugin.TVSorter.Models;
using Xunit;

namespace Jellyfin.Plugin.TVSorter.Tests.Models;

public sealed class BuildResultTests
{
    [Theory]
    [InlineData(false, 0, BuildStatus.Success)]
    [InlineData(false, 1, BuildStatus.Failed)]
    [InlineData(true, 0, BuildStatus.Cancelled)]
    [InlineData(true, 2, BuildStatus.Cancelled)]
    public void FinalStatusDependsOnCancellationAndErrorsOnly(bool wasCancelled, int errorCount, BuildStatus expected)
    {
        Assert.Equal(expected, BuildResult.ResolveFinalStatus(wasCancelled, errorCount));
    }

    [Fact]
    public void ConflictsAndSkippedItemsRemainAvailableForSuccessfulResult()
    {
        var result = new BuildResult
        {
            FilesDiscovered = 939,
            EpisodesMatched = 867,
            LinksAlreadyCorrect = 865,
            FilesSkipped = 2,
            Conflicts =
            [
                new ConflictInfo
                {
                    DestinationPath = "The Originals/Season 01/The Originals S01E01.mkv",
                    SourcePaths = ["source-a.mkv", "source-b.mkv"],
                    Reason = "Multiple source files map to the same destination."
                }
            ],
            SkippedItems = [new SkippedItemInfo { Path = "source-a.mkv", Category = "DestinationConflict", Reason = "Ambiguous destination." }]
        };

        result.Status = BuildResult.ResolveFinalStatus(wasCancelled: false, result.Errors.Count);

        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Single(result.Conflicts);
        Assert.Equal(2, result.Conflicts[0].SourcePaths.Count);
        Assert.Contains("completed successfully", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.LastError);
    }
}