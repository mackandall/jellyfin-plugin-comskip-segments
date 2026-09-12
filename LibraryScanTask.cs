using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using Jellyfin.Plugin.ComskipSegments.Detection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Tasks;

/// <summary>
/// The library-scan front door. A scheduled task (also runnable on demand from
/// Dashboard -> Scheduled Tasks) that walks the configured folders, finds video items
/// not yet processed, and drops them into the same detection queue the DVR path uses.
///
/// Deliberately opt-in and path-scoped: running Comskip across an entire movie library
/// is wasteful and invites false positives, so it only touches the recordings folder
/// plus whatever extra paths you explicitly whitelist in the plugin settings.
/// </summary>
public sealed class LibraryScanTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly DetectionManager _manager;
    private readonly DetectionStore _store;
    private readonly RecordingChannelCache _channelCache;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LibraryScanTask> _logger;

    public LibraryScanTask(
        ILibraryManager libraryManager,
        DetectionManager manager,
        DetectionStore store,
        RecordingChannelCache channelCache,
        IServiceProvider serviceProvider,
        ILogger<LibraryScanTask> logger)
    {
        _libraryManager = libraryManager;
        _manager = manager;
        _store = store;
        _channelCache = channelCache;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public string Name => "Scan library for commercials (Comskip)";
    public string Key => "ComskipSegmentsLibraryScan";
    public string Description => "Runs Comskip on eligible recordings/files that don't yet have commercial segments.";
    public string Category => "Comskip Commercial Segments";

    // Runs on an interval as a backstop for the DVR watcher: a finished recording that
    // lands over an already-existing episode raises ItemUpdated, not ItemAdded, and can
    // slip past RecordingWatcher. This periodic sweep re-enqueues anything eligible that
    // still has no completed detection record. Interval comes from plugin config
    // (AutoScanIntervalHours); 0 disables it. Cheap: it only enqueues, and Enqueue()
    // no-ops for items already done or in flight.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (!cfg.EnableBackstopScan || cfg.AutoScanIntervalHours <= 0)
        {
            yield break;
        }

        var hours = cfg.AutoScanIntervalHours;

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(hours).Ticks
        };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(cfg.RecordingsPath))
        {
            roots.Add(cfg.RecordingsPath);
        }

        roots.AddRange(
            (cfg.ScanPaths ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (roots.Count == 0)
        {
            _logger.LogInformation("No scan paths configured; nothing to do");
            progress.Report(100);
            return Task.CompletedTask;
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video },
            MediaTypes = new[] { MediaType.Video },
            Recursive = true,
            IsVirtualItem = false
        };

        var all = _libraryManager.GetItemList(query);

        var inScope = all
            .Where(i => i.Path is not null
                        && roots.Any(r => IsUnder(i.Path!, r))
                        && !_store.IsCompleted(i.Id))
            .ToList();

        var eligible = new List<BaseItem>(inScope.Count);
        var filtered = 0;
        foreach (var item in inScope)
        {
            if (DetectionGate.ShouldAutoDetect(item, cfg, out var reason, ResolveChannel))
            {
                eligible.Add(item);
            }
            else
            {
                filtered++;
                _logger.LogDebug("Library scan skipping {Path}: {Reason}", item.Path, reason);
            }
        }

        _logger.LogInformation(
            "Library scan: {Count} eligible item(s) to detect ({Filtered} excluded by allow/block list)",
            eligible.Count,
            filtered);

        var done = 0;
        foreach (var item in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _manager.Enqueue(item.Id);
            done++;
            progress.Report(eligible.Count == 0 ? 100 : (double)done / eligible.Count * 100);
        }

        // We only enqueue here; the manager's workers do the heavy lifting off-task.
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Lazily resolved by <see cref="DetectionGate"/> only when a channel list is
    /// configured. Prefers whatever <see cref="LiveRecordingChannelPoller"/> caught
    /// live, by path, while this item was actively recording; falls back to a by-name
    /// guide lookup otherwise.
    /// </summary>
    private ChannelResolver.ChannelInfo? ResolveChannel(BaseItem item, string name)
    {
        if (item.Path is not null && _channelCache.TryGet(item.Path, out var cached))
        {
            return cached;
        }

        if (_serviceProvider.GetService<ILiveTvManager>() is not { } liveTvManager)
        {
            return null;
        }

        var userId = _serviceProvider.GetService<IUserManager>()?.GetUsers().FirstOrDefault()?.Id ?? Guid.Empty;

        return ChannelResolver.Resolve(liveTvManager, name, userId, _logger);
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.Ordinal);
    }
}
