using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// Serves the web-player overlay assets (overlay.js/overlay.css) embedded in this
/// assembly. jellyfin-web's index.html is patched (see Web/install-overlay.sh — this
/// plugin can't write that file itself, it's root-owned) to load them from here, so a
/// plugin redeploy is enough to update the overlay; the web root never needs touching.
///
/// Anonymous: index.html loads these on every page, including the login screen before
/// anyone is signed in, so the request must not require auth. The script itself is a
/// no-op until a session with a playing item exists.
/// </summary>
[ApiController]
[Route("ComskipSegments/web")]
[AllowAnonymous]
public sealed class ComskipWebAssetsController : ControllerBase
{
    [HttpGet("overlay.js")]
    [Produces("application/javascript")]
    public async Task<ContentResult> GetOverlayJs()
    {
        var js = await ReadEmbeddedAsync("overlay.js").ConfigureAwait(false);
        return Content(js, "application/javascript");
    }

    [HttpGet("overlay.css")]
    [Produces("text/css")]
    public async Task<ContentResult> GetOverlayCss()
    {
        var css = await ReadEmbeddedAsync("overlay.css").ConfigureAwait(false);
        return Content(css, "text/css");
    }

    [HttpGet("itemmenu.js")]
    [Produces("application/javascript")]
    public async Task<ContentResult> GetItemMenuJs()
    {
        var js = await ReadEmbeddedAsync("itemmenu.js").ConfigureAwait(false);
        return Content(js, "application/javascript");
    }

    private static async Task<string> ReadEmbeddedAsync(string fileName)
    {
        var assembly = typeof(ComskipWebAssetsController).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Web.{fileName}";
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }
}
