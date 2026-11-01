using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>Why a recording was queued for Comskip (shown on the dashboard's Queue tab).</summary>
public static class ScanReason
{
    /// <summary>Someone asked for it: the item menu, the editor's Re-run, the Queue tab's Scan again.</summary>
    public const string Manual = "manual";

    /// <summary>A recording just finished.</summary>
    public const string NewRecording = "recording";

    /// <summary>The hourly check / the Scan Library for Commercials task.</summary>
    public const string LibraryCheck = "library";

    /// <summary>Jellyfin's Media Segment Scan found an item without a result.</summary>
    public const string SegmentScan = "segment-scan";

    /// <summary>The file was trimmed (audio format or program start), so its breaks moved.</summary>
    public const string AfterTrim = "trim";
}

/// <param name="Force">Scans even outside the automatic scope (a manual scan).</param>
public sealed record QueueEntry(Guid Id, string Reason, DateTime QueuedUtc, bool Force);

/// <param name="Result">breaks, none, failed, stopped, skipped.</param>
/// <param name="Detail">Why it failed or was skipped.</param>
/// <param name="Comskip">Comskip itself ran (its time counts towards the "time left" guess).</param>
public sealed record FinishedEntry(
    Guid Id,
    string Name,
    string Reason,
    string Result,
    int Breaks,
    string? Detail,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    bool Comskip);

/// <summary>
/// The Comskip queue in order, kept on disk so a restart doesn't lose it, and the last
/// <see cref="HistorySize"/> finished scans. It used to be an in-memory channel: a restart dropped
/// every waiting scan, and nothing showed what was waiting.
/// Saving is batched (a library check can queue hundreds of recordings in one go).
/// </summary>
public sealed class ScanQueue
{
    public const int HistorySize = 200;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ILogger _logger;
    private readonly string _queuePath;
    private readonly string _historyPath;
    private readonly string _pausedPath;
    private TaskCompletionSource _resumed = Completed();
    private bool _paused;
    private readonly object _gate = new();
    private readonly List<QueueEntry> _waiting = new();
    private readonly LinkedList<FinishedEntry> _history = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Timer _saveTimer;
    private Func<IReadOnlyList<QueueEntry>> _alsoSave = () => Array.Empty<QueueEntry>();
    private bool _saveScheduled;
    private bool _frozen;

    public ScanQueue(ILogger logger, IApplicationPaths paths)
    {
        _logger = logger;
        var folder = PluginDataFolder.Get(paths, logger);
        _queuePath = Path.Combine(folder, "scan-queue.json");
        _historyPath = Path.Combine(folder, "scan-history.json");
        _pausedPath = Path.Combine(folder, "scan-queue-paused");
        if (File.Exists(_pausedPath))
        {
            _paused = true;
            _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        foreach (var f in Read<List<FinishedEntry>>(_historyPath) ?? new List<FinishedEntry>())
        {
            _history.AddLast(f);
        }
    }

    /// <summary>Entries outside the waiting list that must survive a restart too (running, waiting for a recording to end).</summary>
    public void SaveAlso(Func<IReadOnlyList<QueueEntry>> others) => _alsoSave = others;

    /// <summary>What was queued when the server stopped (running scans first), read once at start.</summary>
    public IReadOnlyList<QueueEntry> LoadSaved() => Read<List<QueueEntry>>(_queuePath) ?? new List<QueueEntry>();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count;
            }
        }
    }

    public IReadOnlyList<QueueEntry> Waiting()
    {
        lock (_gate)
        {
            return _waiting.ToList();
        }
    }

    public IReadOnlyList<FinishedEntry> History()
    {
        lock (_gate)
        {
            return _history.ToList();
        }
    }

    public void Add(QueueEntry entry, bool first = false)
    {
        lock (_gate)
        {
            if (first)
            {
                _waiting.Insert(0, entry);
            }
            else
            {
                _waiting.Add(entry);
            }
        }

        _signal.Release();
        Changed();
    }

    /// <summary>Paused: nothing new starts (running scans go on). Kept across restarts.</summary>
    public bool Paused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused)
            {
                return;
            }

            _paused = paused;
            if (paused)
            {
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                _resumed.TrySetResult();
            }
        }

        try
        {
            if (paused)
            {
                File.WriteAllText(_pausedPath, "Paused from the Queue tab: delete this file (or press Resume) to start scans again.");
            }
            else
            {
                File.Delete(_pausedPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the queue's paused state");
        }
    }

    private static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource();
        t.SetResult();
        return t;
    }

    /// <summary>The next scan, waiting until there is one (and the queue isn't paused).</summary>
    public async Task<QueueEntry> TakeAsync(CancellationToken ct)
    {
        while (true)
        {
            Task resumed;
            lock (_gate)
            {
                resumed = _resumed.Task;
            }

            await resumed.WaitAsync(ct).ConfigureAwait(false);
            await _signal.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_paused)
                {
                    _signal.Release(); // paused meanwhile: leave it for after Resume
                    continue;
                }

                if (_waiting.Count > 0)
                {
                    var next = _waiting[0];
                    _waiting.RemoveAt(0);
                    return next; // saved by the caller once it's marked running
                }
            }

            // Removed from the list after it was signalled: wait for the next one.
        }
    }

    /// <summary>Takes the matching waiting entries out; returns them.</summary>
    public IReadOnlyList<QueueEntry> Remove(Func<QueueEntry, bool> match)
    {
        List<QueueEntry> removed;
        lock (_gate)
        {
            removed = _waiting.Where(match).ToList();
            _waiting.RemoveAll(e => match(e));
        }

        if (removed.Count > 0)
        {
            Changed();
        }

        return removed;
    }

    /// <summary>Moves a waiting entry to the front; false if it isn't waiting.</summary>
    public bool MoveFirst(Guid id)
    {
        lock (_gate)
        {
            var i = _waiting.FindIndex(e => e.Id == id);
            if (i < 0)
            {
                return false;
            }

            var e = _waiting[i];
            _waiting.RemoveAt(i);
            _waiting.Insert(0, e);
        }

        Changed();
        return true;
    }

    public void Finished(FinishedEntry entry)
    {
        lock (_gate)
        {
            _history.AddFirst(entry);
            while (_history.Count > HistorySize)
            {
                _history.RemoveLast();
            }
        }

        Changed();
    }

    /// <summary>Saves soon (batched).</summary>
    public void Changed()
    {
        lock (_gate)
        {
            if (_saveScheduled || _frozen)
            {
                return;
            }

            _saveScheduled = true;
        }

        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Saves now and stops saving: the server is stopping, and the running scans it cancels
    /// must stay in the saved queue (they start again first after the restart).
    /// </summary>
    public void Freeze()
    {
        Save();
        lock (_gate)
        {
            _frozen = true;
        }
    }

    private void Save()
    {
        List<QueueEntry> queue;
        List<FinishedEntry> history;
        lock (_gate)
        {
            _saveScheduled = false;
            if (_frozen)
            {
                return;
            }

            history = _history.ToList();
            queue = _waiting.ToList();
        }

        IReadOnlyList<QueueEntry> others;
        try
        {
            others = _alsoSave();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not list the running scans to save");
            others = Array.Empty<QueueEntry>();
        }

        // Running first: they go back to the top after a restart.
        var all = others.Concat(queue).GroupBy(e => e.Id).Select(g => g.First()).ToList();
        Write(_queuePath, all);
        Write(_historyPath, history);
    }

    private void Write<T>(string path, T value)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {Path}", path);
        }
    }

    private T? Read<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read {Path}; starting with an empty list", path);
            return null;
        }
    }
}
