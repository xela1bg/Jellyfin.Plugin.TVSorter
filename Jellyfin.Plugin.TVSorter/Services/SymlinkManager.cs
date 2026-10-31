namespace Jellyfin.Plugin.TVSorter.Services;

public sealed class SymlinkManager
{
    public string? ReadStoredTarget(string linkPath)
    {
        try
        {
            var target = new FileInfo(linkPath).LinkTarget;
            if (target is not null)
            {
                return target;
            }

            return new DirectoryInfo(linkPath).LinkTarget;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool StoredTargetMatches(string linkPath, string expectedTarget)
    {
        var storedTarget = ReadStoredTarget(linkPath);
        if (storedTarget is null)
        {
            return false;
        }

        var fullStoredTarget = Path.IsPathRooted(storedTarget)
            ? Path.GetFullPath(storedTarget)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, storedTarget));
        var fullExpectedTarget = Path.GetFullPath(expectedTarget);
        return PathEquals(fullStoredTarget, fullExpectedTarget);
    }

    public void Create(string linkPath, string sourcePath)
    {
        File.CreateSymbolicLink(linkPath, Path.GetFullPath(sourcePath));
    }

    public void Replace(string linkPath, string sourcePath)
    {
        var parent = Path.GetDirectoryName(linkPath)
            ?? throw new IOException("The destination link has no parent directory.");
        var stagingPath = Path.Combine(parent, $".tv-sorter-{Guid.NewGuid():N}.link");

        try
        {
            Create(stagingPath, sourcePath);
            if (!StoredTargetMatches(stagingPath, sourcePath))
            {
                throw new IOException("The temporary symbolic link did not retain its expected target.");
            }

            File.Delete(linkPath);
            File.Move(stagingPath, linkPath);
        }
        finally
        {
            if (ReadStoredTarget(stagingPath) is not null)
            {
                File.Delete(stagingPath);
            }
        }
    }

    public void DeleteVerifiedLink(string linkPath, string expectedTarget)
    {
        if (!StoredTargetMatches(linkPath, expectedTarget))
        {
            throw new IOException("The destination symbolic link no longer has its recorded target.");
        }

        File.Delete(linkPath);
    }

    public void ProbeCapability(string targetDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = Path.Combine(targetDirectory, $".tv-sorter-probe-source-{Guid.NewGuid():N}");
        var linkPath = Path.Combine(targetDirectory, $".tv-sorter-probe-link-{Guid.NewGuid():N}");

        try
        {
            using (File.Create(sourcePath))
            {
            }

            Create(linkPath, sourcePath);
            if (!StoredTargetMatches(linkPath, sourcePath))
            {
                throw new IOException("The temporary symbolic link could not be inspected correctly.");
            }
        }
        finally
        {
            try
            {
                if (ReadStoredTarget(linkPath) is not null)
                {
                    File.Delete(linkPath);
                }
            }
            finally
            {
                if (File.Exists(sourcePath))
                {
                    File.Delete(sourcePath);
                }
            }
        }
    }

    public static bool PathEquals(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}