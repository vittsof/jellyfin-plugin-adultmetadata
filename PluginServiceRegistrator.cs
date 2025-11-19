using System.Collections.Generic;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.AdultMetadata.Movies;

namespace Jellyfin.Plugin.AdultMetadata
{
    /// <summary>
    /// Plugin service registrator for AdultMetadata.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Register the metadata provider
            serviceCollection.AddScoped<IRemoteMetadataProvider<Movie, MovieInfo>, AdultMovieProvider>();
            serviceCollection.AddScoped<IMetadataProvider<Movie>, AdultMovieProvider>();
        }
    }
}