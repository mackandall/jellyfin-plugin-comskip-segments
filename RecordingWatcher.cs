using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using Jellyfin.Plugin.ComskipSegments.Detection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Events;

/// <summary>
/// The DVR front door. Subscribes to the library's ItemAdded AND ItemUpdated events and
/// enqueues any item whose path sits under the configured recordings folder.
///
/// Why both events: when a scheduled recording finishes, Jellyfin only raises ItemAdded
/// if the episode is brand new. Re-recording an episode that already exists (a duplicate
/// timer, a re-run, a manual re-record) raises ItemUpdated instead, so an ItemAdded-only
/// watcher silently skips it — the recording just never gets a commercial scan.
///
/// Both events also fire repeatedly *while* the .ts is still being written, so we don't
/// enqueue immediately. Each event (re)arms a per-item timer; we only enqueue once the
/// file has stopped growing for <see cref="SettleDelay"/>. Path-prefix matching (rather
/// than reading live-TV internals) keeps this simple, consistent with the "own your
/// paths" approach used everywhere else.
/// </summary>
public sealed class RecordingWatcher : IHostedService, IDisposable
{
    /// <summary>How long a recording's file size must hold steady before we treat it as finished.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(30);

    /// <summary>Ignore stub/placeholder items — a real recording is far bigger than this.</summary>
    private const long MinRecordingBytes = 2_000_000;

    private readonly ILogger<RecordingWatcher> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly DetectionManager _manager;

    // Items seen via library events that we're waiting to "settle" (stop growing) before
    // enqueuing. Keyed by item id.
    private readonly ConcurrentDictionary<Guid, PendingItem> _pending = new();
    private readonly object _gate = new();
    private bool _disposed;

    public RecordingWatcher(
        ILogger<RecordingWatcher> logger,
        ILibraryManager libraryManager,
        DetectionManager manager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _manager = manager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _logger.LogInformation("Comskip recording watcher active (ItemAdded + ItemUpdated)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        Dispose();
        return Task.CompletedTask;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.ProcessRecordingsAutomatically)
        {
            return;
        }

        if (e.Item is not Video video || string.IsNullOrEmpty(video.Path) || !video.IsFileProtocol)
        {
            return;
        }

        var recordingsPath = cfg.RecordingsPath;
        if (string.IsNullOrWhiteSpace(recordingsPath) || !IsUnder(video.Path, recordingsPath))
        {
            return;
        }

        ArmSettleCheck(video.Id, video.Path);
    }

    /// <summary>(Re)start the per-item settle timer; each library event pushes the check out.</summary>
    private void ArmSettleCheck(Guid itemId, string path)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var pending = _pending.GetOrAdd(itemId, _ => new PendingItem());
            pending.LastSize = SafeFileLength(path);
            pending.Path = path;
            pending.Timer ??= new Timer(OnSettleTimer, itemId, Timeout.Infinite, Timeout.Infinite);
            pending.Timer.Change(SettleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnSettleTimer(object? state)
    {
        var itemId = (Guid)state!;

        try
        {
            if (!_pending.TryGetValue(itemId, out var pending))
            {
                return;
            }

            var size = SafeFileLength(pending.Path);

            if (size < 0)
            {
                // File vanished (deleted or moved out) between events — give up on it.
                Forget(itemId);
                return;
            }

            if (size != pending.LastSize)
            {
                // Still being written — check again after another quiet period.
                pending.LastSize = size;
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        pending.Timer?.Change(SettleDelay, Timeout.InfiniteTimeSpan);
                    }
                }

                return;
            }

            Forget(itemId);

            if (size < MinRecordingBytes)
            {
                _logger.LogDebug(
                    "Ignoring {Path}: {Bytes} bytes, not a finished recording", pending.Path, size);
                return;
            }

            // Enqueue() no-ops if this item already has a completed detection record or is
            // in flight, so re-firing on a later metadata-only ItemUpdated is harmless. When
            // the .ts was overwritten (re-record), Jellyfin's CleanupExtractedData has
            // already dropped the stale record by now, so this correctly re-detects.
            _logger.LogInformation("Recording settled, queueing commercial detection: {Path}", pending.Path);
            _manager.Enqueue(itemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settle check failed for {ItemId}", itemId);
            Forget(itemId);
        }
    }

    private void Forget(Guid itemId)
    {
        if (_pending.TryRemove(itemId, out var pending))
        {
            pending.Timer?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        foreach (var kv in _pending)
        {
            kv.Value.Timer?.Dispose();
        }

        _pending.Clear();
    }

    private static long SafeFileLength(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return -1;
        }

        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : -1;
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.Ordinal);
    }

    private sealed class PendingItem
    {
        public Timer? Timer { get; set; }

        public long LastSize { get; set; } = -1;

        public string? Path { get; set; }
    }
}
