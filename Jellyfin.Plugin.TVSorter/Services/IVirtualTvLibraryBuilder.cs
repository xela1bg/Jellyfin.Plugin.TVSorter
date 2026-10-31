using Jellyfin.Plugin.TVSorter.Models;

namespace Jellyfin.Plugin.TVSorter.Services;

public interface IVirtualTvLibraryBuilder
{
    Task<BuildResult> SynchronizeAsync(string sourcePath, string targetPath, CancellationToken cancellationToken);
}