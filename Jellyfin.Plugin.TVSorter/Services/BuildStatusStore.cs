using System.Text.Json;
using Jellyfin.Plugin.TVSorter.Models;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.TVSorter.Services;

public sealed class BuildStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _statusFilePath;

    public BuildStatusStore(IApplicationPaths applicationPaths)
        : this(applicationPaths.DataPath)
    {
    }

    public BuildStatusStore(string dataPath)
    {
        _statusFilePath = Path.Combine(dataPath, "TVSorter", "last-result.json");
    }

    public BuildResult? Load()
    {
        if (!File.Exists(_statusFilePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BuildResult>(File.ReadAllText(_statusFilePath), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public bool Save(BuildResult result, string sourcePath, string targetPath)
    {
        var directory = Path.GetDirectoryName(_statusFilePath)!;
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        var canonicalDirectory = PathValidator.CanonicalizeDirectoryPath(directory);
        var canonicalSource = PathValidator.CanonicalizeDirectoryPath(sourcePath);
        var canonicalTarget = PathValidator.CanonicalizeDirectoryPath(targetPath);
        if (PathValidator.PathsOverlap(canonicalSource, canonicalDirectory)
            || PathValidator.PathsOverlap(canonicalTarget, canonicalDirectory))
        {
            return false;
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = _statusFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(result, JsonOptions));
            File.Move(temporaryPath, _statusFilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return true;
    }
}