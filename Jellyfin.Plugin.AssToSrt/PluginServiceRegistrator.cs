using Jellyfin.Plugin.AssToSrt.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AssToSrt;

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<AssToSrtConverter>();

        // One instance: background service and queue used by the settings page.
        serviceCollection.AddSingleton<AutoConvertService>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<AutoConvertService>());
    }
}
