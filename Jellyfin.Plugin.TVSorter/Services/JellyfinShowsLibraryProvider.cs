using Jellyfin.Plugin.TVSorter.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.TVSorter.Services;

public sealed class JellyfinShowsLibraryProvider : IShowsLibraryProvider
{
    private readonly ILibraryManager _libraryManager;

    public JellyfinShowsLibraryProvider(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public IReadOnlyList<ShowsLibrary> GetShowsLibraries()
    {
        return _libraryManager.GetVirtualFolders()
            .Where(folder => folder.CollectionType == CollectionTypeOptions.tvshows)
            .Where(folder => !string.IsNullOrWhiteSpace(folder.ItemId))
            .Select(folder => new ShowsLibrary(
                folder.ItemId,
                folder.Name ?? string.Empty,
                (folder.Locations ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray()))
            .ToArray();
    }
}