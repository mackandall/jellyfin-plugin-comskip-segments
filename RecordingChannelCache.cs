using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// Caches the channel a recording came from, keyed by file path — populated by
/// <see cref="LiveRecordingChannelPoller"/> polling
/// <see cref="MediaBrowser.Controller.LiveTv.ILiveTvManager.GetRecordingsAsync"/> with
/// <c>IsInProgress = true</c> while a recording is still active.
///
/// Keyed by path, not item ID: confirmed live that Jellyfin does not add a DVR recording
/// to the library — and so it has no item ID yet — until AFTER it finishes recording, by
/// which point the channel is no longer obtainable at all. Polling (rather than reacting
/// to library events, which never fire in time) is what makes catching it possible.
///
/// Small and JSON-backed, same pattern as <see cref="DetectionStore"/>. Entries expire
/// after a couple of days so the file doesn't grow forever; a recording's channel is
/// only ever needed in the hours around when it happened.
/// </summary>
public sealed class RecordingChannelCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(2);

    private readonly ILogger<RecordingChannelCache> _logger;
    private readonly string _path;
    private readonly object _ioLock = new();
    private readonly ConcurrentDictionary<string, Entry> _cache;

    public RecordingChannelCache(ILogger<RecordingChannelCache> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;

        var dataFolder = Path.Combine(applicationPaths.PluginsPath, "ComskipSegments");
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "channel-cache.json");

        _cache = Load();
    }

    private sealed record Entry(ChannelResolver.ChannelInfo Channel, DateTime CapturedUtc);

    public bool TryGet(string recordingPath, out ChannelResolver.ChannelInfo info)
    {
        if (_cache.TryGetValue(Normalize(recordingPath), out var entry))
        {
            info = entry.Channel;
            return true;
        }

        info = default;
        return false;
    }

    public void Set(string recordingPath, ChannelResolver.ChannelInfo info)
    {
        _cache[Normalize(recordingPath)] = new Entry(info, DateTime.UtcNow);
        Persist();
    }

    private static string Normalize(string path) => path.Trim();

    private ConcurrentDictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json);
                if (data is not null)
                {
                    var cutoff = DateTime.UtcNow - MaxAge;
                    return new ConcurrentDictionary<string, Entry>(
                        data.Where(kv => kv.Value.CapturedUtc >= cutoff),
                        StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load channel cache; starting fresh");
        }

        return new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }

    private void Persist()
    {
        lock (_ioLock)
        {
            try
            {
                var cutoff = DateTime.UtcNow - MaxAge;
                var live = _cache.Where(kv => kv.Value.CapturedUtc >= cutoff)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(live));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist channel cache");
            }
        }
    }
}
