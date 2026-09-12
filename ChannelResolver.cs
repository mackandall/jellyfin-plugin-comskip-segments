using System;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// Resolves the DVR channel (name + number) a show normally airs on, by name — NOT by
/// looking up the specific recording. A completed recording carries no durable channel
/// link anywhere in the plugin-visible API: <c>BaseItem.ChannelId</c> is empty,
/// <c>ILiveTvManager.GetRecordingsAsync</c> only carries channel info while a recording
/// is still actively in progress (confirmed by hand against a live 12.0.0 server — it
/// vanishes the moment the recording finishes). What DOES persist for a day or two is
/// the aired program guide, and in a normal broadcast/syndication lineup a given show
/// airs on the same channel every time — so matching the show's name against a recent
/// aired guide entry reliably recovers its channel even long after the recording that
/// prompted the check has completed.
///
/// Only called when a channel allow- or block-list is actually configured (see
/// DetectionGate), so a default install never touches Live TV for this.
/// </summary>
public static class ChannelResolver
{
    public readonly record struct ChannelInfo(string? Name, string? Number);

    // ChannelName/ChannelNumber are an opt-in DtoOptions field, not populated by default.
    private static readonly DtoOptions ProgramDtoOptions = new(false)
    {
        Fields = new[] { ItemFields.ChannelInfo }
    };

    /// <param name="name">The show's series name (or item name for a non-episodic recording).</param>
    /// <param name="userId">
    /// Required: GetPrograms with UserId left at Guid.Empty comes back empty — confirmed
    /// by hand against a live server, same as GetRecordingsAsync. Pass any existing
    /// user's ID; guide data isn't meaningfully user-scoped, it's just what the API needs.
    /// </param>
    public static ChannelInfo? Resolve(ILiveTvManager liveTvManager, string name, Guid userId, ILogger logger)
    {
        try
        {
            var query = new InternalItemsQuery
            {
                Name = name,
                HasAired = true,
                Limit = 5
            };

            // Blocking call: this runs off a background worker (queue processing) or a
            // library-event thread, never a UI thread — ASP.NET Core has no
            // SynchronizationContext to deadlock against. The one caller that runs it on
            // an HTTP request thread (the manual-detect endpoint) is likewise fine for
            // the same reason.
            var result = liveTvManager
                .GetPrograms(query, ProgramDtoOptions, CancellationToken.None)
                .GetAwaiter().GetResult();

            var match = result.Items.FirstOrDefault(i => !string.IsNullOrEmpty(i.ChannelName) || !string.IsNullOrEmpty(i.ChannelNumber));
            if (match is not null)
            {
                return new ChannelInfo(match.ChannelName, match.ChannelNumber);
            }

            logger.LogWarning("No recently-aired guide entry with channel info found for \"{Name}\"", name);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Channel lookup failed for \"{Name}\"", name);
            return null;
        }
    }
}
