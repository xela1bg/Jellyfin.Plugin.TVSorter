using System.Net;
using Jellyfin.Plugin.TVSorter.Configuration;
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

    [SkippableTheory]
    [InlineData("Heated Rivalry (2025)", "Heated Rivalry")]
    [InlineData("The Originals 2015", "The Originals")]
    [InlineData("Shifting Gears 2025", "Shifting Gears")]
    [InlineData("Dark Matter 2024", "Dark Matter")]
    [InlineData("Foundation (2021)", "Foundation")]
    [InlineData("Chicago PD", "Chicago Pd")]
    [InlineData("FROM", "From")]
    public async Task LocalNormalizationCreatesLinksWithoutOnlineLookup(string raw, string normalized)
    {
        using var fixture = new BuilderFixture();
        var source = fixture.AddSourceFile(raw, "S01E01.mkv");
        fixture.RequireSymlinks();
        var result = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Equal(1, result.LinksCreated);
        Assert.True(new SymlinkManager().StoredTargetMatches(fixture.GetEpisodePath(normalized, 1, 1), source));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [SkippableTheory]
    [InlineData("[]", HttpStatusCode.ServiceUnavailable)]
    [InlineData("[]", HttpStatusCode.TooManyRequests)]
    [InlineData("[]", HttpStatusCode.OK)]
    [InlineData("invalid-json", HttpStatusCode.OK)]
    [InlineData("[{\"score\":15,\"show\":{\"id\":1,\"name\":\"Hawaii Five-0\"}},{\"score\":14,\"show\":{\"id\":2,\"name\":\"Hawaii Five-0\"}}]", HttpStatusCode.OK)]
    public async Task ProviderFailureOrAmbiguityStillCreatesLocalLinks(string json, HttpStatusCode status)
    {
        using var fixture = new BuilderFixture(new MockTvmazeHandler(json, status));
        fixture.Configuration.UseTvmaze = true;
        var source = fixture.AddSourceFile("Hawaii Flve 0", "S01E01.mkv");
        fixture.RequireSymlinks();
        var result = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Equal(1, result.LinksCreated);
        Assert.True(new SymlinkManager().StoredTargetMatches(fixture.GetEpisodePath("Hawaii Flve 0", 1, 1), source));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailureOrTimeoutStillCreatesLinks(bool timeout)
    {
        var handler = new MockTvmazeHandler("[]")
        {
            Failure = timeout ? new OperationCanceledException("mock timeout") : new HttpRequestException("mock offline")
        };
        using var fixture = new BuilderFixture(handler);
        fixture.Configuration.UseTvmaze = true;
        fixture.AddSourceFile("Foundation (2021)", "S01E01.mkv");
        fixture.RequireSymlinks();
        var result = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Equal(1, result.LinksCreated);
        Assert.True(File.Exists(fixture.GetEpisodePath("Foundation", 1, 1)));
    }

    [SkippableFact]
    public async Task CanonicalMatchIsSharedByEpisodesCachedAndIdempotent()
    {
        using var fixture = new BuilderFixture(HawaiiMatch());
        fixture.Configuration.UseTvmaze = true;
        var firstSource = fixture.AddSourceFile("Hawaii Flve 0", "S01E01.mkv");
        fixture.AddSourceFile("Hawaii Flve 0", "S01E02.mkv");
        fixture.RequireSymlinks();
        var first = await fixture.BuildAsync();
        var second = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, first.Status);
        Assert.Equal(2, first.LinksCreated);
        Assert.Equal(BuildStatus.Success, second.Status);
        Assert.Equal(2, second.LinksAlreadyCorrect);
        Assert.Equal(0, second.LinksCreated);
        Assert.Equal(0, second.StaleLinksRemoved);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.True(new SymlinkManager().StoredTargetMatches(fixture.GetEpisodePath("Hawaii Five-0", 1, 1), firstSource));
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, "Hawaii Flve 0")));
    }

    [SkippableFact]
    public async Task CanonicalizationReconcilesOnlyManagedLinksAndEmptyManagedDirectories()
    {
        using var fixture = new BuilderFixture(HawaiiMatch());
        var source = fixture.AddSourceFile("Hawaii Flve 0", "S01E01.mkv");
        fixture.AddSourceFile("Hawaii Flve 0", "S02E01.mkv");
        fixture.RequireSymlinks();
        Assert.Equal(2, (await fixture.BuildAsync()).LinksCreated);
        var oldRoot = Path.Combine(fixture.Target, "Hawaii Flve 0");
        var untrackedFile = Path.Combine(oldRoot, "keep.txt");
        File.WriteAllText(untrackedFile, "user data");
        var untrackedLink = Path.Combine(oldRoot, "untracked.mkv");
        new SymlinkManager().Create(untrackedLink, source);
        var untrackedDirectory = Path.Combine(oldRoot, "untracked-empty");
        Directory.CreateDirectory(untrackedDirectory);
        var canonicalRoot = Path.Combine(fixture.Target, "Hawaii Five-0");
        Directory.CreateDirectory(canonicalRoot);
        fixture.Configuration.UseTvmaze = true;

        var result = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Equal(2, result.LinksCreated);
        Assert.Equal(2, result.StaleLinksRemoved);
        Assert.False(Directory.Exists(Path.Combine(oldRoot, "Season 01")));
        Assert.False(Directory.Exists(Path.Combine(oldRoot, "Season 02")));
        Assert.True(Directory.Exists(oldRoot));
        Assert.Equal("user data", File.ReadAllText(untrackedFile));
        Assert.True(new SymlinkManager().StoredTargetMatches(untrackedLink, source));
        Assert.True(Directory.Exists(untrackedDirectory));
        Assert.True(File.Exists(source));
        Assert.DoesNotContain("Hawaii Five-0", fixture.ManifestStore.Load(PathValidator.CanonicalizeDirectoryPath(fixture.Target)).Manifest.Directories);
        Assert.Equal(2, (await fixture.BuildAsync()).LinksAlreadyCorrect);
    }

    [SkippableFact]
    public async Task RemovesEmptyOldManagedShowAfterSuccessfulReconciliation()
    {
        using var fixture = new BuilderFixture(HawaiiMatch());
        fixture.AddSourceFile("Hawaii Flve 0", "S01E01.mkv");
        fixture.RequireSymlinks();
        await fixture.BuildAsync();
        fixture.Configuration.UseTvmaze = true;
        var result = await fixture.BuildAsync();
        Assert.Equal(1, result.StaleLinksRemoved);
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, "Hawaii Flve 0")));
        Assert.True(File.Exists(fixture.GetEpisodePath("Hawaii Five-0", 1, 1)));
    }

    [SkippableFact]
    public async Task BlockedCanonicalDestinationRetainsOldManagedLink()
    {
        using var fixture = new BuilderFixture(HawaiiMatch());
        var source = fixture.AddSourceFile("Hawaii Flve 0", "S01E01.mkv");
        fixture.RequireSymlinks();
        await fixture.BuildAsync();
        var canonicalPath = fixture.GetEpisodePath("Hawaii Five-0", 1, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(canonicalPath)!);
        File.WriteAllText(canonicalPath, "user data");
        fixture.Configuration.UseTvmaze = true;
        var result = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Equal(0, result.StaleLinksRemoved);
        Assert.True(new SymlinkManager().StoredTargetMatches(fixture.GetEpisodePath("Hawaii Flve 0", 1, 1), source));
        Assert.Equal("user data", File.ReadAllText(canonicalPath));
        Assert.NotEmpty(result.Warnings);
        Assert.Equal(0, (await fixture.BuildAsync()).StaleLinksRemoved);
    }

    [SkippableFact]
    public async Task ReconcilesBothObservedNamePairsUsingRealisticSearchResponses()
    {
        var handler = new MockTvmazeHandler("[]")
        {
            Respond = request => request.RequestUri!.Query.Contains("Flve", StringComparison.Ordinal) ? "[]"
                : request.RequestUri!.Query.Contains("Hawaii", StringComparison.Ordinal)
                    ? "[{\"score\":0.9,\"show\":{\"id\":23,\"name\":\"Hawaii Five-0\"}}]"
                    : "[{\"score\":0.5287102,\"show\":{\"id\":58933,\"name\":\"Happy Face\"}}]"
        };
        using var fixture = new BuilderFixture(handler);
        var sources = new[]
        {
            fixture.AddSourceFile("Hawaii Flve 0", "S01E01.mkv"),
            fixture.AddSourceFile("Hawaii Five-0", "S01E02.mkv"),
            fixture.AddSourceFile("Happy Faces", "S01E01.mkv"),
            fixture.AddSourceFile("Happy Face", "S01E02.mkv")
        };
        fixture.RequireSymlinks();
        Assert.Equal(4, (await fixture.BuildAsync()).LinksCreated);
        fixture.Configuration.UseTvmaze = true;
        var result = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Equal(2, result.StaleLinksRemoved);
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, "Hawaii Flve 0")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, "Happy Faces")));
        Assert.True(new SymlinkManager().StoredTargetMatches(fixture.GetEpisodePath("Hawaii Five-0", 1, 1), sources[0]));
        Assert.True(new SymlinkManager().StoredTargetMatches(fixture.GetEpisodePath("Happy Face", 1, 1), sources[2]));
        Assert.All(sources, source => Assert.True(File.Exists(source)));
        Assert.Equal(4, (await fixture.BuildAsync()).LinksAlreadyCorrect);
        Assert.Equal(4, handler.Requests);
    }

    [SkippableFact]
    public async Task UnmarkedFilesKeepTheirOriginalBasenameUnderNormalizedShow()
    {
        using var fixture = new BuilderFixture();
        var source = fixture.AddSourceFile("Foundation (2021)", "original.mkv");
        fixture.RequireSymlinks();
        var result = await fixture.BuildAsync();
        Assert.Equal(BuildStatus.Success, result.Status);
        Assert.Equal(1, result.LinksCreated);
        var destination = Path.Combine(fixture.Target, "Foundation", "Season 01", "original.mkv");
        Assert.True(new SymlinkManager().StoredTargetMatches(destination, source));
    }

    private static MockTvmazeHandler HawaiiMatch() => new("[{\"score\":15,\"show\":{\"id\":23,\"name\":\"Hawaii Five-0\"}}]");

    private sealed class BuilderFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tv-sorter-test-" + Guid.NewGuid().ToString("N"));

        public BuilderFixture(MockTvmazeHandler? handler = null)
        {
            Source = Path.Combine(_root, "source");
            Target = Path.Combine(_root, "target");
            Data = Path.Combine(_root, "data");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Target);
            Directory.CreateDirectory(Data);
            ManifestStore = new ManagedLinkManifestStore(Data);
            Handler = handler ?? new MockTvmazeHandler("[]");
            Canonicalizer = new ShowNameCanonicalizer(new HttpClient(Handler), ManifestStore, () => Configuration);
            var symlinkManager = new SymlinkManager();
            Builder = new VirtualTvLibraryBuilder(
                new TvEpisodeParser(),
                new PathValidator(symlinkManager, ManifestStore),
                symlinkManager,
                ManifestStore,
                NullLogger<VirtualTvLibraryBuilder>.Instance,
                Canonicalizer);
        }

        public string Source { get; }

        public string Target { get; }

        public string Data { get; }

        public ManagedLinkManifestStore ManifestStore { get; }

        public PluginConfiguration Configuration { get; } = new() { UseTvmaze = false };

        public MockTvmazeHandler Handler { get; }

        private ShowNameCanonicalizer Canonicalizer { get; }

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
            Canonicalizer.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }
}