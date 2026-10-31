namespace Jellyfin.Plugin.TVSorter.Models;

public enum BuildStatus
{
    Idle,
    Running,
    Success,
    Failed,
    Cancelled,
    AlreadyRunning
}

public sealed class ConflictInfo
{
    public string DestinationPath { get; set; } = string.Empty;

    public List<string> SourcePaths { get; set; } = [];

    public string Reason { get; set; } = string.Empty;
}

public sealed class SkippedItemInfo
{
    public string Path { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}

public sealed class BuildResult
{
    public BuildStatus Status { get; set; }

    public int FilesDiscovered { get; set; }

    public int EpisodesMatched { get; set; }

    public int LinksCreated { get; set; }

    public int LinksAlreadyCorrect { get; set; }

    public int LinksUpdated { get; set; }

    public int StaleLinksRemoved { get; set; }

    public int EmptyDirectoriesRemoved { get; set; }

    public int FilesSkipped { get; set; }

    public List<ConflictInfo> Conflicts { get; set; } = [];

    public List<SkippedItemInfo> SkippedItems { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public List<string> Errors { get; set; } = [];

    public TimeSpan Duration { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public bool LibraryScanQueued { get; set; }

    public string LastError => Errors.LastOrDefault() ?? string.Empty;

    public bool IsSuccessful => Status == BuildStatus.Success;

    public string Summary => Status switch
    {
        BuildStatus.Success => $"Synchronization completed successfully. {FilesDiscovered} files were discovered, " +
            $"{EpisodesMatched} episodes matched, {LinksAlreadyCorrect} links were already correct, " +
            $"{FilesSkipped} files were safely skipped, and {Conflicts.Count} destination conflicts were left unchanged.",
        BuildStatus.Cancelled => $"Synchronization was cancelled after discovering {FilesDiscovered} files.",
        BuildStatus.AlreadyRunning => "A synchronization is already running.",
        BuildStatus.Failed => $"Synchronization failed after discovering {FilesDiscovered} files. " +
            $"{Errors.Count} error(s): {LastError}",
        BuildStatus.Running => $"Synchronization is running. {FilesDiscovered} files discovered so far.",
        _ => "No synchronization has run yet."
    };

    public static BuildStatus ResolveFinalStatus(bool wasCancelled, int errorCount)
    {
        if (wasCancelled)
        {
            return BuildStatus.Cancelled;
        }

        return errorCount > 0 ? BuildStatus.Failed : BuildStatus.Success;
    }

    public void AddSkipped(string path, string category, string reason)
    {
        FilesSkipped++;
        SkippedItems.Add(new SkippedItemInfo { Path = path, Category = category, Reason = reason });
    }

    public static BuildResult InProgress(DateTimeOffset startedAt) => new()
    {
        Status = BuildStatus.Running,
        StartedAt = startedAt
    };
}