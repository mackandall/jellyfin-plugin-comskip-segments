using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// The heart of the plugin: ONE detection queue with a capped number of workers.
/// Both triggers (new DVR recordings and the on-demand library scan) drop item IDs
/// in here, so all the heavy, version-independent logic lives in one place and the
/// two front doors stay symmetric.
///
/// This runs OUT OF BAND from Jellyfin's Media Segment Scan. The segment provider
/// never blocks on Comskip; it only serves whatever this manager has already stored.
/// When a job finishes, we ask Jellyfin to re-run providers for that one item so the
/// fresh segments publish immediately instead of waiting for the next full scan.
/// </summary>
public sealed class DetectionManager : BackgroundService
{
    private readonly ILogger<DetectionManager> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ComskipRunner _runner;
    private readonly DetectionStore _store;
    private readonly RecordingChannelCache _channelCache;

    private readonly Channel<Guid> _queue =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false });

    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    // In-progress-recording guard: an item enqueued while its .ts is still being written
    // (e.g. a backstop scan or Media Segment Scan that overlaps a recording) is not run
    // now — it's re-checked after RequeueDelay, up to MaxDeferrals times. _requeueScheduled
    // keeps that to one pending timer per item.
    private static readonly TimeSpan RequeueDelay = TimeSpan.FromMinutes(2);
    private const int MaxDeferrals = 45; // ~90 min ceiling before we stop chasing a file
    private readonly ConcurrentDictionary<Guid, int> _deferrals = new();
    private readonly ConcurrentDictionary<Guid, byte> _requeueScheduled = new();
    private CancellationToken _stoppingToken = CancellationToken.None;

    // NOTE: IMediaSegmentManager is resolved lazily via IServiceProvider rather than
    // constructor-injected. On Jellyfin 12 the segment manager's provider list is built
    // eagerly inside the IProviderManager -> IDtoService graph at startup, so taking a
    // direct IMediaSegmentManager dependency here creates a fatal circular dependency:
    //   IMediaSegmentManager -> IEnumerable<IMediaSegmentProvider> -> ComskipSegmentProvider
    //   -> DetectionManager -> IMediaSegmentManager
    public DetectionManager(
        ILogger<DetectionManager> logger,
        ILibraryManager libraryManager,
        IServiceProvider serviceProvider,
        ComskipRunner runner,
        DetectionStore store,
        RecordingChannelCache channelCache)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _serviceProvider = serviceProvider;
        _runner = runner;
        _store = store;
        _channelCache = channelCache;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Enqueue an item for detection unless it's already done or already queued.</summary>
    public void Enqueue(Guid itemId, bool force = false)
    {
        if (itemId.Equals(Guid.Empty))
        {
            return;
        }

        if (!force && _store.IsCompleted(itemId))
        {
            return;
        }

        if (!force)
        {
            // Single chokepoint for the show/folder allow- & block-lists: the watcher and
            // the media-segment provider both land here. LibraryScanTask pre-filters with
            // the same gate (so its "eligible" count and skip log stay accurate), so it
            // won't reach this branch for excluded items.
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                _logger.LogDebug("Enqueue skipped: item {ItemId} no longer exists", itemId);
                return;
            }

            if (!DetectionGate.ShouldAutoDetect(item, Config, out var reason, ResolveChannel))
            {
                _logger.LogDebug("Enqueue skipped for {Path}: {Reason}", item.Path, reason);
                return;
            }

            if (item.Path is not null && IsStillBeingWritten(item.Path))
            {
                _logger.LogDebug("Enqueue deferred, still recording: {Path}", item.Path);
                ScheduleRequeue(itemId);
                return;
            }
        }

        if (!_inFlight.TryAdd(itemId, 0))
        {
            return; // already queued or running
        }

        if (!_queue.Writer.TryWrite(itemId))
        {
            _inFlight.TryRemove(itemId, out _);
        }
    }

    /// <summary>True if the item is queued or currently being processed.</summary>
    public bool IsInFlight(Guid itemId) => _inFlight.ContainsKey(itemId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var workerCount = Math.Max(1, Config.MaxConcurrentJobs);
        _logger.LogInformation("Comskip detection manager starting with {Workers} worker(s)", workerCount);

        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = WorkerLoop(stoppingToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        await foreach (var itemId in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ProcessOne(itemId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error detecting commercials for {ItemId}", itemId);
            }
            finally
            {
                _inFlight.TryRemove(itemId, out _);
            }
        }
    }

    private async Task ProcessOne(Guid itemId, CancellationToken ct)
    {
        var cfg = Config;

        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item.Path is null)
        {
            _logger.LogWarning("Item {ItemId} has no resolvable path; skipping", itemId);
            return;
        }

        if (!File.Exists(item.Path))
        {
            _logger.LogWarning("Path missing for {ItemId}: {Path}", itemId, item.Path);
            return;
        }

        // The recording may have started (or restarted, for a re-record) after this item
        // was queued. Never run Comskip on a file that's still being written — a partial
        // .ts yields a partial EDL that would then be stored as a final result.
        if (IsStillBeingWritten(item.Path))
        {
            _logger.LogInformation("Recording still in progress, deferring detection: {Path}", item.Path);
            ScheduleRequeue(itemId);
            return;
        }

        _deferrals.TryRemove(itemId, out _);
        _logger.LogInformation("Running Comskip on {Path}", item.Path);

        var result = await _runner.RunAsync(
            cfg.ComskipPath,
            cfg.ComskipIniPath,
            cfg.WorkDirectory,
            item.Path,
            cfg.PerFileTimeoutMinutes,
            ct).ConfigureAwait(false);

        if (result.Outcome == ComskipOutcome.Failed || result.EdlPath is null)
        {
            // Record as Failed (NOT NoCommercials) so it stays eligible for a retry.
            _store.Save(itemId, new DetectionRecord(DetectionStatus.Failed, Array.Empty<CommercialBreak>(), DateTime.UtcNow));
            return;
        }

        var breaks = EdlParser.Parse(result.EdlPath, cfg.PaddingSeconds);
        var status = breaks.Count > 0 ? DetectionStatus.Detected : DetectionStatus.NoCommercials;
        _store.Save(itemId, new DetectionRecord(status, breaks, DateTime.UtcNow));

        _logger.LogInformation("{Status}: {Count} break(s) for {Path}", status, breaks.Count, item.Path);

        if (cfg.PlaceEdlBesideRecording)
        {
            PlaceEdlBesideRecording(result.EdlPath, item.Path);
        }

        if (cfg.CleanupWorkFiles)
        {
            CleanupWorkFiles(result.EdlPath);
        }

        // Publish immediately: re-run providers for just this item so our stored
        // segments get persisted to Jellyfin without waiting for the next full scan.
        try
        {
            // Resolved here (not in the constructor) to avoid a startup circular dependency.
            // Jellyfin 12: RunSegmentPluginProviders takes the item's LibraryOptions and
            // renamed the flag to forceOverwrite.
            var segmentManager = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
            await segmentManager
                .RunSegmentPluginProviders(item, _libraryManager.GetLibraryOptions(item), forceOverwrite: true, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Detected segments stored but could not trigger immediate publish for {Path}", item.Path);
        }
    }

    /// <summary>
    /// Lazily resolved by <see cref="DetectionGate"/> only when a channel list is
    /// configured. Prefers whatever <see cref="LiveRecordingChannelPoller"/> caught
    /// live, by path, while this item was actively recording; falls back to a by-name
    /// guide lookup for anything it never caught in time.
    /// </summary>
    private ChannelResolver.ChannelInfo? ResolveChannel(BaseItem item, string name)
    {
        if (item.Path is not null && _channelCache.TryGet(item.Path, out var cached))
        {
            return cached;
        }

        if (_serviceProvider.GetService<ILiveTvManager>() is not { } liveTvManager)
        {
            _logger.LogWarning("ILiveTvManager not resolvable; treating channel as unknown for \"{Name}\"", name);
            return null;
        }

        // GetPrograms requires a real UserId (Guid.Empty comes back empty, confirmed
        // against a live server) even though guide data isn't meaningfully user-scoped.
        // Any existing user works; there's no "system" user concept to reach for here.
        var userId = _serviceProvider.GetService<IUserManager>()?.GetUsers().FirstOrDefault()?.Id ?? Guid.Empty;

        return ChannelResolver.Resolve(liveTvManager, name, userId, _logger);
    }

    /// <summary>
    /// True while the file at <paramref name="path"/> is still being recorded. Uses
    /// Jellyfin's own active-recording registry when available; falls back to "modified
    /// in the last 90 seconds" if <see cref="IRecordingsManager"/> can't be resolved.
    /// </summary>
    private bool IsStillBeingWritten(string path)
    {
        try
        {
            if (_serviceProvider.GetService<IRecordingsManager>() is { } recordings)
            {
                return recordings.GetActiveRecordingInfo(path) is not null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Active-recording check failed for {Path}; using mtime fallback", path);
        }

        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(90);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Re-queue an item after <see cref="RequeueDelay"/>, bounded by <see cref="MaxDeferrals"/>.</summary>
    private void ScheduleRequeue(Guid itemId)
    {
        if (!_requeueScheduled.TryAdd(itemId, 0))
        {
            return; // a delayed re-enqueue is already pending for this item
        }

        var attempts = _deferrals.AddOrUpdate(itemId, 1, (_, c) => c + 1);
        if (attempts > MaxDeferrals)
        {
            _logger.LogWarning(
                "Still recording after {Attempts} checks; stopping automatic retries for {ItemId}", attempts, itemId);
            _requeueScheduled.TryRemove(itemId, out _);
            _deferrals.TryRemove(itemId, out _);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RequeueDelay, _stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _requeueScheduled.TryRemove(itemId, out _);
            }

            Enqueue(itemId);
        });
    }

    /// <summary>
    /// Copies the .edl Comskip just produced (still sitting in WorkDirectory at this
    /// point — this must run before <see cref="CleanupWorkFiles"/>, which deletes it
    /// from there) to sit alongside the recording, for external tools that expect a
    /// sibling .edl next to the video file.
    /// </summary>
    private void PlaceEdlBesideRecording(string edlPath, string recordingPath)
    {
        try
        {
            var recordingDir = Path.GetDirectoryName(recordingPath);
            if (recordingDir is null)
            {
                return;
            }

            var destination = Path.Combine(
                recordingDir,
                Path.GetFileNameWithoutExtension(recordingPath) + ".edl");
            File.Copy(edlPath, destination, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not place .edl beside recording {Path}", recordingPath);
        }
    }

    private void CleanupWorkFiles(string edlPath)
    {
        // Remove the .txt/.log clutter next to the .edl; the state we need is now in our store.
        var dir = Path.GetDirectoryName(edlPath);
        var baseName = Path.GetFileNameWithoutExtension(edlPath);
        if (dir is null)
        {
            return;
        }

        foreach (var ext in new[] { ".edl", ".txt", ".log", ".logo.txt", ".ffmeta" })
        {
            try
            {
                var f = Path.Combine(dir, baseName + ext);
                if (File.Exists(f))
                {
                    File.Delete(f);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete work file {Base}{Ext}", baseName, ext);
            }
        }
    }
}
