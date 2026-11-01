using Jellyfin.Plugin.TVSorter.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TVSorter.Services;

public sealed class VirtualTvLibraryBuilder : IVirtualTvLibraryBuilder
{
    private readonly TvEpisodeParser _parser;
    private readonly PathValidator _pathValidator;
    private readonly SymlinkManager _symlinkManager;
    private readonly ManagedLinkManifestStore _manifestStore;
    private readonly ILogger<VirtualTvLibraryBuilder> _logger;
    private readonly ShowNameCanonicalizer? _canonicalizer;

    public VirtualTvLibraryBuilder(
        TvEpisodeParser parser,
        PathValidator pathValidator,
        SymlinkManager symlinkManager,
        ManagedLinkManifestStore manifestStore,
        ILogger<VirtualTvLibraryBuilder> logger,
        ShowNameCanonicalizer? canonicalizer = null)
    {
        _parser = parser;
        _pathValidator = pathValidator;
        _symlinkManager = symlinkManager;
        _manifestStore = manifestStore;
        _logger = logger;
        _canonicalizer = canonicalizer;
    }

    public async Task<BuildResult> SynchronizeAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var result = BuildResult.InProgress(started);
        _logger.LogInformation("TV Sorter synchronization starting. Source: {SourcePath}; target: {TargetPath}", sourcePath, targetPath);

        var paths = await _pathValidator.ValidateAsync(sourcePath, targetPath, cancellationToken).ConfigureAwait(false);
        if (!paths.Success)
        {
            result.Errors.Add(paths.Error);
            _logger.LogError("TV Sorter path validation failed: {ValidationError}", paths.Error);
            return Complete(result, BuildStatus.Failed, started);
        }

        _logger.LogInformation("TV Sorter paths validated. Source: {SourcePath}; target: {TargetPath}", paths.SourcePath, paths.TargetPath);
        var manifestDirectory = PathValidator.CanonicalizeDirectoryPath(_manifestStore.StorageDirectory);
        if (PathValidator.IsWithinOrEqual(paths.TargetPath, manifestDirectory))
        {
            result.Errors.Add("The configured target contains TV Sorter's ownership manifest location; choose a separate target directory.");
            return Complete(result, BuildStatus.Failed, started);
        }

        var loaded = _manifestStore.Load(paths.TargetPath);
        if (!loaded.IsValid)
        {
            result.Errors.Add(loaded.Error);
            _logger.LogError("TV Sorter ownership manifest validation failed: {ManifestError}", loaded.Error);
            return Complete(result, BuildStatus.Failed, started);
        }

        var manifest = loaded.Manifest;
        var initialLinks = manifest.Links.ToDictionary(
            pair => pair.Key,
            pair => CopyEntry(pair.Value),
            GetPathComparer());
        var initialDirectories = new HashSet<string>(manifest.Directories, GetPathComparer());
        var desired = DiscoverDesired(paths.SourcePath, paths.TargetPath, result, cancellationToken);
        _logger.LogInformation("TV Sorter discovered {FileCount} supported media files.", result.FilesDiscovered);
        if (result.Errors.Count > 0)
        {
            return Complete(result, BuildStatus.Failed, started);
        }

        desired = await CanonicalizeDesiredAsync(desired, cancellationToken).ConfigureAwait(false);
        var desiredPaths = new HashSet<string>(desired.Keys, GetPathComparer());
        var desiredBySource = desired.SelectMany(pair => pair.Value.Select(entry => (Path: pair.Key, Entry: entry)))
            .GroupBy(item => item.Entry.SourcePath, GetPathComparer())
            .ToDictionary(group => group.Key, group => group.ToArray(), GetPathComparer());
        var blockedPaths = new HashSet<string>(GetPathComparer());
        foreach (var pair in desired.Where(pair => pair.Value.Count > 1))
        {
            blockedPaths.Add(pair.Key);
            var destinationPath = Path.GetFullPath(Path.Combine(paths.TargetPath, pair.Key));
            var sources = pair.Value.Select(item => item.SourcePath).ToList();
            AddConflict(result, destinationPath, sources, "Multiple source files map to the same destination.");
            foreach (var source in sources)
            {
                result.AddSkipped(source, "DestinationConflict", "The destination is ambiguous because multiple sources map to it.");
            }
        }

        var touchedManifest = false;
        try
        {
            foreach (var pair in desired.OrderBy(item => item.Key, GetPathComparer()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (blockedPaths.Contains(pair.Key))
                {
                    continue;
                }

                var desiredEntry = pair.Value[0];
                if (!IsContainedDestination(paths.TargetPath, pair.Key, out var destinationPath))
                {
                    result.Errors.Add($"Computed destination escapes the target directory: {pair.Key}");
                    result.FilesSkipped++;
                    continue;
                }

                var parentPath = Path.GetDirectoryName(destinationPath)!;
                if (HasLinkedParent(paths.TargetPath, parentPath))
                {
                    result.AddSkipped(desiredEntry.SourcePath, "UnsafeTargetPath", "A symbolic link in the target path prevents safe synchronization.");
                    AddConflict(result, destinationPath, [desiredEntry.SourcePath], $"A symbolic link in the target path prevents safe synchronization: {parentPath}");
                    continue;
                }

                if (!EnsureDirectories(paths.TargetPath, parentPath, destinationPath, desiredEntry.SourcePath, manifest, result))
                {
                    result.AddSkipped(desiredEntry.SourcePath, "DestinationUnavailable", "A target directory could not be safely created.");
                    continue;
                }

                var hasManifestEntry = manifest.Links.TryGetValue(pair.Key, out var managedEntry);
                var storedTarget = _symlinkManager.ReadStoredTarget(destinationPath);
                var filesystemEntryExists = storedTarget is not null || File.Exists(destinationPath) || Directory.Exists(destinationPath);

                if (filesystemEntryExists && !hasManifestEntry)
                {
                    result.AddSkipped(desiredEntry.SourcePath, "UnownedDestination", "An untracked destination already exists and was left untouched.");
                    AddConflict(result, destinationPath, [desiredEntry.SourcePath], "An unmanaged destination already exists and was left untouched.");
                    continue;
                }

                if (filesystemEntryExists && managedEntry is not null && !IsRecordedTarget(destinationPath, managedEntry))
                {
                    result.AddSkipped(desiredEntry.SourcePath, "ChangedManagedLink", "The managed destination no longer matches its recorded link target.");
                    AddConflict(result, destinationPath, [desiredEntry.SourcePath], "Managed destination was replaced or changed by another process.");
                    manifest.Links.Remove(pair.Key);
                    touchedManifest = true;
                    continue;
                }

                if (storedTarget is not null && managedEntry is not null)
                {
                    if (_symlinkManager.StoredTargetMatches(destinationPath, desiredEntry.SourcePath))
                    {
                        managedEntry.SourcePath = desiredEntry.SourcePath;
                        managedEntry.PendingSourcePath = null;
                        result.LinksAlreadyCorrect++;
                        touchedManifest = true;
                        continue;
                    }

                    managedEntry.PendingSourcePath = desiredEntry.SourcePath;
                    _manifestStore.Save(manifest);
                    touchedManifest = false;
                    _symlinkManager.Replace(destinationPath, desiredEntry.SourcePath);
                    managedEntry.SourcePath = desiredEntry.SourcePath;
                    managedEntry.PendingSourcePath = null;
                    result.LinksUpdated++;
                    touchedManifest = true;
                    continue;
                }

                if (!filesystemEntryExists)
                {
                    if (!File.Exists(desiredEntry.SourcePath))
                    {
                        result.AddSkipped(desiredEntry.SourcePath, "SourceUnavailable", "Source disappeared before link creation.");
                        result.Warnings.Add($"Source disappeared before link creation: {desiredEntry.SourcePath}");
                        continue;
                    }

                    var createdEntry = managedEntry ?? new ManagedLinkEntry
                    {
                        DestinationRelativePath = pair.Key,
                        SourcePath = desiredEntry.SourcePath
                    };
                    createdEntry.SourcePath = desiredEntry.SourcePath;
                    createdEntry.PendingSourcePath = null;
                    manifest.Links[pair.Key] = createdEntry;
                    _manifestStore.Save(manifest);
                    touchedManifest = false;

                    if (File.Exists(destinationPath) || Directory.Exists(destinationPath) || _symlinkManager.ReadStoredTarget(destinationPath) is not null)
                    {
                        result.AddSkipped(desiredEntry.SourcePath, "DestinationChanged", "Destination appeared during synchronization and was left untouched.");
                        AddConflict(result, destinationPath, [desiredEntry.SourcePath], "Destination appeared during synchronization and was left untouched.");
                        if (!hasManifestEntry)
                        {
                            manifest.Links.Remove(pair.Key);
                        }

                        touchedManifest = true;
                        continue;
                    }

                    _symlinkManager.Create(destinationPath, desiredEntry.SourcePath);
                    result.LinksCreated++;
                    touchedManifest = true;
                }
            }

            foreach (var pair in manifest.Links.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (desiredPaths.Contains(pair.Key))
                {
                    continue;
                }

                if (!IsContainedDestination(paths.TargetPath, pair.Key, out var destinationPath))
                {
                    result.Errors.Add($"Manifest destination escapes the target directory: {pair.Key}");
                    continue;
                }

                var destinationParent = Path.GetDirectoryName(destinationPath)!;
                if (HasLinkedParent(paths.TargetPath, destinationParent))
                {
                    AddConflict(result, destinationPath, pair.Value.AcceptedTargets().ToList(), "A symbolic link in the target path prevents safe stale-link cleanup.");
                    continue;
                }

                var currentTarget = _symlinkManager.ReadStoredTarget(destinationPath);
                if (currentTarget is null && !File.Exists(destinationPath) && !Directory.Exists(destinationPath))
                {
                    manifest.Links.Remove(pair.Key);
                    touchedManifest = true;
                    continue;
                }

                if (!IsRecordedTarget(destinationPath, pair.Value))
                {
                    AddConflict(result, destinationPath, pair.Value.AcceptedTargets().ToList(), "Ownership could not be verified; destination was left untouched.");
                    manifest.Links.Remove(pair.Key);
                    touchedManifest = true;
                    continue;
                }

                var replacements = pair.Value.AcceptedTargets()
                    .Where(desiredBySource.ContainsKey).SelectMany(source => desiredBySource[source]).ToArray();
                if (replacements.Any(replacement => blockedPaths.Contains(replacement.Path)
                    || !manifest.Links.TryGetValue(replacement.Path, out var replacementManifest)
                    || !IsContainedDestination(paths.TargetPath, replacement.Path, out var replacementPath)
                    || HasLinkedParent(paths.TargetPath, Path.GetDirectoryName(replacementPath)!)
                    || !IsRecordedTarget(replacementPath, replacementManifest)
                    || !_symlinkManager.StoredTargetMatches(replacementPath, replacement.Entry.SourcePath)))
                {
                    result.Warnings.Add($"Retained managed link until its replacement is successfully reconciled: {destinationPath}");
                    continue;
                }

                _symlinkManager.DeleteVerifiedLink(destinationPath, currentTarget!);
                manifest.Links.Remove(pair.Key);
                result.StaleLinksRemoved++;
                touchedManifest = true;
            }

            RemoveEmptyManagedDirectories(paths.TargetPath, manifest, result, cancellationToken);
            touchedManifest = true;
        }
        catch (OperationCanceledException)
        {
            result.Status = BuildStatus.Cancelled;
            result.Warnings.Add("Synchronization was cancelled; completed filesystem changes were retained.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            result.Errors.Add(exception.Message);
            _logger.LogError(exception, "TV Sorter synchronization encountered a filesystem error.");
        }
        finally
        {
            if (touchedManifest || !ManifestEquals(initialLinks, manifest.Links) || !initialDirectories.SetEquals(manifest.Directories))
            {
                try
                {
                    _manifestStore.Save(manifest);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    result.Errors.Add($"Ownership manifest could not be reconciled: {exception.Message}");
                }
            }
        }

        var status = BuildResult.ResolveFinalStatus(result.Status == BuildStatus.Cancelled, result.Errors.Count);
        return Complete(result, status, started);
    }

    private Dictionary<string, List<DesiredEntry>> DiscoverDesired(
        string sourcePath,
        string targetPath,
        BuildResult result,
        CancellationToken cancellationToken)
    {
        var desired = new Dictionary<string, List<DesiredEntry>>(GetPathComparer());
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(sourcePath);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            string[] childDirectories;
            string[] files;

            try
            {
                childDirectories = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"Could not completely enumerate source directory '{directory}': {exception.Message}");
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(childDirectory);
                if (_parser.ShouldSkipDirectory(name))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                    {
                        pendingDirectories.Push(childDirectory);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    result.Errors.Add($"Could not safely inspect source directory '{childDirectory}': {exception.Message}");
                }
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                if (!_parser.IsVideo(fileName))
                {
                    continue;
                }

                result.FilesDiscovered++;
                if (!File.Exists(file))
                {
                    result.AddSkipped(file, "SourceUnavailable", "Supported media file changed or became inaccessible during discovery.");
                    result.Errors.Add($"Supported media file changed or became inaccessible during discovery: {file}");
                    continue;
                }

                var relative = Path.GetRelativePath(sourcePath, file);
                var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                string? seriesHint = null;
                if (parts.Length > 0 && _parser.ParseSeasonEpisode(parts[0]) is null && !_parser.ShouldSkipDirectory(parts[0]))
                {
                    seriesHint = _parser.CleanShowName(parts[0]);
                }

                var parent = Path.GetDirectoryName(file);
                var parentName = parent is null ? string.Empty : Path.GetFileName(parent);
                var grandparentName = parent is null ? null : Directory.GetParent(parent)?.Name;
                var parsed = _parser.Parse(fileName, parentName, grandparentName, seriesHint);
                if (parsed.IsEpisodeMatched)
                {
                    result.EpisodesMatched++;
                }

                var relativeDestination = Path.Combine(parsed.ShowName, parsed.SeasonDirectory, parsed.GetFileName(fileName));
                if (!IsContainedDestination(targetPath, relativeDestination, out _))
                {
                    result.AddSkipped(file, "UnsafeDestination", "Parsed destination is not a safe relative path.");
                    result.Errors.Add($"Parsed destination is not a safe relative path: {relativeDestination}");
                    continue;
                }

                if (!desired.TryGetValue(relativeDestination, out var entries))
                {
                    entries = [];
                    desired.Add(relativeDestination, entries);
                }

                entries.Add(new DesiredEntry(Path.GetFullPath(file), relativeDestination, parsed));
                _logger.LogDebug("Parsed {SourcePath} to {DestinationPath} ({Season}, {Episode}).", file, relativeDestination, parsed.Season, parsed.Episode);
            }
        }

        return desired;
    }

    private async Task<Dictionary<string, List<DesiredEntry>>> CanonicalizeDesiredAsync(
        Dictionary<string, List<DesiredEntry>> discovered,
        CancellationToken cancellationToken)
    {
        var entries = discovered.Values.SelectMany(items => items)
            .Select(entry => (Entry: entry, Candidate: ShowNameNormalizer.Normalize(entry.Episode.ShowName))).ToArray();
        var candidates = entries.Select(item => item.Candidate).ToArray();
        var names = _canonicalizer is null
            ? candidates.GroupBy(candidate => candidate.Identity).ToDictionary(group => group.Key, group => group.First().Name)
            : await _canonicalizer.ResolveAsync(candidates, cancellationToken).ConfigureAwait(false);
        var desired = new Dictionary<string, List<DesiredEntry>>(GetPathComparer());
        foreach (var item in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episode = item.Entry.Episode with { ShowName = names[item.Candidate.Identity] };
            var relativePath = Path.Combine(episode.ShowName, episode.SeasonDirectory, episode.GetFileName(Path.GetFileName(item.Entry.SourcePath)));
            if (!desired.TryGetValue(relativePath, out var destinations))
            {
                destinations = [];
                desired.Add(relativePath, destinations);
            }

            destinations.Add(item.Entry with { RelativePath = relativePath, Episode = episode });
        }

        return desired;
    }

    private bool EnsureDirectories(
        string targetRoot,
        string destinationParent,
        string destinationPath,
        string sourcePath,
        ManagedEntryManifest manifest,
        BuildResult result)
    {
        var relativeParent = Path.GetRelativePath(targetRoot, destinationParent);
        var segments = relativeParent.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = targetRoot;

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (new DirectoryInfo(current).LinkTarget is not null)
            {
                AddConflict(result, destinationPath, [sourcePath], $"A symbolic link blocks the target directory: {current}");
                return false;
            }

            if (Directory.Exists(current))
            {
                continue;
            }

            if (File.Exists(current) || _symlinkManager.ReadStoredTarget(current) is not null)
            {
                AddConflict(result, destinationPath, [sourcePath], $"A non-directory entry blocks the managed directory: {current}");
                return false;
            }

            try
            {
                Directory.CreateDirectory(current);
                manifest.Directories.Add(Path.GetRelativePath(targetRoot, current));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"Could not create target directory '{current}': {exception.Message}");
                return false;
            }
        }

        return true;
    }

    private void RemoveEmptyManagedDirectories(
        string targetRoot,
        ManagedEntryManifest manifest,
        BuildResult result,
        CancellationToken cancellationToken)
    {
        foreach (var relativeDirectory in manifest.Directories.OrderByDescending(path => path.Length).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsContainedDestination(targetRoot, relativeDirectory, out var directoryPath))
            {
                result.Errors.Add($"Manifest directory escapes the target directory: {relativeDirectory}");
                continue;
            }

            if (!Directory.Exists(directoryPath))
            {
                manifest.Directories.Remove(relativeDirectory);
                continue;
            }

            if (new DirectoryInfo(directoryPath).LinkTarget is not null)
            {
                AddConflict(result, directoryPath, [], "Managed directory was replaced by a symbolic link and was left untouched.");
                manifest.Directories.Remove(relativeDirectory);
                continue;
            }

            var parentPath = Path.GetDirectoryName(directoryPath);
            if (parentPath is null || HasLinkedParent(targetRoot, parentPath))
            {
                AddConflict(result, directoryPath, [], "A symbolic link in the target path prevents safe cleanup.");
                manifest.Directories.Remove(relativeDirectory);
                continue;
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
                {
                    Directory.Delete(directoryPath, false);
                    manifest.Directories.Remove(relativeDirectory);
                    result.EmptyDirectoriesRemoved++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"Could not inspect or remove empty managed directory '{directoryPath}': {exception.Message}");
            }
        }
    }

    private bool IsRecordedTarget(string destinationPath, ManagedLinkEntry entry)
    {
        return entry.AcceptedTargets().Any(target => _symlinkManager.StoredTargetMatches(destinationPath, target));
    }

    private static void AddConflict(BuildResult result, string destinationPath, List<string> sourcePaths, string reason)
    {
        result.Conflicts.Add(new ConflictInfo
        {
            DestinationPath = destinationPath,
            SourcePaths = sourcePaths,
            Reason = reason
        });
    }

    private static bool HasLinkedParent(string targetRoot, string parentPath)
    {
        var current = targetRoot;
        if (!PathValidator.IsWithinOrEqual(targetRoot, parentPath))
        {
            return true;
        }

        var relative = Path.GetRelativePath(targetRoot, parentPath);
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new DirectoryInfo(current).LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsContainedDestination(string targetRoot, string relativePath, out string destinationPath)
    {
        destinationPath = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
        return !Path.IsPathRooted(relativePath) && PathValidator.IsWithinOrEqual(targetRoot, destinationPath)
            && !SymlinkManager.PathEquals(targetRoot, destinationPath);
    }

    private static BuildResult Complete(BuildResult result, BuildStatus status, DateTimeOffset started)
    {
        result.Status = status;
        result.CompletedAt = DateTimeOffset.UtcNow;
        result.Duration = result.CompletedAt.Value - started;
        return result;
    }

    private static ManagedLinkEntry CopyEntry(ManagedLinkEntry entry) => new()
    {
        DestinationRelativePath = entry.DestinationRelativePath,
        SourcePath = entry.SourcePath,
        PendingSourcePath = entry.PendingSourcePath,
        EntryType = entry.EntryType
    };

    private static bool ManifestEquals(
        IReadOnlyDictionary<string, ManagedLinkEntry> before,
        IReadOnlyDictionary<string, ManagedLinkEntry> after)
    {
        return before.Count == after.Count && before.All(pair =>
            after.TryGetValue(pair.Key, out var current)
            && pair.Value.SourcePath == current.SourcePath
            && pair.Value.PendingSourcePath == current.PendingSourcePath);
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record DesiredEntry(string SourcePath, string RelativePath, EpisodeInfo Episode);
}