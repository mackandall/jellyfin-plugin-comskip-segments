using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.ProgramStart;

/// <summary>
/// A remembered program-start card: a frame a channel shows just before its programs (Starz's
/// "The following is rated…" card, say), as a small greyscale picture to compare frames with.
/// </summary>
public sealed record StartCard(
    Guid Id,
    string Label,
    string? Channel,
    string Pixels,          // base64, CardFrames.Width × CardFrames.Height bytes of luma
    string? SourcePath,
    double SourceSeconds,
    DateTime CreatedUtc);

/// <summary>Where a recording's program starts, and how that was decided.</summary>
public sealed record ProgramStartRecord(
    double StartSeconds,
    string Source,          // "manual", "card", "card-auto"
    Guid? CardId,
    string Action,          // "marker" or "trim"
    string Message,
    DateTime UpdatedUtc,
    string? BackupPath = null)   // the untouched original a trim kept, for Undo
{
    public const string SourceManual = "manual";
    public const string SourceCard = "card";
    public const string SourceCardAuto = "card-auto";
    public const string SourceNone = "none";       // looked automatically, no card found
    public const string SourceUndone = "undone";   // a trim was undone: left alone from now on
}

/// <summary>
/// The remembered cards and each recording's program start, in one small JSON file beside
/// the plugin's other state (same pattern as the audio-trim store).
/// </summary>
public sealed class ProgramStartStore
{
    private readonly ILogger<ProgramStartStore> _logger;
    private readonly string _path;
    private readonly object _lock = new();
    private State _state;

    public ProgramStartStore(ILogger<ProgramStartStore> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;
        var dataFolder = PluginDataFolder.Get(applicationPaths, _logger);
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "program-start.json");
        _state = Load();
    }

    public IReadOnlyList<StartCard> Cards
    {
        get
        {
            lock (_lock)
            {
                return _state.Cards.ToList();
            }
        }
    }

    public void AddCard(StartCard card)
    {
        lock (_lock)
        {
            _state.Cards.Add(card);
            Persist();
        }
    }

    public bool RemoveCard(Guid id)
    {
        lock (_lock)
        {
            var removed = _state.Cards.RemoveAll(c => c.Id == id) > 0;
            if (removed)
            {
                Persist();
            }

            return removed;
        }
    }

    public bool TryGet(Guid itemId, out ProgramStartRecord record)
    {
        lock (_lock)
        {
            return _state.Starts.TryGetValue(itemId, out record!);
        }
    }

    public void Save(Guid itemId, ProgramStartRecord record)
    {
        lock (_lock)
        {
            _state.Starts[itemId] = record;
            Persist();
        }
    }

    public bool Remove(Guid itemId)
    {
        lock (_lock)
        {
            var removed = _state.Starts.Remove(itemId);
            if (removed)
            {
                Persist();
            }

            return removed;
        }
    }

    private State Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load program-start state; starting fresh");
        }

        return new State();
    }

    private void Persist()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist program-start state");
        }
    }

    private sealed class State
    {
        public List<StartCard> Cards { get; set; } = new();

        public Dictionary<Guid, ProgramStartRecord> Starts { get; set; } = new();
    }
}
