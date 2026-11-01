using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.AudioTrim;

public enum AudioTrimStatus
{
    /// <summary>Scanned and a trim was applied.</summary>
    Applied,

    /// <summary>Scanned cleanly; no trim was needed. A VALID result — do not retry automatically.</summary>
    NotNeeded,

    /// <summary>Scan or ffmpeg failed, or the trimmed output failed its sanity check. Retry allowed.</summary>
    Failed
}

public sealed record AudioTrimRecord(
    AudioTrimStatus Status,
    double LeadTrimSeconds,
    double TrailTrimSeconds,
    string? BackupPath,
    string Message,
    DateTime UpdatedUtc);

/// <summary>
/// Tiny JSON-backed store keyed by Jellyfin ItemId, tracking what the audio-trim
/// feature has already done to a recording. Same crash-vs-clean distinction as
/// <see cref="Jellyfin.Plugin.ComskipSegments.Detection.DetectionStore"/> and for the
/// same reason: "scanned, nothing to do" must never be retried automatically (it would
/// pointlessly re-decode a multi-gigabyte file's audio track on every backstop pass),
/// but "the scan or the ffmpeg run itself failed" must be.
/// </summary>
public sealed class AudioTrimStore
{
    private readonly ILogger<AudioTrimStore> _logger;
    private readonly string _path;
    private readonly object _ioLock = new();
    private readonly ConcurrentDictionary<Guid, AudioTrimRecord> _records;

    public AudioTrimStore(ILogger<AudioTrimStore> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;

        var dataFolder = PluginDataFolder.Get(applicationPaths, _logger);
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "audio-trim-state.json");

        _records = Load();
    }

    public bool TryGet(Guid itemId, out AudioTrimRecord record) => _records.TryGetValue(itemId, out record!);

    /// <summary>A completed result (Applied or NotNeeded) exists — no need to re-scan automatically.</summary>
    public bool IsCompleted(Guid itemId) =>
        _records.TryGetValue(itemId, out var r) && r.Status != AudioTrimStatus.Failed;

    public void Save(Guid itemId, AudioTrimRecord record)
    {
        _records[itemId] = record;
        Persist();
    }

    public void Remove(Guid itemId)
    {
        _records.TryRemove(itemId, out _);
        Persist();
    }

    private ConcurrentDictionary<Guid, AudioTrimRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var data = JsonSerializer.Deserialize<Dictionary<Guid, AudioTrimRecord>>(json);
                if (data is not null)
                {
                    return new ConcurrentDictionary<Guid, AudioTrimRecord>(data);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load audio-trim state; starting fresh");
        }

        return new ConcurrentDictionary<Guid, AudioTrimRecord>();
    }

    private void Persist()
    {
        lock (_ioLock)
        {
            try
            {
                var tmp = _path + ".tmp";
                var json = JsonSerializer.Serialize(
                    new Dictionary<Guid, AudioTrimRecord>(_records),
                    new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not persist audio-trim state");
            }
        }
    }
}
