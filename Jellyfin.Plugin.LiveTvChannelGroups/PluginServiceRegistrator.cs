using Jellyfin.Plugin.LiveTvChannelGroups.Engine;
using Jellyfin.Plugin.LiveTvChannelGroups.HostedServices;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LiveTvChannelGroups;

/// <summary>
/// Registers this plugin's own services with the host's dependency injection
/// container. Required for the API controller and the scheduled task to be
/// able to take <see cref="ChannelGroupEngine"/> as a constructor dependency,
/// and for the hosted service to be started at all.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ChannelGroupEngine>();
        serviceCollection.AddHostedService<ChannelGroupHostedService>();
    }
}
