using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Editing;

/// <summary>
/// A recording whose commercials were cut out: what it kept, and what it had before, so Undo can
/// put the breaks back with the original file.
/// </summary>
/// <param name="Parts">What was kept, in seconds of the uncut file (each part starting on its keyframe).</param>
/// <param name="BreaksBefore">The commercial breaks before the cut.</param>
/// <param name="SourceBefore">Where those came from (comskip, edl, edited).</param>
/// <param name="SecondsBefore">The uncut length.</param>
/// <param name="OwnBackup">The cut made the kept original (no earlier trim had), so Undo brings back exactly what was cut.</param>
/// <param name="ForeignEdlAside">Another program's .edl, kept beside the original because it no longer matched the cut file.</param>
/// <param name="Path">The recording's file, to find it again when it's moved (null on cuts from before 1.3.1).</param>
public sealed record CommercialCutRecord(
    IReadOnlyList<KeptPart> Parts,
    IReadOnlyList<CommercialBreak> BreaksBefore,
    string? SourceBefore,
    double SecondsBefore,
    bool OwnBackup,
    string? ForeignEdlAside,
    DateTime CutUtc,
    string? Path = null);

/// <summary>The commercial cuts, in one small JSON file beside the plugin's other state.</summary>
public sealed class CommercialCutStore
{
    private readonly ILogger<CommercialCutStore> _logger;
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<Guid, CommercialCutRecord> _records;

    public CommercialCutStore(ILogger<CommercialCutStore> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;
        var dataFolder = PluginDataFolder.Get(applicationPaths, _logger);
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "commercial-cuts.json");
        _records = Load();
    }

    public bool TryGet(Guid itemId, out CommercialCutRecord record)
    {
        lock (_lock)
        {
            return _records.TryGetValue(itemId, out record!);
        }
    }

    public IReadOnlyDictionary<Guid, CommercialCutRecord> All()
    {
        lock (_lock)
        {
            return _records.ToDictionary(r => r.Key, r => r.Value);
        }
    }

    public void Save(Guid itemId, CommercialCutRecord record)
    {
        lock (_lock)
        {
            _records[itemId] = record;
            Persist();
        }
    }

    /// <summary>The recording is a new item now (moved to another folder).</summary>
    public void Move(Guid fromId, Guid toId, CommercialCutRecord record)
    {
        lock (_lock)
        {
            _records.Remove(fromId);
            _records[toId] = record;
            Persist();
        }
    }

    public void Remove(Guid itemId)
    {
        lock (_lock)
        {
            if (_records.Remove(itemId))
            {
                Persist();
            }
        }
    }

    private Dictionary<Guid, CommercialCutRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<Dictionary<Guid, CommercialCutRecord>>(File.ReadAllText(_path)) ?? new();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the commercial cut list; starting fresh");
        }

        return new();
    }

    private void Persist()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_records));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the commercial cut list");
        }
    }
}
