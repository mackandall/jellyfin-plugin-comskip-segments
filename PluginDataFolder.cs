using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments;

/// <summary>
/// Where the plugin keeps its state: &lt;data&gt;/comskip-segments, beside the other plugins'
/// data. It used to be &lt;plugins&gt;/ComskipSegments, the folder a manual install also put
/// the DLL in. Jellyfin deletes a plugin's old folder when it installs an update from the
/// catalog, so updating a manually installed copy wiped every stored result (2026-10-31).
/// The first start after that move copies the old files across.
/// </summary>
internal static class PluginDataFolder
{
    public const string FolderName = "comskip-segments";
    public const string OldFolderName = "ComskipSegments";

    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>The data folder, created (and filled from the old one) on first use.</summary>
    public static string Get(IApplicationPaths paths, ILogger logger)
    {
        lock (Gate)
        {
            var folder = Path.Combine(paths.DataPath, FolderName);
            if (_path == folder)
            {
                return folder;
            }

            var fresh = !Directory.Exists(folder);
            Directory.CreateDirectory(folder);
            if (fresh)
            {
                MoveFromOldFolder(Path.Combine(paths.PluginsPath, OldFolderName), folder, logger);
            }

            _path = folder;
            return folder;
        }
    }

    /// <summary>
    /// Copies the state files (not the overview pictures, which are rebuilt on demand) from
    /// the old folder. The old copies stay where they are: that folder may still hold a
    /// manually installed DLL, and an older version put back would still find its state.
    /// </summary>
    internal static int MoveFromOldFolder(string oldFolder, string folder, ILogger logger)
    {
        if (!Directory.Exists(oldFolder))
        {
            return 0;
        }

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(oldFolder))
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("index.html.comskip-backup", StringComparison.Ordinal))
            {
                continue;
            }

            if (name.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
            {
                continue; // the plugin's own manifest, not state
            }

            var target = Path.Combine(folder, name);
            try
            {
                if (!File.Exists(target))
                {
                    File.Copy(file, target);
                    copied++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not move {File} to the plugin's new data folder {Folder}", file, folder);
            }
        }

        if (copied > 0)
        {
            logger.LogInformation("Moved {Count} state file(s) from {Old} to {New}; the old copies are no longer used", copied, oldFolder, folder);
        }

        return copied;
    }
}
