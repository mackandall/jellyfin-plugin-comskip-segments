using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
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
    private readonly LiveScanCoordinator _liveScanCoordinator;
    private readonly AudioTrimService _audioTrim;
    private readonly ProgramStart.ProgramStartService _programStart;
    private readonly CutTracker _cuts;

    private readonly ScanQueue _queue;
    private readonly IReadOnlyList<QueueEntry> _restore;

    // Waiting (in _queue) or running (in _running): one of each item at a time.
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    // Per worker number, the scan it's on.
    private readonly ConcurrentDictionary<int, RunningScan> _running = new();

    // In-progress-recording guard: an item enqueued while its .ts is still being written
    // (e.g. a backstop scan or Media Segment Scan that overlaps a recording) is not run
    // now — it's re-checked after RequeueDelay, up to MaxDeferrals times. _requeueScheduled
    // keeps that to one pending timer per item.
    private static readonly TimeSpan RequeueDelay = TimeSpan.FromMinutes(2);
    private const int MaxDeferrals = 45; // ~90 min ceiling before we stop chasing a file
    private readonly ConcurrentDictionary<Guid, int> _deferrals = new();
    private readonly ConcurrentDictionary<Guid, Deferred> _requeueScheduled = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _deferredSince = new();
    private readonly ConcurrentDictionary<Guid, byte> _republishPending = new();

    // Long enough for Jellyfin to finish re-creating an item (and its siblings) before the
    // stored breaks are published back onto it.
    private static readonly TimeSpan RepublishDelay = TimeSpan.FromSeconds(15);

    // A sibling .edl whose mtime is this close to a record's time was written by our own
    // "place .edl beside recording" step (used to recognise files written before we
    // started keeping a list of them).
    private static readonly TimeSpan OwnedEdlTolerance = TimeSpan.FromMinutes(10);
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
        RecordingChannelCache channelCache,
        LiveScanCoordinator liveScanCoordinator,
        AudioTrimService audioTrim,
        ProgramStart.ProgramStartService programStart,
        CutTracker cuts,
        IApplicationPaths applicationPaths)
    {
        _queue = new ScanQueue(logger, applicationPaths);
        _restore = _queue.LoadSaved();
        _queue.SaveAlso(SavedOutsideQueue);
        _programStart = programStart;
        _cuts = cuts;
        _logger = logger;
        _libraryManager = libraryManager;
        _serviceProvider = serviceProvider;
        _runner = runner;
        _store = store;
        _channelCache = channelCache;
        _liveScanCoordinator = liveScanCoordinator;
        _audioTrim = audioTrim;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>What <see cref="Enqueue"/> did with an item.</summary>
    public enum EnqueueResult
    {
        /// <summary>Queued for Comskip (or already queued / deferred while still recording).</summary>
        Queued,

        /// <summary>Another program's .edl beside the video was used; no Comskip run needed.</summary>
        ImportedEdl,

        /// <summary>A completed result already exists (its segments are republished if missing).</summary>
        AlreadyDone,

        /// <summary>Not eligible: gone, outside the recordings folders, or filtered by the show/channel lists.</summary>
        Skipped
    }

    /// <summary>
    /// Gets commercial breaks for an item: from an .edl another program (SageTV, Channels
    /// DVR, Emby) already left beside the video if there is one — always preferred, for
    /// automatic and forced requests alike — otherwise by queueing a Comskip run.
    /// Automatic (non-forced) Comskip runs are limited to files under the recordings
    /// folder / scan paths and to the show/channel lists; a forced request (the item menu,
    /// the config page) scans any file.
    /// </summary>
    /// <param name="reason">Why (a <see cref="ScanReason"/>); shown on the Queue tab. Default: manual when forced, else library check.</param>
    public EnqueueResult Enqueue(Guid itemId, bool force = false, string? reason = null)
    {
        reason ??= force ? ScanReason.Manual : ScanReason.LibraryCheck;
        if (itemId.Equals(Guid.Empty))
        {
            return EnqueueResult.Skipped;
        }

        if (!force && _store.IsCompleted(itemId))
        {
            RepublishIfMissing(itemId);
            return EnqueueResult.AlreadyDone;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogDebug("Enqueue skipped: item {ItemId} no longer exists", itemId);
            return EnqueueResult.Skipped;
        }

        if (TryImportSiblingEdl(item, out _))
        {
            _ = Task.Run(() => PublishAsync(item, forceOverwrite: true, _stoppingToken));
            return EnqueueResult.ImportedEdl;
        }

        if (!force)
        {
            // Single chokepoint for automatic runs: the watcher and the media-segment
            // provider both land here. LibraryScanTask pre-filters with the same checks
            // (so its "eligible" count and skip log stay accurate).
            if (!IsInAutoScope(item.Path))
            {
                _logger.LogDebug("Enqueue skipped for {Path}: outside the recordings folder and scan paths", item.Path);
                return EnqueueResult.Skipped;
            }

            if (!DetectionGate.ShouldAutoDetect(item, Config, out var skipReason, ResolveChannel))
            {
                _logger.LogDebug("Enqueue skipped for {Path}: {Reason}", item.Path, skipReason);
                // Commercial-free channels are kept from Comskip, but their promos before the
                // program are exactly what a remembered card finds.
                if (item.Path is not null && !IsStillBeingWritten(item.Path) && _programStart.WantsAutomatic(item))
                {
                    _ = Task.Run(() => AutoProgramStart(item, _stoppingToken));
                }

                return EnqueueResult.Skipped;
            }

            if (item.Path is not null && IsStillBeingWritten(item.Path))
            {
                _logger.LogDebug("Enqueue deferred, still recording: {Path}", item.Path);
                ScheduleRequeue(new QueueEntry(itemId, reason, DateTime.UtcNow, force));
                return EnqueueResult.Queued;
            }
        }

        if (!_inFlight.TryAdd(itemId, 0))
        {
            return EnqueueResult.Queued; // already queued or running
        }

        _queue.Add(new QueueEntry(itemId, reason, DateTime.UtcNow, force));
        return EnqueueResult.Queued;
    }

    /// <summary>
    /// True if <paramref name="path"/> is under the recordings folder or one of the scan
    /// paths — the only places Comskip runs automatically. Archives added from elsewhere
    /// (NAS shares) get their breaks from an existing .edl, or from a manual scan.
    /// </summary>
    public static bool IsInAutoScope(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var cfg = Config;
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(cfg.RecordingsPath))
        {
            roots.Add(cfg.RecordingsPath);
        }

        roots.AddRange((cfg.ScanPaths ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return roots.Any(root =>
        {
            var r = root.TrimEnd('/') + "/";
            return path.StartsWith(r, StringComparison.Ordinal);
        });
    }

    /// <summary>The .edl another program would leave beside <paramref name="videoPath"/> (same name, .edl extension).</summary>
    public static string? SiblingEdlPath(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        return dir is null ? null : Path.Combine(dir, Path.GetFileNameWithoutExtension(videoPath) + ".edl");
    }

    /// <summary>
    /// If another program (SageTV, Channels DVR, Emby, a standalone Comskip) already left an
    /// .edl beside the video, stores its breaks as this item's result instead of running
    /// Comskip. An .edl this plugin wrote itself is not "another program's" and is ignored
    /// here. Does not publish; callers do (or, for the segment provider, return the breaks).
    /// </summary>
    public bool TryImportSiblingEdl(BaseItem item, out DetectionRecord record)
    {
        record = null!;
        if (item is not Video || string.IsNullOrEmpty(item.Path) || !item.IsFileProtocol
            || SiblingEdlPath(item.Path) is not { } edlPath)
        {
            return false;
        }

        try
        {
            if (!File.Exists(edlPath) || _store.IsOwnedEdl(edlPath))
            {
                return false;
            }

            var breaks = EdlParser.Parse(edlPath, Config.PaddingSeconds);
            var status = breaks.Count > 0 ? DetectionStatus.Detected : DetectionStatus.NoCommercials;
            record = new DetectionRecord(status, breaks, DateTime.UtcNow, DetectionRecord.SourceEdl);
            _store.Save(item.Id, record);
            _logger.LogInformation("Using the existing .edl beside {Path}: {Count} break(s)", item.Path, breaks.Count);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _logger.LogWarning(ex, "Could not read the .edl beside {Path}; falling back to Comskip rules", item.Path);
            return false;
        }
    }

    /// <summary>
    /// Puts an item's stored breaks back into Jellyfin if they went missing. Jellyfin
    /// deletes an item's segments whenever it deletes the item, and it deletes and re-creates
    /// every item of a library when the library is renamed or its folders edited — seen
    /// live: renaming "Recorded Shows" to "TV Shows" wiped the segments of every recording
    /// while our records stayed, so nothing showed until the next Media Segment Scan.
    /// Asking the providers again without overwrite is a no-op when nothing is missing.
    /// </summary>
    public void RepublishIfMissing(Guid itemId)
    {
        if (!_store.TryGet(itemId, out var record) || record.Status != DetectionStatus.Detected
            || !_republishPending.TryAdd(itemId, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RepublishDelay, _stoppingToken).ConfigureAwait(false);
                if (_libraryManager.GetItemById(itemId) is BaseItem item)
                {
                    await PublishAsync(item, forceOverwrite: false, _stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _republishPending.TryRemove(itemId, out _);
            }
        });
    }

    /// <summary>Looks for a remembered program-start card; true when it trimmed the file.</summary>
    private async Task<bool> AutoProgramStart(BaseItem item, CancellationToken ct)
    {
        try
        {
            var r = await _programStart.FindAutomaticAsync(item, ct).ConfigureAwait(false);
            return r.Trimmed;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Program-start search failed for {Path}", item.Path);
            return false;
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Video)
        {
            return;
        }

        var item = e.Item;
        _ = Task.Run(() => ImportAlternatesAsync(item));

        if (_store.TryGet(item.Id, out _))
        {
            RepublishIfMissing(item.Id);
            return;
        }

        // A recording moved to another folder (Space's sorting, Move to archive) comes back as
        // a new item with our .edl beside it: import it now rather than at the next daily
        // Media Segment Scan. Off the event thread: the File.Exists may be on a NAS.
        _ = Task.Run(() =>
        {
            if (TryImportSiblingEdl(item, out _))
            {
                return PublishAsync(item, forceOverwrite: true, _stoppingToken);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The other versions of a newly added video (pieces of a recording split by a restart, a
    /// re-record beside the old copy) raise no ItemAdded of their own: Jellyfin attaches them to
    /// the main version in the same refresh. Each one moved in with an .edl beside it gets it too.
    /// </summary>
    private async Task ImportAlternatesAsync(BaseItem item)
    {
        try
        {
            await Task.Delay(RepublishDelay, _stoppingToken).ConfigureAwait(false); // until they're attached
            var alternates = _libraryManager.GetItemList(new InternalItemsQuery
            {
                OwnerIds = new[] { item.Id },
                IncludeOwnedItems = true,
                IsFolder = false
            }).OfType<Video>();
            foreach (var alternate in alternates)
            {
                if (!_store.TryGet(alternate.Id, out _) && TryImportSiblingEdl(alternate, out _))
                {
                    await PublishAsync(alternate, forceOverwrite: true, _stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Looking for other versions of {Path} failed", item.Path);
        }
    }

    /// <summary>
    /// Re-runs Jellyfin's segment providers for one item so our stored breaks are persisted
    /// without waiting for the next Media Segment Scan. forceOverwrite replaces what is there
    /// (a new result); without it Jellyfin only writes when our segments changed or are missing.
    /// </summary>
    private async Task PublishAsync(BaseItem item, bool forceOverwrite, CancellationToken ct)
    {
        try
        {
            // Resolved here (not in the constructor) to avoid a startup circular dependency.
            var segmentManager = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
            await segmentManager
                .RunSegmentPluginProviders(item, _libraryManager.GetLibraryOptions(item), forceOverwrite, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Commercial breaks stored but could not be published for {Path}", item.Path);
        }
    }

    /// <summary>
    /// Records every .edl this plugin already placed beside a recording before it started
    /// keeping a list of them (records without a Source), so those are recognised as ours:
    /// replaceable by a new scan, never imported as another program's.
    /// </summary>
    private void SeedOwnedEdls()
    {
        var owned = new List<string>();
        foreach (var (itemId, record) in _store.All())
        {
            if (record.Source is not null || _libraryManager.GetItemById(itemId) is not BaseItem item
                || string.IsNullOrEmpty(item.Path) || SiblingEdlPath(item.Path) is not { } edlPath)
            {
                continue;
            }

            try
            {
                if (File.Exists(edlPath)
                    && (File.GetLastWriteTimeUtc(edlPath) - record.UpdatedUtc).Duration() <= OwnedEdlTolerance)
                {
                    owned.Add(edlPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var added = _store.MarkOwnedEdls(owned);
        if (added > 0)
        {
            _logger.LogInformation("Recognised {Count} .edl file(s) beside recordings as written by this plugin", added);
        }
    }

    /// <summary>True if the item is queued or currently being processed.</summary>
    public bool IsInFlight(Guid itemId) => _inFlight.ContainsKey(itemId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var workerCount = Math.Max(1, Config.MaxConcurrentJobs);
        _logger.LogInformation("Comskip detection manager starting with {Workers} worker(s)", workerCount);

        _libraryManager.ItemAdded += OnItemAdded;
        _ = Task.Run(SeedOwnedEdls, stoppingToken);
        RestoreSavedQueue();

        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = WorkerLoop(i + 1, stoppingToken);
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            _libraryManager.ItemAdded -= OnItemAdded;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Saved before the running scans are cancelled, so they're still in the list.
        _queue.Freeze();
        return base.StopAsync(cancellationToken);
    }

    /// <summary>Puts back what was queued when the server stopped: running scans first, then the waiting ones in order.</summary>
    private void RestoreSavedQueue()
    {
        var restored = 0;
        foreach (var e in _restore)
        {
            if (_libraryManager.GetItemById(e.Id) is null || (!e.Force && _store.IsCompleted(e.Id)) || !_inFlight.TryAdd(e.Id, 0))
            {
                continue;
            }

            _queue.Add(e);
            restored++;
        }

        if (restored > 0)
        {
            _logger.LogInformation("Put {Count} scan(s) back in the queue from before the restart", restored);
        }
    }

    private async Task WorkerLoop(int worker, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QueueEntry entry;
            try
            {
                entry = await _queue.TakeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using var job = new RunningScan(entry, worker);
            _running[worker] = job;
            _queue.Changed();
            FinishedEntry? done;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Cancel.Token);
                done = await ProcessOne(job, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // the server is stopping: it stays in the saved queue and runs again first
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Scan cancelled from the Queue tab: {Name}", job.Name);
                done = Done(job, "stopped", 0, _store.TryGet(entry.Id, out _) ? "Kept the breaks it had" : null, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error detecting commercials for {ItemId}", entry.Id);
                done = Done(job, "failed", 0, ex.Message, false);
            }

            _running.TryRemove(worker, out _);
            _inFlight.TryRemove(entry.Id, out _);
            if (done is not null)
            {
                _queue.Finished(done);
            }
            else
            {
                _queue.Changed();
            }
        }
    }

    private static FinishedEntry Done(RunningScan job, string result, int breaks, string? detail, bool comskip) =>
        new(job.Entry.Id, job.Name, job.Entry.Reason, result, breaks, detail, job.StartedUtc, DateTime.UtcNow, comskip);

    /// <summary>"FBI · S02E04 · Predator", or the name.</summary>
    public static string Describe(BaseItem item) =>
        item is Episode e && !string.IsNullOrEmpty(e.SeriesName)
            ? e.SeriesName + " · " + (e.ParentIndexNumber is { } sn && e.IndexNumber is { } en ? $"S{sn:00}E{en:00} · " : string.Empty) + e.Name
            : item.Name;

    // What must be saved besides the waiting list: running scans (first) and those waiting for a recording to end.
    private IReadOnlyList<QueueEntry> SavedOutsideQueue() =>
        _running.Values.OrderBy(j => j.StartedUtc).Select(j => j.Entry)
            .Concat(_requeueScheduled.Values.Select(d => d.Entry))
            .ToList();

    /// <summary>A scan a worker is on.</summary>
    public sealed class RunningScan : IDisposable
    {
        public RunningScan(QueueEntry entry, int worker)
        {
            Entry = entry;
            Worker = worker;
        }

        public QueueEntry Entry { get; }

        public int Worker { get; }

        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        public CancellationTokenSource Cancel { get; } = new();

        public string Name { get; set; } = string.Empty;

        public string? Path { get; set; }

        /// <summary>starting, audio, program-start, comskip, saving.</summary>
        public string Stage { get; set; } = "starting";

        public int? Pid { get; set; }

        public DateTime? ComskipStartedUtc { get; set; }

        /// <summary>The time Comskip has actually run (not while paused).</summary>
        public System.Diagnostics.Stopwatch ComskipActive { get; } = new();

        /// <summary>When it was paused (null: not paused).</summary>
        public DateTime? PausedUtc { get; private set; }

        /// <summary>Only Comskip itself can be paused, not the audio or program-start checks before it.</summary>
        public bool CanPause => Pid is not null && OperatingSystem.IsLinux();

        private readonly object _pauseGate = new();

        /// <summary>Freezes Comskip where it is (SIGSTOP); <see cref="Resume"/> lets it carry on.</summary>
        public bool Pause()
        {
            lock (_pauseGate)
            {
                if (PausedUtc is not null || Pid is not { } pid || !OperatingSystem.IsLinux() || Signal(pid, SigStop) != 0)
                {
                    return false;
                }

                ComskipActive.Stop();
                PausedUtc = DateTime.UtcNow;
                return true;
            }
        }

        public bool Resume()
        {
            lock (_pauseGate)
            {
                if (PausedUtc is null)
                {
                    return false;
                }

                if (Pid is { } pid)
                {
                    Signal(pid, SigCont);
                }

                ComskipActive.Start();
                PausedUtc = null;
                return true;
            }
        }

        public void Dispose() => Cancel.Dispose();

        private const int SigCont = 18; // Linux (x86-64, arm64)
        private const int SigStop = 19;

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int Signal(int pid, int sig);
    }

    /// <summary>Waiting for its recording to end; checked again every couple of minutes.</summary>
    public sealed record Deferred(QueueEntry Entry, CancellationTokenSource Cancel);

    /// <summary>Everything the Queue tab shows.</summary>
    public sealed record QueueSnapshot(
        int Workers,
        IReadOnlyList<RunningScan> Running,
        IReadOnlyList<QueueEntry> Waiting,
        IReadOnlyList<(QueueEntry Entry, DateTime Since)> Recording,
        IReadOnlyList<FinishedEntry> History);

    public QueueSnapshot Snapshot() => new(
        Math.Max(1, Config.MaxConcurrentJobs),
        _running.Values.OrderBy(j => j.Worker).ToList(),
        _queue.Waiting(),
        _requeueScheduled.Values
            .Select(d => (d.Entry, _deferredSince.TryGetValue(d.Entry.Id, out var since) ? since : d.Entry.QueuedUtc))
            .OrderBy(d => d.Item2)
            .ToList(),
        _queue.History());

    /// <summary>Cancels a running scan (the recording keeps the breaks it had). False if it isn't running.</summary>
    public bool Cancel(Guid itemId)
    {
        var job = _running.Values.FirstOrDefault(j => j.Entry.Id == itemId);
        if (job is null)
        {
            return false;
        }

        job.Cancel.Cancel(); // a paused Comskip is killed all the same
        return true;
    }

    /// <summary>Pauses (or resumes) a running Comskip. False if it isn't running Comskip.</summary>
    public bool PauseScan(Guid itemId, bool pause)
    {
        var job = _running.Values.FirstOrDefault(j => j.Entry.Id == itemId);
        if (job is null)
        {
            return false;
        }

        var done = pause ? job.Pause() : job.Resume();
        if (done)
        {
            _logger.LogInformation("Scan {Action} from the Queue tab: {Name}", pause ? "paused" : "resumed", job.Name);
        }

        return done;
    }

    /// <summary>Paused: nothing new starts; running scans go on. Kept across restarts.</summary>
    public bool QueuePaused => _queue.Paused;

    public void PauseQueue(bool pause)
    {
        _queue.SetPaused(pause);
        _logger.LogInformation("Comskip queue {State} from the Queue tab", pause ? "paused" : "resumed");
    }

    /// <summary>Moves a waiting scan to the top: it runs when the next worker is free.</summary>
    public bool ScanNext(Guid itemId) => _queue.MoveFirst(itemId);

    /// <summary>Takes waiting scans (and those waiting for their recording to end) out of the queue; returns how many.</summary>
    public int Remove(Func<QueueEntry, bool> match)
    {
        var removed = _queue.Remove(match);
        foreach (var e in removed)
        {
            _inFlight.TryRemove(e.Id, out _);
        }

        var count = removed.Count;
        foreach (var d in _requeueScheduled.Values.Where(d => match(d.Entry)).ToList())
        {
            if (_requeueScheduled.TryRemove(d.Entry.Id, out _))
            {
                try
                {
                    d.Cancel.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Its wait just ended; the re-check sees it's no longer listed.
                }

                _deferrals.TryRemove(d.Entry.Id, out _);
                _deferredSince.TryRemove(d.Entry.Id, out _);
                count++;
            }
        }

        _queue.Changed();
        return count;
    }

    /// <summary>How far Comskip has read the file (0–1), from the position of its open file; null if not known.</summary>
    public static double? Progress(RunningScan job)
    {
        if (job.Pid is not { } pid || job.Path is not { } path || !OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var size = new FileInfo(path).Length;
            if (size <= 0)
            {
                return null;
            }

            foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
            {
                var target = new FileInfo(fd).LinkTarget;
                if (!string.Equals(target, path, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var line in File.ReadLines($"/proc/{pid}/fdinfo/{System.IO.Path.GetFileName(fd)}"))
                {
                    if (line.StartsWith("pos:", StringComparison.Ordinal)
                        && long.TryParse(line.AsSpan(4).Trim(), out var pos))
                    {
                        return Math.Clamp((double)pos / size, 0, 1);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Comskip just ended, or /proc can't be read.
        }

        return null;
    }

    private DateTime _lastNotInstalledWarning = DateTime.MinValue;

    /// <summary>Runs one scan; what to show under Recently finished (null: not finished, e.g. still recording).</summary>
    private async Task<FinishedEntry?> ProcessOne(RunningScan job, CancellationToken ct)
    {
        var cfg = Config;
        var itemId = job.Entry.Id;

        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item.Path is null)
        {
            _logger.LogWarning("Item {ItemId} has no resolvable path; skipping", itemId);
            job.Name = "(no longer in the library)";
            return Done(job, "skipped", 0, "No longer in the library", false);
        }

        job.Name = Describe(item);
        job.Path = item.Path;
        if (!File.Exists(item.Path))
        {
            _logger.LogWarning("Path missing for {ItemId}: {Path}", itemId, item.Path);
            return Done(job, "skipped", 0, "The file isn't there", false);
        }

        // The recording may have started (or restarted, for a re-record) after this item
        // was queued. Never run Comskip on a file that's still being written — a partial
        // .ts yields a partial EDL that would then be stored as a final result.
        if (IsStillBeingWritten(item.Path))
        {
            _logger.LogInformation("Recording still in progress, deferring detection: {Path}", item.Path);
            ScheduleRequeue(job.Entry);
            return null;
        }

        _deferrals.TryRemove(itemId, out _);
        _deferredSince.TryRemove(itemId, out _);

        // Audio-format trim runs BEFORE Comskip, and only as part of the automatic
        // pipeline here (the manual item-menu/config-page path calls AudioTrimService
        // directly). Running it first means Comskip always scans the FINAL file, so the
        // resulting break timestamps are correct from the start — no separate
        // invalidate-and-rerun step is needed for this, first-run, path.
        var audioTrimApplied = false;
        if (cfg.EnableAudioFormatTrim && cfg.AudioTrimAutomaticApply)
        {
            job.Stage = "audio";
            try
            {
                // Claimed like a manual cut: shows as "Cutting" in the apps, and nothing else
                // rewrites the file meanwhile.
                AudioTrimApplyResult trimResult;
                if (_cuts.TryStart(item, "audio-format", out var cutJob))
                {
                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cutJob.Cancel.Token);
                        trimResult = await _audioTrim.ApplyIfNeededAsync(item, cfg, force: false, linked.Token, cutJob).ConfigureAwait(false);
                    }
                    finally
                    {
                        _cuts.Finish(cutJob);
                    }
                }
                else
                {
                    trimResult = AudioTrimApplyResult.Skipped;
                }

                if (trimResult.Trimmed)
                {
                    audioTrimApplied = true;
                    _logger.LogInformation(
                        "Audio-format trim applied automatically to {Path} (-{Lead:F1}s lead, -{Trail:F1}s trail) before commercial detection",
                        item.Path, trimResult.LeadTrimSeconds, trimResult.TrailTrimSeconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic audio-format trim failed for {Path}; continuing with commercial detection on the original file", item.Path);
            }
        }

        // A remembered program-start card (after the audio check: that one already fixes the
        // start where the audio format changes). Before Comskip, so a trim comes first.
        if (!audioTrimApplied && _programStart.WantsAutomatic(item))
        {
            job.Stage = "program-start";
            if (await AutoProgramStart(item, ct).ConfigureAwait(false))
            {
                audioTrimApplied = true; // the timeline moved: same handling as an audio trim
            }
        }

        ct.ThrowIfCancellationRequested(); // stopped from the Queue tab during the checks above

        // If a live Comskip process has been tailing this recording since it started
        // (see LiveScanStarter / LiveScanCoordinator), harvest whatever it already
        // produced instead of scanning the whole finished file over again. Not gated on
        // the current EnableLiveScanStart value — a no-op if nothing is tracked, and
        // correctly still benefits from a job started before the setting was toggled off
        // mid-recording, rather than wasting it.
        //
        // Always called even when a trim just ran (never skipped outright) — TryHarvest
        // is also what kills/untracks the live process for this path; skipping it would
        // leave that job running against an already-finished recording for up to
        // LiveScanCoordinator's 12-hour stale ceiling. Its result IS discarded in that
        // case: the live process tailed the ORIGINAL, pre-trim bytes, so its EDL's
        // offsets no longer line up with the file now sitting at this path.
        var harvestedEdlPath = _liveScanCoordinator.TryHarvest(item.Path);
        if (audioTrimApplied && harvestedEdlPath is not null)
        {
            _logger.LogInformation("Discarding live-harvested EDL for {Path}: audio-trim shifted the timeline, forcing a fresh scan", item.Path);
            harvestedEdlPath = null;
        }

        ComskipRunResult result;
        if (harvestedEdlPath is not null)
        {
            result = new ComskipRunResult(ComskipOutcome.Ran, harvestedEdlPath, string.Empty);
        }
        else if (!ComskipInstaller.IsInstalled(cfg))
        {
            // Nothing to run yet: leave the item unscanned (not Failed), so it's picked up once
            // Comskip is installed, and say so in the log now and then rather than per item.
            if (DateTime.UtcNow - _lastNotInstalledWarning > TimeSpan.FromMinutes(10))
            {
                _lastNotInstalledWarning = DateTime.UtcNow;
                _logger.LogWarning("{Message}", ComskipInstaller.NotInstalledMessage);
            }

            return Done(job, "skipped", 0, "Comskip isn't installed (Settings)", false);
        }
        else
        {
            _logger.LogInformation("Running Comskip on {Path}", item.Path);
            job.Stage = "comskip";
            job.ComskipStartedUtc = DateTime.UtcNow;
            result = await _runner.RunAsync(
                cfg.ComskipPath,
                cfg.ComskipIniPath,
                cfg.WorkDirectory,
                item.Path,
                cfg.PerFileTimeoutMinutes,
                ct,
                pid =>
                {
                    job.Pid = pid;
                    job.ComskipActive.Start();
                },
                () => job.ComskipActive.Elapsed).ConfigureAwait(false);
            job.Resume(); // only if it was paused when Comskip ended (it can't, but never leave it marked so)
            job.Pid = null;
            job.ComskipActive.Stop();
        }

        job.Stage = "saving";
        var ranComskip = harvestedEdlPath is null;
        if (result.Outcome == ComskipOutcome.Failed || result.EdlPath is null)
        {
            // Record as Failed (NOT NoCommercials) so it stays eligible for a retry.
            _store.Save(itemId, new DetectionRecord(DetectionStatus.Failed, Array.Empty<CommercialBreak>(), DateTime.UtcNow, DetectionRecord.SourceComskip));
            return Done(job, "failed", 0, result.Error ?? "Comskip failed", false);
        }

        var breaks = EdlParser.Parse(result.EdlPath, cfg.PaddingSeconds);
        var status = breaks.Count > 0 ? DetectionStatus.Detected : DetectionStatus.NoCommercials;
        _store.Save(itemId, new DetectionRecord(status, breaks, DateTime.UtcNow, DetectionRecord.SourceComskip));

        _logger.LogInformation("{Status}: {Count} break(s) for {Path}", status, breaks.Count, item.Path);

        if (cfg.PlaceEdlBesideRecording)
        {
            PlaceEdlBesideRecording(result.EdlPath, item.Path);
        }

        if (cfg.CleanupWorkFiles)
        {
            CleanupWorkFiles(result.EdlPath);
        }

        // Publish immediately so the new breaks don't wait for the next Media Segment Scan.
        await PublishAsync(item, forceOverwrite: true, ct).ConfigureAwait(false);
        return Done(
            job,
            breaks.Count > 0 ? "breaks" : "none",
            breaks.Count,
            ranComskip ? null : "From the scan that ran while it recorded",
            ranComskip && job.ComskipStartedUtc is not null);
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
    private void ScheduleRequeue(QueueEntry entry)
    {
        var itemId = entry.Id;
        var deferred = new Deferred(entry, CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken));
        if (!_requeueScheduled.TryAdd(itemId, deferred))
        {
            deferred.Cancel.Dispose();
            return; // a delayed re-enqueue is already pending for this item
        }

        _deferredSince.TryAdd(itemId, DateTime.UtcNow);
        var attempts = _deferrals.AddOrUpdate(itemId, 1, (_, c) => c + 1);
        if (attempts > MaxDeferrals)
        {
            _logger.LogWarning(
                "Still recording after {Attempts} checks; stopping automatic retries for {ItemId}", attempts, itemId);
            _requeueScheduled.TryRemove(itemId, out _);
            _deferrals.TryRemove(itemId, out _);
            _deferredSince.TryRemove(itemId, out _);
            deferred.Cancel.Dispose();
            return;
        }

        _queue.Changed();
        _ = Task.Run(async () =>
        {
            bool owned;
            try
            {
                await Task.Delay(RequeueDelay, deferred.Cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Removed from the Queue tab, or the server is stopping.
            }
            finally
            {
                // Only our own entry: removed from the Queue tab means it's gone already (and maybe deferred anew since).
                owned = _requeueScheduled.TryRemove(new KeyValuePair<Guid, Deferred>(itemId, deferred));
                deferred.Cancel.Dispose();
            }

            if (!owned || _stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (Enqueue(itemId, entry.Force, entry.Reason) != EnqueueResult.Queued || !_requeueScheduled.ContainsKey(itemId))
            {
                _deferredSince.TryRemove(itemId, out _);
            }
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
            if (File.Exists(destination) && !_store.IsOwnedEdl(destination))
            {
                _logger.LogInformation("Keeping the existing .edl beside {Path}: another program wrote it", recordingPath);
                return;
            }

            File.Copy(edlPath, destination, overwrite: true);
            _store.MarkOwnedEdl(destination);
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
