namespace Jellyfin.Plugin.TVSorter.Models;

public sealed class ManagedEntryManifest
{
    public int SchemaVersion { get; set; } = 1;

    public string CanonicalTargetPath { get; set; } = string.Empty;

    public Dictionary<string, ManagedLinkEntry> Links { get; set; } = new(StringComparer.Ordinal);

    public HashSet<string> Directories { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ManagedLinkEntry
{
    public string DestinationRelativePath { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string? PendingSourcePath { get; set; }

    public string EntryType { get; set; } = "FileSymbolicLink";

    public IEnumerable<string> AcceptedTargets()
    {
        yield return SourcePath;
        if (!string.IsNullOrWhiteSpace(PendingSourcePath))
        {
            yield return PendingSourcePath;
        }
    }
}