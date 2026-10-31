using System.Reflection;
using Jellyfin.Plugin.TVSorter.Configuration;
using Jellyfin.Plugin.TVSorter.Models;
using Jellyfin.Plugin.TVSorter.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.TVSorter.Tests.Services;

public sealed class LibrarySelectionServiceTests
{
    [Fact]
    public void OneSelectedShowsLibraryAutomaticallyResolvesItsOnlyLocation()
    {
        var target = MakePath("shows");
        var configuration = new PluginConfiguration { SelectedLibraryId = "library-a" };
        var service = CreateService(new ShowsLibrary("library-a", "English Series", [target]));

        var result = service.GetState(configuration);

        Assert.True(result.IsValid);
        Assert.Equal("English Series", result.SelectedLibraryName);
        Assert.Equal(Path.GetFullPath(target), result.EffectiveTargetPath);
        Assert.Equal(Path.GetFullPath(target), result.SelectedLibraryLocation);
        Assert.True(result.NeedsMigration);
    }

    [Fact]
    public void SavingNewSingleLocationSelectionSetsEffectiveTargetPath()
    {
        var target = MakePath("new-shows");
        var configuration = new PluginConfiguration { SelectedLibraryId = "library-a" };
        var service = CreateService(new ShowsLibrary("library-a", "Kids TV", [target]));

        var applied = service.TryApplySelection(configuration, new PluginConfiguration(), out var result);

        Assert.True(applied);
        Assert.Equal(Path.GetFullPath(target), configuration.TargetPath);
        Assert.Equal(Path.GetFullPath(target), configuration.SelectedLibraryLocation);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void MultipleShowsLibrariesAreListedWithoutSelectingOneForNewConfiguration()
    {
        var service = CreateService(
            new ShowsLibrary("english", "English Series", [MakePath("english")]),
            new ShowsLibrary("kids", "Kids TV", [MakePath("kids")]));

        var result = service.GetState(new PluginConfiguration());

        Assert.Equal(2, result.Libraries.Count);
        Assert.False(result.IsValid);
        Assert.Null(result.SelectedLibraryId);
    }

    [Fact]
    public void JellyfinProviderExcludesNonShowsCollectionTypes()
    {
        var libraryManager = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        var proxy = (LibraryManagerProxy)(object)libraryManager;
        proxy.VirtualFolders =
        [
            VirtualFolder("shows-id", "Kids TV", CollectionTypeOptions.tvshows, MakePath("shows")),
            VirtualFolder("movies-id", "My Shows", CollectionTypeOptions.movies, MakePath("movies")),
            VirtualFolder("music-id", "Series Music", CollectionTypeOptions.music, MakePath("music"))
        ];

        var libraries = new JellyfinShowsLibraryProvider(libraryManager).GetShowsLibraries();

        var library = Assert.Single(libraries);
        Assert.Equal("shows-id", library.Id);
    }

    [Fact]
    public void MultipleLocationsRequireExplicitSelectionAndNeverPickFirst()
    {
        var first = MakePath("first");
        var second = MakePath("second");
        var service = CreateService(new ShowsLibrary("shows", "My Shows", [first, second]));

        var missing = service.GetState(new PluginConfiguration { SelectedLibraryId = "shows" });
        var selected = service.GetState(new PluginConfiguration
        {
            SelectedLibraryId = "shows",
            SelectedLibraryLocation = Path.GetFullPath(second)
        });

        Assert.False(missing.IsValid);
        Assert.Contains("multiple locations", missing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(selected.IsValid);
        Assert.Equal(Path.GetFullPath(second), selected.EffectiveTargetPath);
    }

    [Fact]
    public void UniqueLegacyTargetMatchMigratesAndPreservesSavedTargetString()
    {
        var target = MakePath("shows");
        var legacyTarget = Path.Combine(Path.GetTempPath(), "tv-sorter-library-tests", "legacy", "..", "shows");
        var configuration = new PluginConfiguration { TargetPath = legacyTarget };
        var previous = Clone(configuration);
        var service = CreateService(new ShowsLibrary("shows-id", "Family Television", [target]));

        var state = service.GetState(configuration);
        var applied = service.TryApplySelection(configuration, previous, out var savedState);

        Assert.True(state.IsValid);
        Assert.True(state.NeedsMigration);
        Assert.True(applied);
        Assert.Equal("shows-id", configuration.SelectedLibraryId);
        Assert.Equal(Path.GetFullPath(target), configuration.SelectedLibraryLocation);
        Assert.Equal(legacyTarget, configuration.TargetPath);
        Assert.True(savedState.PreserveExistingTargetPath);
    }

    [Fact]
    public void UnmatchedLegacyTargetIsPreservedAndDoesNotSelectUnrelatedLibrary()
    {
        var legacyTarget = MakePath("legacy");
        var configuration = new PluginConfiguration { TargetPath = legacyTarget };
        var service = CreateService(new ShowsLibrary("shows-id", "My Shows", [MakePath("shows")]));

        var result = service.GetState(configuration);

        Assert.False(result.IsValid);
        Assert.Null(result.SelectedLibraryId);
        Assert.Equal(legacyTarget, configuration.TargetPath);
        Assert.Contains("not associated", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmbiguousLegacyTargetMatchRequiresExplicitSelection()
    {
        var sharedTarget = MakePath("shared");
        var configuration = new PluginConfiguration { TargetPath = sharedTarget };
        var service = CreateService(
            new ShowsLibrary("shows-a", "Shows A", [sharedTarget]),
            new ShowsLibrary("shows-b", "Shows B", [sharedTarget]));

        var result = service.GetState(configuration);

        Assert.False(result.IsValid);
        Assert.Null(result.SelectedLibraryId);
        Assert.Contains("multiple", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeletedSelectedLibraryIsNotReplacedByAnotherLibrary()
    {
        var configuration = new PluginConfiguration
        {
            SelectedLibraryId = "deleted-id",
            SelectedLibraryLocation = MakePath("old"),
            TargetPath = MakePath("old")
        };
        var service = CreateService(new ShowsLibrary("other-id", "Other Shows", [MakePath("other")]));

        var result = service.GetState(configuration);

        Assert.False(result.IsValid);
        Assert.Equal("deleted-id", result.SelectedLibraryId);
        Assert.Contains("no longer available", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovedSelectedLocationIsPreservedAndRequiresCorrection()
    {
        var removedLocation = MakePath("removed");
        var configuration = new PluginConfiguration
        {
            SelectedLibraryId = "shows",
            SelectedLibraryLocation = removedLocation,
            TargetPath = removedLocation
        };
        var service = CreateService(new ShowsLibrary("shows", "My Shows", [MakePath("current")]));

        var result = service.GetState(configuration);

        Assert.False(result.IsValid);
        Assert.Equal(removedLocation, result.SelectedLibraryLocation);
        Assert.Equal(removedLocation, configuration.TargetPath);
        Assert.Contains("location is no longer available", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolvedTargetStillPassesThroughPathValidator()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Directory.CreateDirectory(Path.Combine(root, "unsorted")).FullName;
        var target = Path.Combine(source, "shows-target");
        Directory.CreateDirectory(root);
        var manifestStore = new ManagedLinkManifestStore(Path.Combine(root, "data"));
        var selection = CreateService(new ShowsLibrary("shows", "Shows", [target]))
            .GetState(new PluginConfiguration { SelectedLibraryId = "shows" });

        var validation = await new PathValidator(new SymlinkManager(), manifestStore)
            .ValidateAsync(source, selection.EffectiveTargetPath, CancellationToken.None);

        Assert.True(selection.IsValid);
        Assert.False(validation.Success);
        Assert.Contains("must not overlap", validation.Error);
        Assert.False(Directory.Exists(target));
        Directory.Delete(root, true);
    }

    [Fact]
    public void NoShowsLibrariesReturnsRequiredEmptyState()
    {
        var result = CreateService().GetState(new PluginConfiguration());

        Assert.False(result.IsValid);
        Assert.Contains("No Jellyfin Shows libraries are configured", result.Error);
    }

    [Fact]
    public void EnumerationFailureLeavesExistingConfigurationIntact()
    {
        var configuration = new PluginConfiguration
        {
            TargetPath = MakePath("old-target"),
            SelectedLibraryId = "old-id",
            SelectedLibraryLocation = MakePath("old-location")
        };
        var before = Clone(configuration);
        var result = new LibrarySelectionService(new ThrowingShowsLibraryProvider()).GetState(configuration);

        Assert.False(result.IsValid);
        Assert.Equal(before.TargetPath, configuration.TargetPath);
        Assert.Equal(before.SelectedLibraryId, configuration.SelectedLibraryId);
        Assert.Equal(before.SelectedLibraryLocation, configuration.SelectedLibraryLocation);
        Assert.Contains("Existing TV Sorter settings were preserved", result.Error);
    }

    [Fact]
    public void SwitchingCanonicalTargetRootsUsesSeparateOwnershipManifests()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var targetA = Directory.CreateDirectory(Path.Combine(root, "target-a")).FullName;
        var targetB = Directory.CreateDirectory(Path.Combine(root, "target-b")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        var store = new ManagedLinkManifestStore(data);
        var oldManifest = new ManagedEntryManifest
        {
            CanonicalTargetPath = PathValidator.CanonicalizeDirectoryPath(targetA),
            Links = new Dictionary<string, ManagedLinkEntry>
            {
                [Path.Combine("Show", "Season 01", "episode.mkv")] = new ManagedLinkEntry
                {
                    DestinationRelativePath = Path.Combine("Show", "Season 01", "episode.mkv"),
                    SourcePath = Path.Combine(root, "source.mkv")
                }
            }
        };
        store.Save(oldManifest);

        var newManifest = store.Load(PathValidator.CanonicalizeDirectoryPath(targetB));

        Assert.NotEqual(store.GetManifestFilePath(targetA), store.GetManifestFilePath(targetB));
        Assert.Empty(newManifest.Manifest.Links);
        Assert.True(File.Exists(store.GetManifestFilePath(targetA)));
        Directory.Delete(root, true);
    }

    private static LibrarySelectionService CreateService(params ShowsLibrary[] libraries)
    {
        return new LibrarySelectionService(new StaticShowsLibraryProvider(libraries));
    }

    private static PluginConfiguration Clone(PluginConfiguration configuration) => new()
    {
        SourcePath = configuration.SourcePath,
        TargetPath = configuration.TargetPath,
        SelectedLibraryId = configuration.SelectedLibraryId,
        SelectedLibraryLocation = configuration.SelectedLibraryLocation,
        RefreshInterval = configuration.RefreshInterval,
        ScanLibraryAfterRefresh = configuration.ScanLibraryAfterRefresh
    };

    private static string MakePath(string name) => Path.Combine(Path.GetTempPath(), "tv-sorter-library-tests", name);

    private static VirtualFolderInfo VirtualFolder(string itemId, string name, CollectionTypeOptions type, string location)
    {
        return new VirtualFolderInfo
        {
            ItemId = itemId,
            Name = name,
            CollectionType = type,
            Locations = [location]
        };
    }

    private sealed class StaticShowsLibraryProvider(IReadOnlyList<ShowsLibrary> libraries) : IShowsLibraryProvider
    {
        public IReadOnlyList<ShowsLibrary> GetShowsLibraries() => libraries;
    }

    private sealed class ThrowingShowsLibraryProvider : IShowsLibraryProvider
    {
        public IReadOnlyList<ShowsLibrary> GetShowsLibraries() => throw new IOException("Library service unavailable.");
    }
}