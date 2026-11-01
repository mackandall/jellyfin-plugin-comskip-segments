using System;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// Adds this plugin's browser scripts (the seek-bar commercial marks, the item menu) to
/// jellyfin-web's index.html, the same as Web/install-overlay.sh does. Jellyfin can only do it
/// where its own account may write that file: Docker and Windows usually, a Linux package install
/// only after a one-time chown; otherwise the page shows the command. Keep the blocks in step with
/// install-overlay.sh.
/// </summary>
public sealed class WebOverlayPatcher
{
    // Relative to index.html (<server>[/<Base URL>]/web/), so a Base URL works.
    private static readonly (string Begin, string End, string Block)[] Blocks =
    {
        ("<!-- ComskipSegments:overlay:begin -->", "<!-- ComskipSegments:overlay:end -->",
            "<!-- ComskipSegments:overlay:begin -->\n" +
            "<link rel=\"stylesheet\" href=\"../ComskipSegments/web/overlay.css\">\n" +
            "<script defer src=\"../ComskipSegments/web/overlay.js\"></script>\n" +
            "<!-- ComskipSegments:overlay:end -->\n"),
        ("<!-- ComskipSegments:itemmenu:begin -->", "<!-- ComskipSegments:itemmenu:end -->",
            "<!-- ComskipSegments:itemmenu:begin -->\n" +
            "<script defer src=\"../ComskipSegments/web/itemmenu.js\"></script>\n" +
            "<!-- ComskipSegments:itemmenu:end -->\n"),
    };

    private readonly IApplicationPaths _paths;
    private readonly ILogger<WebOverlayPatcher> _logger;

    public WebOverlayPatcher(IApplicationPaths paths, ILogger<WebOverlayPatcher> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// What the settings page shows. State: "installed", "missing" (none or some of it),
    /// "outdated" (an older patch, e.g. one that breaks behind a Base URL) or "nofile".
    /// </summary>
    public sealed record OverlayStatus(string State, string IndexPath, bool CanWrite, string? FixCommand);

    public sealed record PatchResult(bool Ok, string Message, OverlayStatus Status);

    public string IndexPath => Path.Combine(_paths.WebPath, "index.html");

    public OverlayStatus Status()
    {
        var path = IndexPath;
        if (!File.Exists(path))
        {
            return new OverlayStatus("nofile", path, false, null);
        }

        var html = File.ReadAllText(path);
        var state = Apply(html) == html && Blocks.All(b => html.Contains(b.Begin, StringComparison.Ordinal)) ? "installed"
            : Blocks.All(b => html.Contains(b.Begin, StringComparison.Ordinal)) ? "outdated"
            : "missing";
        var canWrite = CanWrite(path);
        return new OverlayStatus(state, path, canWrite, canWrite ? null : FixCommand(path));
    }

    /// <summary>Adds the blocks (or brings older ones up to date), keeping a copy of the file as it was.</summary>
    public PatchResult Install()
    {
        var path = IndexPath;
        if (!File.Exists(path))
        {
            return new PatchResult(false, $"Jellyfin's web page wasn't found at {path}.", Status());
        }

        var html = File.ReadAllText(path);
        var patched = Apply(html);
        if (!Blocks.All(b => patched.Contains(b.Begin, StringComparison.Ordinal)))
        {
            return new PatchResult(false, $"{path} has no </body> tag, so it can't be patched.", Status());
        }

        if (patched == html)
        {
            return new PatchResult(true, "The web overlay is already installed. Reload the web page if the marks don't show.", Status());
        }

        try
        {
            // Written in place, so a one-time chown of the file is enough; the copy goes in the plugin's data folder.
            var backup = Path.Combine(PluginDataFolder.Get(_paths, _logger), "index.html.comskip-backup");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.WriteAllText(backup, html);
            File.WriteAllText(path, patched);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning("Comskip web overlay: can't write {Path} ({Error})", path, ex.Message);
            return new PatchResult(false, $"Jellyfin isn't allowed to change {path}. Run this once on the server, then press the button again:\n{FixCommand(path)}", Status());
        }

        _logger.LogInformation("Comskip web overlay installed in {Path}", path);
        return new PatchResult(true, "Installed. Reload the Jellyfin web page (a hard refresh) to see the commercial marks.", Status());
    }

    /// <summary>The page with every block present and current: an older block is replaced where it is, a missing one goes before &lt;/body&gt;.</summary>
    public static string Apply(string html)
    {
        foreach (var (begin, end, block) in Blocks)
        {
            var start = html.IndexOf(begin, StringComparison.Ordinal);
            var stop = start >= 0 ? html.IndexOf(end, start, StringComparison.Ordinal) : -1;
            if (start >= 0 && stop >= 0)
            {
                stop += end.Length;
                if (stop < html.Length && html[stop] == '\n')
                {
                    stop++;
                }

                html = string.Concat(html.AsSpan(0, start), block, html.AsSpan(stop));
            }
            else
            {
                var body = html.IndexOf("</body>", StringComparison.Ordinal);
                if (body >= 0)
                {
                    html = html.Insert(body, block);
                }
            }
        }

        return html;
    }

    private static bool CanWrite(string path)
    {
        try
        {
            // Open only: FileMode.Open doesn't truncate.
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>What lets Jellyfin's own account write the file: a chown, inside the container on Docker.</summary>
    private static string FixCommand(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"Give the account Jellyfin runs as ({Environment.UserName}) permission to modify \"{path}\" (its Properties › Security).";
        }

        var chown = $"chown {Environment.UserName} \"{path}\"";
        return File.Exists("/.dockerenv") ? $"docker exec -u 0 <your Jellyfin container> {chown}" : $"sudo {chown}";
    }
}
