using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// Polls Jellyfin's Live TV recordings list for anything currently in progress and caches
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

    private static readonly DtoOptions RecordingDtoOptions = new(false)
    {
        Fields = new[] { ItemFields.Path, ItemFields.ChannelInfo }
    };

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
        if (_serviceProvider.GetService<ILiveTvManager>() is not { } liveTvManager)
        {
            return;
        }

        // Requires a real UserId (Guid.Empty comes back empty, confirmed against a live
        // server) even though recordings aren't meaningfully user-scoped.
        var userId = _serviceProvider.GetService<IUserManager>()?.GetUsers().FirstOrDefault()?.Id ?? Guid.Empty;

        var query = new RecordingQuery
        {
            UserId = userId,
            IsInProgress = true,
            EnableImages = false,
            Limit = 200
        };

        var result = await liveTvManager.GetRecordingsAsync(query, RecordingDtoOptions).ConfigureAwait(false);

        foreach (var dto in result.Items)
        {
            if (string.IsNullOrEmpty(dto.Path))
            {
                continue;
            }

            if (string.IsNullOrEmpty(dto.ChannelName) && string.IsNullOrEmpty(dto.ChannelNumber))
            {
                continue;
            }

            if (_channelCache.TryGet(dto.Path, out _))
            {
                continue;
            }

            _channelCache.Set(dto.Path, new ChannelResolver.ChannelInfo(dto.ChannelName, dto.ChannelNumber));
            _logger.LogInformation(
                "Captured channel for in-progress recording {Path}: {Name} ({Number})",
                dto.Path,
                dto.ChannelName,
                dto.ChannelNumber);
        }
    }
}
