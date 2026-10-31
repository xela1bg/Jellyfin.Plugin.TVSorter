using Jellyfin.Plugin.TVSorter.Services;
using Xunit;

namespace Jellyfin.Plugin.TVSorter.Tests.Services;

public sealed class PathValidatorTests
{
    [Fact]
    public void DetectsSamePathAndAncestorOverlap()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "child");

        Assert.True(PathValidator.IsWithinOrEqual(root, root));
        Assert.True(PathValidator.IsWithinOrEqual(root, child));
        Assert.False(PathValidator.IsWithinOrEqual(child, root));
    }

    [Fact]
    public void CanonicalizesDotSegments()
    {
        var path = Path.Combine(Path.GetTempPath(), "tv-sorter", "..", "tv-sorter");

        Assert.True(SymlinkManager.PathEquals(PathValidator.CanonicalizeDirectoryPath(path), Path.Combine(Path.GetTempPath(), "tv-sorter")));
    }

    [Fact]
    public async Task RejectsSourceContainingPluginPrivateDataBeforeTargetCreation()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        var validator = new PathValidator(new SymlinkManager(), new ManagedLinkManifestStore(source));

        var result = await validator.ValidateAsync(source, target, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("private data", result.Error.ToLowerInvariant());
        Assert.False(Directory.Exists(target));
        Directory.Delete(root, true);
    }
}