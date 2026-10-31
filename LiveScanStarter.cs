using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// Tier 1 of real-time-ish commercial detection ("start Comskip at recording-begin"; see
/// project memory for the full investigation): polls what is being recorded right now
/// (<see cref="ActiveRecordings"/>, so a restarted recording's later piece counts too), same as <see cref="LiveRecordingChannelPoller"/>
/// and for the same reason (a DVR recording isn't a library item — and so fires no
/// library events — until after it finishes), and starts a live Comskip process per
/// recording via <see cref="LiveScanCoordinator"/>. When the recording later settles,
/// <see cref="DetectionManager"/> harvests whatever that process already produced
/// instead of scanning the whole finished file from scratch.
///
/// Opt-in (<see cref="PluginConfiguration.EnableLiveScanStart"/>, off by default) because
/// it runs a second Comskip process alongside every in-progress recording for its entire
/// duration.
/// </summary>
public sealed class LiveScanStarter : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Don't bother launching Comskip against a file that's barely started writing —
    /// it has nothing to analyze yet and would likely just exit immediately. The poller
    /// will pick it up on a later tick once there's real data.
    /// </summary>
    private const long MinBytesToStart = 65_536;

    private readonly ILogger<LiveScanStarter> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly LiveScanCoordinator _coordinator;

    public LiveScanStarter(
        ILogger<LiveScanStarter> logger,
        IServiceProvider serviceProvider,
        LiveScanCoordinator coordinator)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _coordinator = coordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Always prune: a job started before the setting was toggled off
                // mid-recording should still be cleaned up eventually rather than
                // orphaned for good.
                _coordinator.PruneStale();

                var cfg = Plugin.Instance?.Configuration;
                if (cfg is not null
                    && cfg.EnableLiveScanStart
                    && !string.IsNullOrWhiteSpace(cfg.ComskipPath)
                    && !string.IsNullOrWhiteSpace(cfg.ComskipIniPath)
                    && !string.IsNullOrWhiteSpace(cfg.WorkDirectory))
                {
                    await PollOnceAsync(cfg, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live scan starter poll failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(PluginConfiguration cfg, CancellationToken cancellationToken)
    {
        foreach (var rec in await ActiveRecordings.ListAsync(_serviceProvider, cancellationToken).ConfigureAwait(false))
        {
            if (_coordinator.IsTracking(rec.Path))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(cfg.RecordingsPath) && !IsUnder(rec.Path, cfg.RecordingsPath))
            {
                continue;
            }

            if (!DetectionGate.ShouldAutoDetectForInProgressRecording(
                    rec.Path, rec.Name, rec.ChannelName, rec.ChannelNumber, cfg, out var reason))
            {
                _logger.LogDebug("Live scan skipped for {Path}: {Reason}", rec.Path, reason);
                continue;
            }

            if (SafeFileLength(rec.Path) < MinBytesToStart)
            {
                continue;
            }

            _coordinator.Start(cfg.ComskipPath, cfg.ComskipIniPath, cfg.WorkDirectory, rec.Path);
        }
    }

    private static long SafeFileLength(string path)
    {
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
}
