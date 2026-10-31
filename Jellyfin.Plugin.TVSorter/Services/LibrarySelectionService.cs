using Jellyfin.Plugin.TVSorter.Configuration;
using Jellyfin.Plugin.TVSorter.Models;

namespace Jellyfin.Plugin.TVSorter.Services;

public sealed class LibrarySelectionService
{
    private readonly IShowsLibraryProvider _libraryProvider;

    public LibrarySelectionService(IShowsLibraryProvider libraryProvider)
    {
        _libraryProvider = libraryProvider;
    }

    public LibrarySelectionResult GetState(PluginConfiguration configuration)
    {
        IReadOnlyList<ShowsLibrary> libraries;
        try
        {
            libraries = NormalizeLibraries(_libraryProvider.GetShowsLibraries());
        }
        catch (Exception exception)
        {
            return new LibrarySelectionResult
            {
                SelectedLibraryId = configuration.SelectedLibraryId,
                SelectedLibraryLocation = configuration.SelectedLibraryLocation,
                EffectiveTargetPath = configuration.TargetPath,
                Error = $"Could not retrieve Jellyfin libraries. Existing TV Sorter settings were preserved. {exception.Message}"
            };
        }

        var result = new LibrarySelectionResult
        {
            Libraries = ToOptions(libraries),
            SelectedLibraryId = configuration.SelectedLibraryId,
            SelectedLibraryLocation = configuration.SelectedLibraryLocation,
            EffectiveTargetPath = configuration.TargetPath
        };

        if (libraries.Count == 0)
        {
            result.Error = "No Jellyfin Shows libraries are configured. Create a Shows library in Jellyfin before configuring the TV Sorter target.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(configuration.SelectedLibraryId))
        {
            return ResolveLegacyTarget(configuration, libraries, result);
        }

        var selectedLibrary = libraries.FirstOrDefault(library => IdEquals(library.Id, configuration.SelectedLibraryId));
        if (selectedLibrary is null)
        {
            result.Error = "The previously selected Jellyfin library is no longer available. Select a new TV Sorter library.";
            return result;
        }

        result.SelectedLibraryId = selectedLibrary.Id;
        result.SelectedLibraryName = selectedLibrary.Name;
        if (selectedLibrary.Locations.Count == 0)
        {
            result.Error = "The selected Jellyfin Shows library has no usable filesystem location.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(configuration.SelectedLibraryLocation))
        {
            if (selectedLibrary.Locations.Count != 1)
            {
                result.Error = "Select a library location; this Jellyfin Shows library has multiple locations.";
                return result;
            }

            result.SelectedLibraryLocation = selectedLibrary.Locations[0];
            result.EffectiveTargetPath = selectedLibrary.Locations[0];
            result.IsValid = true;
            result.NeedsMigration = true;
            return result;
        }

        var selectedLocation = selectedLibrary.Locations.FirstOrDefault(path => PathsEqual(path, configuration.SelectedLibraryLocation));
        if (selectedLocation is null)
        {
            result.Error = "The previously selected library location is no longer available. Select an available location.";
            return result;
        }

        result.SelectedLibraryLocation = selectedLocation;
        result.EffectiveTargetPath = selectedLocation;
        result.IsValid = true;
        return result;
    }

    public bool TryApplySelection(
        PluginConfiguration configuration,
        PluginConfiguration? previousConfiguration,
        out LibrarySelectionResult result)
    {
        result = GetState(configuration);
        if (!result.IsValid || string.IsNullOrWhiteSpace(result.SelectedLibraryId) || string.IsNullOrWhiteSpace(result.SelectedLibraryLocation))
        {
            return false;
        }

        var legacyMigration = previousConfiguration is not null
            && string.IsNullOrWhiteSpace(previousConfiguration.SelectedLibraryId)
            && !string.IsNullOrWhiteSpace(previousConfiguration.TargetPath)
            && PathsEqual(previousConfiguration.TargetPath, result.EffectiveTargetPath);
        var previousTarget = configuration.TargetPath;
        configuration.SelectedLibraryId = result.SelectedLibraryId;
        configuration.SelectedLibraryLocation = result.SelectedLibraryLocation;
        if (!legacyMigration)
        {
            configuration.TargetPath = result.EffectiveTargetPath;
        }
        else
        {
            configuration.TargetPath = previousConfiguration!.TargetPath;
            result.PreserveExistingTargetPath = true;
        }

        if (!string.IsNullOrWhiteSpace(previousTarget)
            && !PathsEqual(previousTarget, result.EffectiveTargetPath)
            && !legacyMigration)
        {
            result.Warning = "The previous target will be left untouched. TV Sorter manages ownership separately for each selected library location.";
        }

        return true;
    }

    private static LibrarySelectionResult ResolveLegacyTarget(
        PluginConfiguration configuration,
        IReadOnlyList<ShowsLibrary> libraries,
        LibrarySelectionResult result)
    {
        if (string.IsNullOrWhiteSpace(configuration.TargetPath))
        {
            result.Error = "Select a Jellyfin Shows library before configuring the TV Sorter target.";
            return result;
        }

        var matches = libraries
            .SelectMany(library => library.Locations.Select(location => new { Library = library, Location = location }))
            .Where(candidate => PathsEqual(candidate.Location, configuration.TargetPath))
            .ToArray();

        if (matches.Length == 1)
        {
            result.SelectedLibraryId = matches[0].Library.Id;
            result.SelectedLibraryName = matches[0].Library.Name;
            result.SelectedLibraryLocation = matches[0].Location;
            result.EffectiveTargetPath = matches[0].Location;
            result.IsValid = true;
            result.NeedsMigration = true;
            result.PreserveExistingTargetPath = true;
            return result;
        }

        result.Error = matches.Length > 1
            ? "The existing TV Sorter target matches multiple Jellyfin Shows library locations. Select the intended library and location explicitly."
            : "The existing TV Sorter target is not associated with an available Jellyfin Shows library. Select the Jellyfin library that TV Sorter should manage.";
        return result;
    }

    private static IReadOnlyList<ShowsLibrary> NormalizeLibraries(IReadOnlyList<ShowsLibrary> libraries)
    {
        var normalized = new List<ShowsLibrary>(libraries.Count);
        foreach (var library in libraries)
        {
            var locations = new List<string>();
            foreach (var location in library.Locations)
            {
                if (!TryCanonicalize(location, out var canonical))
                {
                    continue;
                }

                if (!locations.Any(existing => PathsEqual(existing, canonical)))
                {
                    locations.Add(canonical);
                }
            }

            normalized.Add(library with { Locations = locations });
        }

        return normalized;
    }

    private static List<ShowsLibraryOption> ToOptions(IReadOnlyList<ShowsLibrary> libraries)
    {
        return libraries.Select(library => new ShowsLibraryOption
        {
            Id = library.Id,
            Name = library.Name,
            Locations = library.Locations.ToList()
        }).ToList();
    }

    private static bool TryCanonicalize(string path, out string canonicalPath)
    {
        try
        {
            if (!Path.IsPathRooted(path))
            {
                canonicalPath = string.Empty;
                return false;
            }

            canonicalPath = PathValidator.CanonicalizeDirectoryPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            canonicalPath = string.Empty;
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return TryCanonicalize(left, out var leftCanonical)
            && TryCanonicalize(right, out var rightCanonical)
            && SymlinkManager.PathEquals(leftCanonical, rightCanonical);
    }

    private static bool IdEquals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}