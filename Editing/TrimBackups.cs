using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Editing;

/// <summary>
/// The untouched originals trims keep for "Undo trim". A backup sits beside its recording as a
/// hidden file, ".&lt;name&gt;.original&lt;ext&gt;": same folder, so keeping it is a rename and
/// undoing is a rename back, and Jellyfin ignores files whose names start with a dot, so it
/// never shows up as a second copy of the recording. When originals aren't kept for good
/// ("Keep the original file as a backup" off), each backup is listed here with the time it
/// expires, and <see cref="TrimBackupCleaner"/> deletes it then.
/// </summary>
public sealed class TrimBackups
{
    private readonly ILogger<TrimBackups> _logger;
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, DateTime> _expiries;

    public TrimBackups(ILogger<TrimBackups> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;
        var dataFolder = PluginDataFolder.Get(applicationPaths, _logger);
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "trim-backups.json");
        _expiries = Load();
    }

    /// <summary>Where a trim keeps <paramref name="recordingPath"/>'s original.</summary>
    public static string PathFor(string recordingPath) =>
        Path.Combine(Path.GetDirectoryName(recordingPath)!, "." + Path.GetFileNameWithoutExtension(recordingPath) + ".original" + Path.GetExtension(recordingPath));

    /// <summary>
    /// Where the .edl that went with <paramref name="backupPath"/> is kept, beside it and named
    /// after it (".&lt;name&gt;.original.edl"), so it goes wherever the backup goes.
    /// </summary>
    public static string EdlFor(string backupPath) =>
        Path.Combine(Path.GetDirectoryName(backupPath)!, Path.GetFileNameWithoutExtension(backupPath) + ".edl");

    /// <summary>
    /// The kept original of <paramref name="recordingPath"/>, if there is one: the hidden backup,
    /// or one kept visibly as "&lt;name&gt;.original&lt;ext&gt;" by an earlier version.
    /// </summary>
    public static string? Find(string? recordingPath)
    {
        if (string.IsNullOrEmpty(recordingPath))
        {
            return null;
        }

        var hidden = PathFor(recordingPath);
        if (File.Exists(hidden))
        {
            return hidden;
        }

        var visible = Path.Combine(Path.GetDirectoryName(recordingPath)!, Path.GetFileNameWithoutExtension(recordingPath) + ".original" + Path.GetExtension(recordingPath));
        return File.Exists(visible) ? visible : null;
    }

    /// <summary>When <paramref name="backupPath"/> is due to be deleted, if it is.</summary>
    public DateTime? ExpiresUtc(string backupPath)
    {
        lock (_lock)
        {
            return _expiries.TryGetValue(backupPath, out var at) ? at : null;
        }
    }

    /// <summary>Delete <paramref name="backupPath"/> at <paramref name="expiresUtc"/>.</summary>
    public void Expire(string backupPath, DateTime expiresUtc)
    {
        lock (_lock)
        {
            _expiries[backupPath] = expiresUtc;
            Persist();
        }
    }

    /// <summary>The backup was used (undone) or is gone: stop tracking it.</summary>
    public void Forget(string backupPath)
    {
        lock (_lock)
        {
            if (_expiries.Remove(backupPath))
            {
                Persist();
            }
        }
    }

    /// <summary>
    /// <paramref name="backupPath"/> turned up in another folder (JellyDVR Space moved the recording
    /// with it): the undo time of the one of that name that's gone from where it was carries over.
    /// </summary>
    public void Follow(string backupPath)
    {
        lock (_lock)
        {
            if (_expiries.ContainsKey(backupPath))
            {
                return;
            }

            var name = Path.GetFileName(backupPath);
            var old = _expiries.Keys.FirstOrDefault(k => string.Equals(Path.GetFileName(k), name, StringComparison.Ordinal) && !File.Exists(k));
            if (old is null)
            {
                return;
            }

            _expiries[backupPath] = _expiries[old];
            _expiries.Remove(old);
            Persist();
            _logger.LogInformation("The kept original {Old} moved to {New}; it still goes at {Expires}", old, backupPath, _expiries[backupPath]);
        }
    }

    /// <summary>
    /// Deletes the backups whose time is up; the ones deleted. One that's gone from its place is
    /// only dropped then too: it may have moved, and <see cref="Follow"/> picks it up when its
    /// recording turns up.
    /// </summary>
    public List<string> DeleteExpired()
    {
        var deleted = new List<string>();
        List<string> due;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            due = _expiries.Where(e => e.Value <= now).Select(e => e.Key).ToList();
        }

        foreach (var path in due)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.LogInformation("Deleted the kept original {Path}: its undo time is up", path);
                    deleted.Add(path);
                }

                if (File.Exists(EdlFor(path)))
                {
                    File.Delete(EdlFor(path));
                }

                Forget(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept on the list: tried again next time.
                _logger.LogWarning(ex, "Could not delete the kept original {Path}", path);
            }
        }

        return deleted;
    }

    private Dictionary<string, DateTime> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path)) ?? new();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the trim backup list; starting fresh");
        }

        return new();
    }

    private void Persist()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_expiries));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the trim backup list");
        }
    }
}

/// <summary>
/// Deletes kept originals whose undo time is up: at startup, then every hour. Once a day
/// (the first time 10 minutes after startup, once the library is loaded) it also looks through
/// the library folders for originals left without their recording (see <see cref="KeptOriginals"/>).
/// </summary>
public sealed class TrimBackupCleaner : BackgroundService
{
    private static readonly TimeSpan OrphanEvery = TimeSpan.FromDays(1);
    private readonly TrimBackups _backups;
    private readonly KeptOriginals _kept;
    private readonly ILogger<TrimBackupCleaner> _logger;

    public TrimBackupCleaner(TrimBackups backups, KeptOriginals kept, ILogger<TrimBackupCleaner> logger)
    {
        _backups = backups;
        _kept = kept;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextOrphans = DateTime.UtcNow.AddMinutes(10);
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var deleted in _backups.DeleteExpired())
            {
                _kept.TidyAfter(deleted);
            }

            if (DateTime.UtcNow >= nextOrphans)
            {
                nextOrphans = DateTime.UtcNow + OrphanEvery;
                try
                {
                    if (Plugin.Instance?.Configuration.DeleteOrphanedOriginals != false)
                    {
                        _kept.DeleteOrphans();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not look for kept originals left without their recording");
                }
            }

            try
            {
                var wait = TimeSpan.FromHours(1);
                if (nextOrphans - DateTime.UtcNow < wait)
                {
                    wait = (nextOrphans - DateTime.UtcNow).Duration();
                }

                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
