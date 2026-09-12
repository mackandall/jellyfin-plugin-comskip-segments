using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// On startup, checks whether jellyfin-web's index.html still has each of this plugin's
/// web patches applied by Web/install-overlay.sh, and logs a clear warning per patch
/// that's missing — most commonly because a jellyfin-web package upgrade overwrote
/// index.html back to stock. The plugin can't reapply the patch itself (index.html is
/// root-owned; this service runs as the `jellyfin` account), so this is
/// detection-and-instructions only.
/// </summary>
public sealed class OverlayPatchChecker : IHostedService
{
    private static readonly (string Marker, string Description)[] Patches =
    {
        ("<!-- ComskipSegments:overlay:begin -->", "commercial-marker seek-bar overlay"),
        ("<!-- ComskipSegments:itemmenu:begin -->", "\"Scan for commercials\" item-details menu entry")
    };

    private readonly ILogger<OverlayPatchChecker> _logger;
    private readonly IApplicationPaths _applicationPaths;

    public OverlayPatchChecker(ILogger<OverlayPatchChecker> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _applicationPaths = applicationPaths;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = Path.Combine(_applicationPaths.WebPath, "index.html");
            if (!File.Exists(indexPath))
            {
                _logger.LogDebug("Comskip web patch check: {Path} not found, skipping", indexPath);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(indexPath);
            var missing = Patches.Where(p => !html.Contains(p.Marker, StringComparison.Ordinal)).ToList();

            if (missing.Count == 0)
            {
                _logger.LogInformation("Comskip web patches present in {Path}", indexPath);
                return Task.CompletedTask;
            }

            foreach (var (_, description) in missing)
            {
                _logger.LogWarning(
                    "Comskip web patch is missing at {Path}: {Description}. This is expected right " +
                    "after installing/updating this feature, and normal again after any jellyfin-web " +
                    "package upgrade, which overwrites index.html. Run the plugin's " +
                    "Web/install-overlay.sh with sudo to (re)install it, then hard-refresh the page.",
                    indexPath,
                    description);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Comskip web patch check failed (non-fatal)");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
