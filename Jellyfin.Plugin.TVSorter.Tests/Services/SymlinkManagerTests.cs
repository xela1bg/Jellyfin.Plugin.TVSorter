using Jellyfin.Plugin.TVSorter.Services;
using Xunit;

namespace Jellyfin.Plugin.TVSorter.Tests.Services;

public sealed class SymlinkManagerTests
{
    [SkippableFact]
    public void ReadsStoredTargetForBrokenSymbolicLink()
    {
        var manager = new SymlinkManager();
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var missingSource = Path.Combine(directory, "missing.mkv");
        var linkPath = Path.Combine(directory, "episode.mkv");

        try
        {
            try
            {
                manager.Create(linkPath, missingSource);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Skip.If(true, $"Host does not permit symbolic-link creation: {exception.Message}");
                return;
            }

            Assert.False(File.Exists(missingSource));
            Assert.Equal(Path.GetFullPath(missingSource), Path.GetFullPath(manager.ReadStoredTarget(linkPath)!));
            Assert.True(manager.StoredTargetMatches(linkPath, missingSource));
            manager.DeleteVerifiedLink(linkPath, missingSource);
            Assert.Null(manager.ReadStoredTarget(linkPath));
        }
        finally
        {
            if (manager.ReadStoredTarget(linkPath) is not null)
            {
                manager.DeleteVerifiedLink(linkPath, manager.ReadStoredTarget(linkPath)!);
            }

            Directory.Delete(directory, true);
        }
    }
}