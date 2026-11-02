using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <param name="Source">Where the breaks came from: <see cref="SourceComskip"/> (this
/// plugin ran Comskip), <see cref="SourceEdl"/> (an .edl another program — SageTV,
/// Channels DVR, Emby — left beside the video), <see cref="SourceEdited"/> or
/// <see cref="SourceCut"/>; "cleared" after Remove commercials. Null on records written before this
/// field existed, which were all Comskip runs.</param>
/// <param name="RepeatSeconds">Seconds of breaks the repeat-ad check added to Comskip's.</param>
/// <param name="RemovedSeconds">Seconds of Comskip's breaks it gave back to the show (learned from hand-edited episodes).</param>
public sealed record DetectionRecord(
    DetectionStatus Status,
    IReadOnlyList<CommercialBreak> Breaks,
    DateTime UpdatedUtc,
    string? Source = null,
    double RepeatSeconds = 0,
    double RemovedSeconds = 0)
{
    public const string SourceComskip = "comskip";
    public const string SourceEdl = "edl";

    /// <summary>Set by hand on the segments dashboard: automatic detection leaves them alone.</summary>
    public const string SourceEdited = "edited";

    /// <summary>The commercials were cut out of the file (see <see cref="Editing.CommercialCutService"/>).</summary>
    public const string SourceCut = "cut";
}

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
    private readonly string _ownedEdlsPath;
    private readonly ConcurrentDictionary<string, byte> _ownedEdls;

    // Plugin.Instance.DataFolderPath is unreliable here: PluginServiceRegistrator runs
    // (and can eagerly resolve this singleton) before Jellyfin's plugin loader has
    // necessarily constructed the Plugin instance and set Plugin.Instance, so it was
    // observed null at startup and the store silently fell back to /tmp (not
    // persistent across restarts/reboots). IApplicationPaths is injected straight from
    // Jellyfin's root DI container instead, which is available immediately.
    public DetectionStore(ILogger<DetectionStore> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;

        var dataFolder = PluginDataFolder.Get(applicationPaths, _logger);
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "detection-state.json");

        _records = Load();

        _ownedEdlsPath = Path.Combine(dataFolder, "owned-edls.json");
        _ownedEdls = LoadOwnedEdls();
    }

    public IReadOnlyCollection<KeyValuePair<Guid, DetectionRecord>> All() => _records.ToArray();

    /// <summary>
    /// True if this plugin wrote the .edl at <paramref name="edlPath"/> (beside a recording).
    /// Such a file is only our own earlier result, so it may be replaced and is never
    /// "imported"; any other .edl beside a video belongs to another program and is
    /// read but never overwritten.
    /// </summary>
    public bool IsOwnedEdl(string edlPath) => _ownedEdls.ContainsKey(edlPath);

    public void MarkOwnedEdl(string edlPath)
    {
        if (_ownedEdls.TryAdd(edlPath, 0))
        {
            PersistOwnedEdls();
        }
    }

    /// <summary>Marks several paths at once (startup seeding); persists once.</summary>
    public int MarkOwnedEdls(IEnumerable<string> edlPaths)
    {
        var added = 0;
        foreach (var p in edlPaths)
        {
            if (_ownedEdls.TryAdd(p, 0))
            {
                added++;
            }
        }

        if (added > 0)
        {
            PersistOwnedEdls();
        }

        return added;
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

    private ConcurrentDictionary<string, byte> LoadOwnedEdls()
    {
        try
        {
            if (File.Exists(_ownedEdlsPath))
            {
                var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_ownedEdlsPath));
                if (paths is not null)
                {
                    return new ConcurrentDictionary<string, byte>(paths.Select(p => new KeyValuePair<string, byte>(p, 0)), StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the list of .edl files this plugin wrote; starting fresh");
        }

        return new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
    }

    private void PersistOwnedEdls()
    {
        lock (_ioLock)
        {
            try
            {
                var tmp = _ownedEdlsPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_ownedEdls.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList()));
                File.Move(tmp, _ownedEdlsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not persist the list of .edl files this plugin wrote");
            }
        }
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
