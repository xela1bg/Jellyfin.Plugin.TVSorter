using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TVSorter.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string SourcePath { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string? SelectedLibraryId { get; set; }

    public string? SelectedLibraryLocation { get; set; }

    public RefreshInterval RefreshInterval { get; set; } = Jellyfin.Plugin.TVSorter.Configuration.RefreshInterval.Manual;

    public bool ScanLibraryAfterRefresh { get; set; }

    public bool UseTvmaze { get; set; } = true;
}

public enum RefreshInterval
{
    Manual,
    Every30Minutes,
    EveryHour,
    Every6Hours,
    Daily
}