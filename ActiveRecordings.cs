using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// The files being recorded right now, one per in-progress timer, taken from the timer's
/// active recording rather than from the recordings list. The recordings list gives each
/// recording's library item path, and a recording Jellyfin had to restart part-way (the
/// stream dropped, so it carries on in "Name - 1.ts") is one item whose path is the first,
/// finished piece: the piece still being written never showed up there, so it got no live
/// scan (Samurai Jack, 2026-10-31). The guide badges have read timers this way all along.
/// </summary>
public static class ActiveRecordings
{
    public sealed record Active(string Path, string? Name, string? ChannelName, string? ChannelNumber);

    public static async Task<IReadOnlyList<Active>> ListAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (services.GetService<ILiveTvManager>() is not { } liveTv || services.GetService<IRecordingsManager>() is not { } recordings)
        {
            return Array.Empty<Active>();
        }

        var library = services.GetService<ILibraryManager>();
        var timers = await liveTv.GetTimers(new TimerQuery(), cancellationToken).ConfigureAwait(false);
        var active = new List<Active>();
        foreach (var timer in timers.Items.Where(t => t.Status == RecordingStatus.InProgress))
        {
            // Active recordings are keyed by the timer's own id, which the DTO carries as
            // ExternalId (its Id is a derived one).
            var path = (timer.ExternalId is { } externalId ? recordings.GetActiveRecordingPath(externalId) : null)
                ?? recordings.GetActiveRecordingPath(timer.Id);
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var channel = timer.ChannelId != Guid.Empty ? library?.GetItemById(timer.ChannelId) as LiveTvChannel : null;
            active.Add(new Active(path, timer.Name, timer.ChannelName ?? channel?.Name, channel?.Number));
        }

        return active;
    }
}
