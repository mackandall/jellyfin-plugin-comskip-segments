using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Plugin.ComskipSegments.Events;
using Jellyfin.Plugin.ComskipSegments.Providers;
using Jellyfin.Plugin.ComskipSegments.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ComskipSegments;

/// <summary>
/// Registers plugin services into Jellyfin's DI container at startup.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<DetectionStore>();
        serviceCollection.AddSingleton<RecordingChannelCache>();
        serviceCollection.AddHostedService<LiveRecordingChannelPoller>();
        serviceCollection.AddSingleton<ComskipRunner>();

        // DetectionManager is both a singleton (so the provider/watcher/task can enqueue
        // into it) AND the hosted service that runs the worker loop. Register the singleton
        // first, then hand the same instance to the hosted-service collection.
        serviceCollection.AddSingleton<DetectionManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<DetectionManager>());

        serviceCollection.AddHostedService<RecordingWatcher>();
        serviceCollection.AddHostedService<OverlayPatchChecker>();

        serviceCollection.AddSingleton<IMediaSegmentProvider, ComskipSegmentProvider>();

        // [ApiController] alone does not make Jellyfin discover a plugin's controllers —
        // the core app builds its MVC ApplicationPartManager once at startup from its own
        // assembly, before any plugin loads, and never re-scans afterwards. A plugin has to
        // add itself explicitly, or its routes are silently absent (confirmed: they never
        // showed up in Jellyfin's own /api-docs/openapi.json route table). Needed for
        // ComskipWebAssetsController.
        serviceCollection.AddControllers().AddApplicationPart(typeof(PluginServiceRegistrator).Assembly);
    }
}
