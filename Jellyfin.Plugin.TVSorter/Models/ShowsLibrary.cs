using Jellyfin.Plugin.TVSorter.Configuration;

namespace Jellyfin.Plugin.TVSorter.Models;

public sealed record ShowsLibrary(string Id, string Name, IReadOnlyList<string> Locations);

public sealed class ShowsLibraryOption
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> Locations { get; set; } = [];
}

public sealed class LibrarySelectionResult
{
    public List<ShowsLibraryOption> Libraries { get; set; } = [];

    public string? SelectedLibraryId { get; set; }

    public string? SelectedLibraryName { get; set; }

    public string? SelectedLibraryLocation { get; set; }

    public string EffectiveTargetPath { get; set; } = string.Empty;

    public bool IsValid { get; set; }

    public bool NeedsMigration { get; set; }

    public bool PreserveExistingTargetPath { get; set; }

    public string Warning { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
}

public sealed class SaveConfigurationRequest
{
    public string SourcePath { get; set; } = string.Empty;

    public string? SelectedLibraryId { get; set; }

    public string? SelectedLibraryLocation { get; set; }

    public RefreshInterval RefreshInterval { get; set; }

    public bool ScanLibraryAfterRefresh { get; set; }
}

public sealed class TestPathsResult
{
    public bool Success { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string SourceStatus { get; set; } = string.Empty;

    public string LibraryName { get; set; } = string.Empty;

    public string LibraryPath { get; set; } = string.Empty;

    public string TargetStatus { get; set; } = string.Empty;

    public string SymlinkStatus { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
}