using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ComskipSegments;

/// <summary>
/// Comskip commercial-segment plugin entry point.
///
/// Dashboard plugin-list icon: NOT done via <c>IHasEmbeddedImage</c> — decompiled the
/// actual server code (Jellyfin.Api.Controllers.PluginsController.GetPluginImage,
/// MediaBrowser.Common.Plugins.PluginManifest) and confirmed
/// IHasEmbeddedImage/ImageResourceName is `[JsonIgnore]`d on the manifest — it's never
/// read from meta.json, only settable in code, and only exists for bundled/first-party
/// plugins the server registers itself (AudioDB, TMDb, etc). A side-loaded plugin's icon
/// instead comes from <c>meta.json</c>'s <c>imagePath</c> field, a path to a plain image
/// FILE on disk relative to the plugin's own folder — see meta.json/icon.png.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // This GUID must match pluginUniqueId in Configuration/configPage.html.
    private static readonly Guid PluginId = Guid.Parse("e8f9a7c2-4b3d-4e1a-9c6f-2a1b0c3d4e5f");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Comskip Commercial Segments";

    public override string Description =>
        "Detects commercials in DVR recordings (and optionally existing library files) " +
        "with Comskip and exposes them as native Jellyfin skip segments.";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
