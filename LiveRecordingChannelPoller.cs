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
/// Polls what is being recorded right now (<see cref="ActiveRecordings"/>: every file an
/// in-progress timer is writing, a restarted recording's later piece included) and caches
/// its channel into <see cref="RecordingChannelCache"/>, keyed by path.
///
/// Why polling instead of reacting to library events (what
/// <see cref="Jellyfin.Plugin.ComskipSegments.Events.RecordingWatcher"/> does for
/// everything else): confirmed live that Jellyfin does not add a DVR recording to the
/// library — and so never raises ItemAdded/ItemUpdated for it — until AFTER the
/// recording finishes, by which point <c>ActiveRecordingInfo</c> and the recordings-list
/// channel data are both already gone. An event handler can never win that race. A
/// periodic poll of "what's recording right now" has no such timing dependency — it just
/// needs to run at least once during the recording, which even a short (~4 minute) test
/// recording comfortably allows at a 20-second interval.
///
/// Only does any work when a channel allow- or block-list is actually configured, so a
/// default install never touches Live TV for this.
/// </summary>
public sealed class LiveRecordingChannelPoller : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly ILogger<LiveRecordingChannelPoller> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly RecordingChannelCache _channelCache;

    public LiveRecordingChannelPoller(
        ILogger<LiveRecordingChannelPoller> logger,
        IServiceProvider serviceProvider,
        RecordingChannelCache channelCache)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _channelCache = channelCache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (cfg is not null
                    && (!string.IsNullOrEmpty(cfg.ChannelAllowList) || !string.IsNullOrEmpty(cfg.ChannelBlockList)))
                {
                    await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live recording channel poll failed");
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

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var rec in await ActiveRecordings.ListAsync(_serviceProvider, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(rec.ChannelName) && string.IsNullOrEmpty(rec.ChannelNumber))
            {
                continue;
            }

            var channel = new ChannelResolver.ChannelInfo(rec.ChannelName, rec.ChannelNumber);
            if (!_channelCache.TryGet(rec.Path, out _))
            {
                _channelCache.Set(rec.Path, channel);
                _logger.LogInformation(
                    "Captured channel for in-progress recording {Path}: {Name} ({Number})",
                    rec.Path,
                    rec.ChannelName,
                    rec.ChannelNumber);
            }

            if (!_channelCache.TryGetLeadIn(rec.Path, out _) && LeadInOf(rec) is { } leadIn)
            {
                _channelCache.SetLeadIn(rec.Path, channel, leadIn);
                _logger.LogInformation("In-progress recording {Path} started {LeadIn:F1}s before its program", rec.Path, leadIn);
            }
        }
    }

    /// <summary>
    /// The program's start minus when the file was begun (its birth time): the recording's early
    /// start, 0 when it began on time or after (joined in progress, or a restarted piece). Null
    /// when either time isn't known or they're too far apart to trust.
    /// </summary>
    private static double? LeadInOf(ActiveRecordings.Active rec)
    {
        if (rec.ProgramStartUtc is not { } program)
        {
            return null;
        }

        // .NET reads the modify time as a file's creation time on Linux; stat has the birth time.
        if (BirthUtc(rec.Path) is not { } born)
        {
            return null;
        }

        var early = (program - born).TotalSeconds;
        return early > 15 * 60 || early < -12 * 3600 ? null : Math.Max(0, Math.Round(early, 1));
    }

    private static DateTime? BirthUtc(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("stat") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("%W");
            psi.ArgumentList.Add(path);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return long.TryParse(text, out var unix) && unix > 946684800 ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
