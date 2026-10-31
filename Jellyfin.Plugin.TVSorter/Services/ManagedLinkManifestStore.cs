using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.TVSorter.Models;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.TVSorter.Services;

public sealed record ManifestLoadResult(ManagedEntryManifest Manifest, bool IsValid, bool WasPresent, string Error);

public sealed class ManagedLinkManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SymlinkManager LinkInspector = new();
    private readonly string _storageDirectory;

    public ManagedLinkManifestStore(IApplicationPaths applicationPaths)
        : this(applicationPaths.DataPath)
    {
    }

    public ManagedLinkManifestStore(string dataPath)
    {
        _storageDirectory = Path.Combine(dataPath, "TVSorter");
    }

    public string GetManifestFilePath(string canonicalTargetPath) => GetManifestPath(canonicalTargetPath);

    public string StorageDirectory => _storageDirectory;

    public ManifestLoadResult Load(string canonicalTargetPath)
    {
        var manifest = new ManagedEntryManifest
        {
            CanonicalTargetPath = canonicalTargetPath,
            Links = new Dictionary<string, ManagedLinkEntry>(PathComparer),
            Directories = new HashSet<string>(PathComparer)
        };
        var path = GetManifestPath(canonicalTargetPath);
        if (LinkInspector.ReadStoredTarget(path) is not null)
        {
            return new ManifestLoadResult(manifest, false, true, "Ownership manifest path is a symbolic link and cannot establish safe ownership.");
        }

        if (!File.Exists(path))
        {
            return new ManifestLoadResult(manifest, true, false, string.Empty);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<ManagedEntryManifest>(File.ReadAllText(path), JsonOptions);
            if (loaded is null || loaded.SchemaVersion != 1 || !SymlinkManager.PathEquals(loaded.CanonicalTargetPath, canonicalTargetPath))
            {
                return new ManifestLoadResult(manifest, false, true, "Ownership manifest schema or target path is invalid.");
            }

            if (loaded.Links is null || loaded.Directories is null)
            {
                return new ManifestLoadResult(manifest, false, true, "Ownership manifest is missing required entry collections.");
            }

            var links = new Dictionary<string, ManagedLinkEntry>(PathComparer);
            foreach (var pair in loaded.Links)
            {
                if (!IsSafeRelativePath(pair.Key)
                    || pair.Value is null
                    || !PathComparer.Equals(pair.Key, pair.Value.DestinationRelativePath)
                    || !Path.IsPathRooted(pair.Value.SourcePath)
                    || (pair.Value.PendingSourcePath is not null && !Path.IsPathRooted(pair.Value.PendingSourcePath))
                    || !string.Equals(pair.Value.EntryType, "FileSymbolicLink", StringComparison.Ordinal))
                {
                    return new ManifestLoadResult(manifest, false, true, "Ownership manifest contains an unsafe or inconsistent link entry.");
                }

                links.Add(pair.Key, pair.Value);
            }

            var directories = new HashSet<string>(PathComparer);
            foreach (var directory in loaded.Directories)
            {
                if (!IsSafeRelativePath(directory))
                {
                    return new ManifestLoadResult(manifest, false, true, "Ownership manifest contains an unsafe directory entry.");
                }

                directories.Add(directory);
            }

            loaded.Links = links;
            loaded.Directories = directories;
            return new ManifestLoadResult(loaded, true, true, string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new ManifestLoadResult(manifest, false, true, $"Ownership manifest could not be read: {exception.Message}");
        }
    }

    public void Save(ManagedEntryManifest manifest)
    {
        Directory.CreateDirectory(_storageDirectory);
        var path = GetManifestPath(manifest.CanonicalTargetPath);
        if (LinkInspector.ReadStoredTarget(path) is not null)
        {
            throw new IOException("Ownership manifest path was replaced by a symbolic link.");
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetManifestPath(string targetPath)
    {
        var identity = OperatingSystem.IsWindows() ? targetPath.ToUpperInvariant() : targetPath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_storageDirectory, $"managed-{hash}.json");
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment is not "." and not "..");
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}