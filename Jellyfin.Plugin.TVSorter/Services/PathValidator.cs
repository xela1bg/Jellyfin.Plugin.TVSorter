namespace Jellyfin.Plugin.TVSorter.Services;

public sealed class PathValidationResult
{
    public bool Success { get; init; }

    public bool SourceAccessible { get; init; }

    public bool TargetWritable { get; init; }

    public bool SymlinkSupported { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;
}

public sealed class PathValidator
{
    private readonly SymlinkManager _symlinkManager;
    private readonly ManagedLinkManifestStore _manifestStore;

    public PathValidator(SymlinkManager symlinkManager, ManagedLinkManifestStore manifestStore)
    {
        _symlinkManager = symlinkManager;
        _manifestStore = manifestStore;
    }

    public Task<PathValidationResult> ValidateAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var sourceAccessible = false;
        var targetWritable = false;
        var symlinkSupported = false;

        PathValidationResult FailureResult(string error) => new()
        {
            Success = false,
            SourceAccessible = sourceAccessible,
            TargetWritable = targetWritable,
            SymlinkSupported = symlinkSupported,
            Error = error
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                return Task.FromResult(FailureResult("An unsorted media directory and a selected Jellyfin library location are required."));
            }

            string source;
            try
            {
                source = CanonicalizeDirectoryPath(sourcePath);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                return Task.FromResult(FailureResult($"Unsorted media directory path is invalid: {exception.Message}"));
            }

            string target;
            try
            {
                target = CanonicalizeDirectoryPath(targetPath);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                return Task.FromResult(FailureResult($"Selected Jellyfin library location is invalid: {exception.Message}"));
            }

            if (PathEqualsRoot(target))
            {
                return Task.FromResult(FailureResult("The filesystem root cannot be used as the TV Sorter target."));
            }

            if (!Directory.Exists(source))
            {
                return Task.FromResult(FailureResult($"Unsorted media directory does not exist: {source}"));
            }

            if (IsWithinOrEqual(source, target) || IsWithinOrEqual(target, source))
            {
                return Task.FromResult(FailureResult("Unsorted media directory and selected Jellyfin library location must not overlap."));
            }

            var manifestDirectory = CanonicalizeDirectoryPath(_manifestStore.StorageDirectory);
            if (PathsOverlap(source, manifestDirectory) || PathsOverlap(target, manifestDirectory))
            {
                return Task.FromResult(FailureResult("Unsorted media directory and selected Jellyfin library location must not overlap TV Sorter's private data directory."));
            }

            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(source).GetEnumerator();
                cancellationToken.ThrowIfCancellationRequested();
                _ = entries.MoveNext();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(FailureResult($"Unsorted media directory is not readable by the Jellyfin server: {exception.Message}"));
            }

            sourceAccessible = true;

            Directory.CreateDirectory(target);
            target = CanonicalizeDirectoryPath(target);

            if (IsWithinOrEqual(source, target) || IsWithinOrEqual(target, source))
            {
                return Task.FromResult(FailureResult("Canonical unsorted media directory and selected Jellyfin library location must not overlap."));
            }

            if (PathsOverlap(source, manifestDirectory) || PathsOverlap(target, manifestDirectory))
            {
                return Task.FromResult(FailureResult("Canonical source and selected Jellyfin library location must not overlap TV Sorter's private data directory."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            ProbeTargetWritable(target);
            targetWritable = true;
            try
            {
                _symlinkManager.ProbeCapability(target, cancellationToken);
                symlinkSupported = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return Task.FromResult(FailureResult($"Selected Jellyfin library location does not support symbolic links: {exception.Message}"));
            }

            return Task.FromResult(new PathValidationResult
            {
                Success = true,
                SourceAccessible = sourceAccessible,
                TargetWritable = targetWritable,
                SymlinkSupported = symlinkSupported,
                SourcePath = source,
                TargetPath = target
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(FailureResult($"Selected Jellyfin library location could not be validated: {exception.Message}"));
        }
    }

    private static void ProbeTargetWritable(string targetPath)
    {
        var probePath = Path.Combine(targetPath, $".tv-sorter-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    public static string CanonicalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("Path has no filesystem root.", nameof(path));
        var remainder = fullPath[root.Length..];
        var segments = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                continue;
            }

            var resolved = new DirectoryInfo(current).ResolveLinkTarget(true);
            if (resolved is not null)
            {
                current = Path.GetFullPath(resolved.FullName);
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    public static bool IsWithinOrEqual(string parentPath, string candidatePath)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        if (SymlinkManager.PathEquals(parent, candidate))
        {
            return true;
        }

        var relative = Path.GetRelativePath(parent, candidate);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static bool PathsOverlap(string firstPath, string secondPath)
    {
        return IsWithinOrEqual(firstPath, secondPath) || IsWithinOrEqual(secondPath, firstPath);
    }

    private static bool PathEqualsRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is not null && SymlinkManager.PathEquals(path, root);
    }
}