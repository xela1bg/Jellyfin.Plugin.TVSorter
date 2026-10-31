using Jellyfin.Plugin.TVSorter.Models;

namespace Jellyfin.Plugin.TVSorter.Services;

public interface IShowsLibraryProvider
{
    IReadOnlyList<ShowsLibrary> GetShowsLibraries();
}