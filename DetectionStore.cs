using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

public enum DetectionStatus
{
    /// <summary>Comskip ran and found one or more commercial breaks.</summary>
    Detected,

    /// <summary>Comskip ran cleanly but found nothing to skip. A VALID result — do not retry.</summary>
    NoCommercials,

    /// <summary>Comskip crashed/timed out/produced no EDL. NOT the same as NoCommercials — retry allowed.</summary>
    Failed
}

public sealed record DetectionRecord(
    DetectionStatus Status,
    IReadOnlyList<CommercialBreak> Breaks,
    DateTime UpdatedUtc);

/// <summary>
/// Tiny JSON-backed store keyed by Jellyfin ItemId. Its whole reason to exist is the
/// crash-vs-empty distinction: "processed, zero segments" and "detection failed" must
/// be recorded differently, or corrupted recordings would be marked done forever and
/// never re-attempted after you fix the source or upgrade Comskip.
/// </summary>
public sealed class DetectionStore
{
    private readonly ILogger<DetectionStore> _logger;
    private readonly string _path;
    private readonly object _ioLock = new();
    private readonly ConcurrentDictionary<Guid, DetectionRecord> _records;

    // Plugin.Instance.DataFolderPath is unreliable here: PluginServiceRegistrator runs
    // (and can eagerly resolve this singleton) before Jellyfin's plugin loader has
    // necessarily constructed the Plugin instance and set Plugin.Instance, so it was
    // observed null at startup and the store silently fell back to /tmp (not
    // persistent across restarts/reboots). IApplicationPaths is injected straight from
    // Jellyfin's root DI container instead, which is available immediately.
    public DetectionStore(ILogger<DetectionStore> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;

        var dataFolder = Path.Combine(applicationPaths.PluginsPath, "ComskipSegments");
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "detection-state.json");

        _records = Load();
    }

    public bool TryGet(Guid itemId, out DetectionRecord record) => _records.TryGetValue(itemId, out record!);

    /// <summary>A completed result (Detected or NoCommercials) exists — no need to re-run.</summary>
    public bool IsCompleted(Guid itemId) =>
        _records.TryGetValue(itemId, out var r) && r.Status != DetectionStatus.Failed;

    public void Save(Guid itemId, DetectionRecord record)
    {
        _records[itemId] = record;
        Persist();
    }

    public void Remove(Guid itemId)
    {
        _records.TryRemove(itemId, out _);
        Persist();
    }

    private ConcurrentDictionary<Guid, DetectionRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var data = JsonSerializer.Deserialize<Dictionary<Guid, DetectionRecord>>(json);
                if (data is not null)
                {
                    return new ConcurrentDictionary<Guid, DetectionRecord>(data);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load detection state; starting fresh");
        }

        return new ConcurrentDictionary<Guid, DetectionRecord>();
    }

    private void Persist()
    {
        lock (_ioLock)
        {
            try
            {
                var tmp = _path + ".tmp";
                var json = JsonSerializer.Serialize(
                    new Dictionary<Guid, DetectionRecord>(_records),
                    new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true); // atomic-ish swap
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not persist detection state");
            }
        }
    }
}
