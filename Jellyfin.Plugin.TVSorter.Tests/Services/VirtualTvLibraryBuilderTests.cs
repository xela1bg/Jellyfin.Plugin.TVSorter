using Jellyfin.Plugin.TVSorter.Models;
using Jellyfin.Plugin.TVSorter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TVSorter.Tests.Services;

public sealed class VirtualTvLibraryBuilderTests
{
    [SkippableFact]
    public async Task SynchronizationIsIdempotentAndRecreatesManuallyMissingLinks()
    {
        using var fixture = new BuilderFixture();
        var source = fixture.AddSourceFile("Show.Name", "Show.Name.S01E01.mkv");
        fixture.RequireSymlinks();

        var first = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, first.Status);
        Assert.Equal(1, first.LinksCreated);

        var second = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, second.Status);
        Assert.Equal(1, second.LinksAlreadyCorrect);

        var link = fixture.GetEpisodePath("Show Name", 1, 1);
        File.Delete(link);
        Assert.False(File.Exists(link));

        var third = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, third.Status);
        Assert.Equal(1, third.LinksCreated);
        Assert.True(new SymlinkManager().StoredTargetMatches(link, source));
    }

    [SkippableFact]
    public async Task UpdatesAManagedLinkWhenItsSourcePathChanges()
    {
        using var fixture = new BuilderFixture();
        var oldSource = fixture.AddSourceFile("Show.Name", "Show.Name.S01E01.mkv");
        fixture.RequireSymlinks();

        Assert.Equal(BuildStatus.Success, (await fixture.BuildAsync()).Status);
        File.Delete(oldSource);
        var newSource = fixture.AddSourceFile("Show.Name", "Season.Pack", "Show.Name.S01E01.mkv");

        var result = await fixture.BuildAsync();

        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Equal(1, result.LinksUpdated);
        Assert.True(new SymlinkManager().StoredTargetMatches(fixture.GetEpisodePath("Show Name", 1, 1), newSource));
    }

    [SkippableFact]
    public async Task RemovesBrokenStaleManagedLinkAndManagedDirectories()
    {
        using var fixture = new BuilderFixture();
        var source = fixture.AddSourceFile("Show.Name", "Show.Name.S01E01.mkv");
        fixture.RequireSymlinks();

        Assert.Equal(BuildStatus.Success, (await fixture.BuildAsync()).Status);
        File.Delete(source);

        var result = await fixture.BuildAsync();

        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Equal(1, result.StaleLinksRemoved);
        Assert.Equal(2, result.EmptyDirectoriesRemoved);
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, "Show Name")));
    }

    [SkippableFact]
    public async Task LeavesUntrackedDestinationUntouchedAndReportsSuccessfulConflict()
    {
        using var fixture = new BuilderFixture();
        fixture.AddSourceFile("Show.Name", "Show.Name.S01E01.mkv");
        var destination = fixture.GetEpisodePath("Show Name", 1, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "user data");
        fixture.RequireSymlinks();

        var result = await fixture.BuildAsync();

        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Single(result.Conflicts);
        Assert.Equal(Path.GetFullPath(destination), result.Conflicts[0].DestinationPath);
        Assert.Single(result.Conflicts[0].SourcePaths);
        Assert.Equal("user data", File.ReadAllText(destination));
    }

    [SkippableFact]
    public async Task DuplicateDesiredDestinationsAreConflictsAndNeitherSourceWins()
    {
        using var fixture = new BuilderFixture();
        fixture.AddSourceFile("Show.Name", "S01E01.mkv");
        fixture.AddSourceFile("Show_Name", "S01E01.mkv");
        fixture.RequireSymlinks();

        var result = await fixture.BuildAsync();

        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Single(result.Conflicts);
        Assert.Equal(2, result.Conflicts[0].SourcePaths.Count);
        Assert.Contains("multiple source files", result.Conflicts[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.GetEpisodePath("Show Name", 1, 1)));
    }

    [SkippableFact]
    public async Task CorruptManifestPreventsFilesystemSynchronization()
    {
        using var fixture = new BuilderFixture();
        fixture.AddSourceFile("Show.Name", "Show.Name.S01E01.mkv");
        fixture.RequireSymlinks();

        var result = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, result.Status);
        var manifestPath = fixture.ManifestStore.GetManifestFilePath(PathValidator.CanonicalizeDirectoryPath(fixture.Target));
        File.WriteAllText(manifestPath, "not-json");
        var existingLink = fixture.GetEpisodePath("Show Name", 1, 1);

        var next = await fixture.BuildAsync();

        Assert.Equal(BuildStatus.Failed, next.Status);
        Assert.True(new SymlinkManager().StoredTargetMatches(existingLink, Path.Combine(fixture.Source, "Show.Name", "Show.Name.S01E01.mkv")));
    }

    private sealed class BuilderFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tv-sorter-test-" + Guid.NewGuid().ToString("N"));

        public BuilderFixture()
        {
            Source = Path.Combine(_root, "source");
            Target = Path.Combine(_root, "target");
            Data = Path.Combine(_root, "data");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Target);
            Directory.CreateDirectory(Data);
            ManifestStore = new ManagedLinkManifestStore(Data);
            var symlinkManager = new SymlinkManager();
            Builder = new VirtualTvLibraryBuilder(
                new TvEpisodeParser(),
                new PathValidator(symlinkManager, ManifestStore),
                symlinkManager,
                ManifestStore,
                NullLogger<VirtualTvLibraryBuilder>.Instance);
        }

        public string Source { get; }

        public string Target { get; }

        public string Data { get; }

        public ManagedLinkManifestStore ManifestStore { get; }

        private VirtualTvLibraryBuilder Builder { get; }

        public string AddSourceFile(params string[] parts)
        {
            var segments = new List<string> { Source };
            segments.AddRange(parts);
            var path = Path.Combine(segments.ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "media");
            return path;
        }

        public string GetEpisodePath(string show, int season, int episode)
        {
            return Path.Combine(Target, show, $"Season {season:00}", $"{show} S{season:00}E{episode:00}.mkv");
        }

        public void RequireSymlinks()
        {
            try
            {
                new SymlinkManager().ProbeCapability(Target, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Skip.If(true, $"Host does not permit symbolic-link creation: {exception.Message}");
            }
        }

        public Task<BuildResult> BuildAsync() => Builder.SynchronizeAsync(Source, Target, CancellationToken.None);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }
}