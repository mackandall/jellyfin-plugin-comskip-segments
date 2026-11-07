using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using Jellyfin.Plugin.ComskipSegments.Detection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// Read-only commercial-skip status for the badges the web script and Wholphin draw on
/// cards, list rows and guide cells. Open to every signed-in user (the admin controls
/// live in <see cref="ComskipSegmentsController"/>); it only says what was found, never
/// changes anything.
/// </summary>
[ApiController]
[Route("ComskipSegments/Badges")]
[Authorize]
public sealed class BadgeController : ControllerBase
{
    private const int MaxIds = 300;
    private const string StateScanning = "scanning";
    private const string StateFound = "found";
    private const string StateNone = "none";
    private const string StateFailed = "failed";
    private const string StateSummary = "summary";

    private readonly ILibraryManager _libraryManager;
    private readonly DetectionStore _store;
    private readonly DetectionManager _manager;
    private readonly LiveScanCoordinator _live;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BadgeController> _logger;

    public BadgeController(
        ILibraryManager libraryManager,
        DetectionStore store,
        DetectionManager manager,
        LiveScanCoordinator live,
        IServiceProvider serviceProvider,
        ILogger<BadgeController> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _manager = manager;
        _live = live;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public sealed record BadgeSettings(string CardStyle, string GuideStyle, string ScanningMotion, bool ShowSummary);

    /// <param name="State">scanning | found | none | failed | summary (a show or season).</param>
    /// <param name="Minutes">Total minutes of commercials (found: at least 1; scanning: found so far, if known).</param>
    /// <param name="Source">comskip | edl, for a found result.</param>
    /// <param name="Done">Summary: episodes with a finished result.</param>
    /// <param name="Total">Summary: episodes in the show or season.</param>
    /// <param name="Failed">Summary: at least one episode's scan failed.</param>
    public sealed record Badge(string State, int? Minutes = null, string? Source = null, int? Done = null, int? Total = null, bool? Failed = null);

    public sealed record BadgesResponse(BadgeSettings Settings, Dictionary<string, Badge> Items);

    /// <param name="State">"scanning" while the live Comskip scan runs, else "recording"
    /// (recording, but not scanned: e.g. a channel on the block-list). Badges show only for scanning.</param>
    /// <param name="RecordingItemId">The in-progress recording as a library item, for "Watch from start".</param>
    public sealed record GuideBadge(string ProgramId, string State, int? Minutes, string? RecordingItemId = null);

    public sealed record GuideResponse(BadgeSettings Settings, IReadOnlyList<GuideBadge> Programs);

    /// <summary>
    /// Badges for the given library items (comma-separated ids, as many as a screen shows).
    /// Items with nothing to show (never scanned, e.g. a commercial-free channel) are left
    /// out of <c>Items</c>.
    /// </summary>
    [HttpGet]
    public ActionResult<BadgesResponse> Get([FromQuery] string? ids)
    {
        var items = new Dictionary<string, Badge>(StringComparer.Ordinal);
        foreach (var raw in (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxIds))
        {
            if (!Guid.TryParse(raw, out var id) || _libraryManager.GetItemById(id) is not BaseItem item)
            {
                continue;
            }

            var badge = item is Series or Season ? SummaryBadge(item) : ItemBadge(item);
            if (badge is not null)
            {
                items[id.ToString("N")] = badge;
            }
        }

        return Ok(new BadgesResponse(Settings(), items));
    }

    /// <summary>
    /// Guide entries being recorded right now whose recording is being scanned live, with
    /// the minutes of commercials found so far. The web script and Wholphin poll this while
    /// the guide is on screen.
    /// </summary>
    [HttpGet("Guide")]
    public async Task<ActionResult<GuideResponse>> Guide(CancellationToken cancellationToken)
    {
        var programs = new List<GuideBadge>();
        var liveTv = _serviceProvider.GetService<ILiveTvManager>();
        var recordings = _serviceProvider.GetService<IRecordingsManager>();
        if (liveTv is not null && recordings is not null)
        {
            try
            {
                var timers = await liveTv.GetTimers(new TimerQuery(), cancellationToken).ConfigureAwait(false);
                foreach (var timer in timers.Items.Where(t => t.Status == RecordingStatus.InProgress && !string.IsNullOrEmpty(t.ProgramId)))
                {
                    // Active recordings are keyed by the timer's own id, which the DTO carries
                    // as ExternalId; its Id is a derived one (confirmed live: looking up by Id
                    // found nothing, so no guide entry ever got its tag).
                    var path = (timer.ExternalId is { } externalId ? recordings.GetActiveRecordingPath(externalId) : null)
                        ?? recordings.GetActiveRecordingPath(timer.Id);
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    var scanning = _live.IsTracking(path);
                    int? minutes = null;
                    if (scanning && _live.GetLiveEdl(path) is { } edl)
                    {
                        try
                        {
                            minutes = Minutes(EdlParser.Parse(edl, 0), roundUp: false);
                        }
                        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                        {
                            // Being rewritten by Comskip at this instant; show "scanning" without a count.
                        }
                    }

                    var recordingItem = _libraryManager.FindByPath(path, false);
                    programs.Add(new GuideBadge(timer.ProgramId, scanning ? StateScanning : "recording", minutes, recordingItem?.Id.ToString("N")));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not read active recordings for guide badges");
            }
        }

        return Ok(new GuideResponse(Settings(), programs));
    }

    public sealed record LiveSegment(string Type, long StartTicks, long EndTicks);

    /// <param name="InProgress">The recording is still being written. False: use Jellyfin's
    /// own segments (GET /MediaSegments/{id}), which exist once the recording is finished.</param>
    /// <param name="Items">Commercial breaks the live Comskip scan has found so far.</param>
    public sealed record LiveResponse(bool InProgress, IReadOnlyList<LiveSegment> Items);

    /// <summary>
    /// Commercial breaks found so far in a recording that is still in progress. Jellyfin
    /// itself serves no segments for such an item until the recording finishes (its
    /// segments endpoint 404s), so the web overlay and Wholphin ask here to mark and skip
    /// commercials while catching up on, say, a game still being recorded. Takes a library
    /// item id or an active recording's id.
    /// </summary>
    [HttpGet("~/ComskipSegments/Live/{id}")]
    public async Task<ActionResult<LiveResponse>> Live([FromRoute] string id, CancellationToken ct)
    {
        var recordings = _serviceProvider.GetService<IRecordingsManager>();
        string? path = null;
        if (Guid.TryParse(id, out var itemId) && _libraryManager.GetItemById(itemId) is BaseItem item)
        {
            path = item.Path;
        }

        path ??= recordings?.GetActiveRecordingPath(id);
        if (string.IsNullOrEmpty(path) || recordings?.GetActiveRecordingInfo(path) is null)
        {
            return Ok(new LiveResponse(false, Array.Empty<LiveSegment>()));
        }

        var segments = new List<LiveSegment>();
        if (_live.GetLiveEdl(path) is { } edl)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
                IReadOnlyList<CommercialBreak> breaks = EdlParser.Parse(edl, 0);
                var ffprobe = _serviceProvider.GetService<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>()?.ProbePath;
                if (cfg.AlignComskipClock && breaks.Count > 0 && !string.IsNullOrEmpty(ffprobe))
                {
                    // Comskip's clock starts at the first decodable picture (see ComskipClock).
                    breaks = ComskipClock.Shift(breaks, await ComskipClock.LiveOffsetAsync(ffprobe, path, ct).ConfigureAwait(false));
                }

                segments.AddRange(EdlParser.Pad(breaks, cfg.PaddingSeconds).Select(b => new LiveSegment("Commercial", b.StartTicks, b.EndTicks)));
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Being rewritten by Comskip at this instant; the next poll gets it.
            }
        }

        return Ok(new LiveResponse(true, segments));
    }

    private Badge? ItemBadge(BaseItem item)
    {
        if (item is not Video)
        {
            return null;
        }

        if (_manager.IsInFlight(item.Id) || (item.Path is not null && _live.IsTracking(item.Path)))
        {
            return new Badge(StateScanning);
        }

        if (!_store.TryGet(item.Id, out var record))
        {
            return null;
        }

        return record.Status switch
        {
            DetectionStatus.Detected => new Badge(StateFound, Minutes(record.Breaks, roundUp: true), record.Source ?? DetectionRecord.SourceComskip),
            DetectionStatus.NoCommercials => new Badge(StateNone, 0, record.Source ?? DetectionRecord.SourceComskip),
            _ => new Badge(StateFailed)
        };
    }

    /// <summary>
    /// "done/total" for a show or season; amber (scanning) while any episode is in the queue.
    /// A show none of whose episodes was ever scanned gets no badge rather than "0/12".
    /// </summary>
    private Badge? SummaryBadge(BaseItem folder)
    {
        if (!(Plugin.Instance?.Configuration ?? new PluginConfiguration()).BadgeShowSummary)
        {
            return null;
        }

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = new[] { folder.Id },
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true
        });

        int done = 0;
        bool busy = false, failed = false;
        foreach (var episode in episodes)
        {
            if (_manager.IsInFlight(episode.Id))
            {
                busy = true;
            }
            else if (_store.TryGet(episode.Id, out var record))
            {
                if (record.Status == DetectionStatus.Failed)
                {
                    failed = true;
                }
                else if (record.Source != DetectionRecord.SourceCleared)
                {
                    // Commercials removed by hand: that episode isn't marked any more.
                    done++;
                }
            }
        }

        if (done == 0 && !busy && !failed)
        {
            return null;
        }

        return new Badge(busy ? StateScanning : StateSummary, Done: done, Total: episodes.Count, Failed: failed);
    }

    private static int Minutes(IReadOnlyList<CommercialBreak> breaks, bool roundUp)
    {
        var ticks = breaks.Sum(b => Math.Max(0, b.EndTicks - b.StartTicks));
        var minutes = (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes, MidpointRounding.AwayFromZero);

        // A found result with under half a minute of breaks still reads "1m", never "0m"
        // (which means "no commercials").
        return roundUp && ticks > 0 && minutes == 0 ? 1 : minutes;
    }

    private static BadgeSettings Settings()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return new BadgeSettings(
            OneOf(cfg.BadgeCardStyle, "icon", "icon", "letters"),
            OneOf(cfg.BadgeGuideStyle, "letters", "icon", "letters"),
            OneOf(cfg.BadgeScanningMotion, "pulse", "pulse", "spin", "none"),
            cfg.BadgeShowSummary);
    }

    private static string OneOf(string? value, string fallback, params string[] allowed) =>
        value is not null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase) ? value.ToLowerInvariant() : fallback;
}
