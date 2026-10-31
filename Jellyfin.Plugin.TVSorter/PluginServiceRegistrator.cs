using Jellyfin.Plugin.TVSorter.Configuration;
using Jellyfin.Plugin.TVSorter.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TVSorter;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TvEpisodeParser>();
        serviceCollection.AddSingleton<SymlinkManager>();
        serviceCollection.AddSingleton<PathValidator>();
        serviceCollection.AddSingleton<ManagedLinkManifestStore>();
        serviceCollection.AddSingleton<BuildStatusStore>();
        serviceCollection.AddSingleton<IShowsLibraryProvider, JellyfinShowsLibraryProvider>();
        serviceCollection.AddSingleton<LibrarySelectionService>();
        serviceCollection.AddSingleton<IVirtualTvLibraryBuilder, VirtualTvLibraryBuilder>();
        serviceCollection.AddSingleton<Func<PluginConfiguration?>>(_ => () => Plugin.Instance?.Configuration);
        serviceCollection.AddSingleton<BuildCoordinator>();
    }
}