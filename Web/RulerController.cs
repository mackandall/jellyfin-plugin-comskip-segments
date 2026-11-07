using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using Jellyfin.Plugin.ComskipSegments.Detection;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>The settings page's break-length check (see <see cref="BreakRuler"/>).</summary>
[ApiController]
[Route("ComskipSegments/Ruler")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class RulerController : ControllerBase
{
    private readonly DetectionStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly DetectionManager _manager;
    private readonly ILogger<RulerController> _logger;

    public RulerController(DetectionStore store, ILibraryManager libraryManager, DetectionManager manager, ILogger<RulerController> logger)
    {
        _logger = logger;
        _store = store;
        _libraryManager = libraryManager;
        _manager = manager;
    }

    /// <param name="Old">Comskip results with breaks from before the clock fix (their edges are early).</param>
    /// <param name="Waiting">Re-scans of them still in the queue.</param>
    public sealed record OldDto(int Old, int Waiting);

    /// <summary>How many Comskip results are on the old clock, and how many of their re-scans are queued.</summary>
    [HttpGet("Old")]
    public ActionResult<OldDto> Old() => Ok(new OldDto(OldResults().Count, WaitingRescans()));

    /// <summary>
    /// Queues a Comskip re-scan of every result from before the clock fix (Comskip's times were
    /// early by up to two seconds, see <see cref="Comskip.ComskipClock"/>). Each keeps its breaks
    /// until its new ones are in; breaks set by hand and other programs' .edl files aren't touched.
    /// The batch waits behind every other scan.
    /// </summary>
    [HttpPost("RescanOld")]
    public ActionResult<OldDto> RescanOld()
    {
        var queued = 0;
        foreach (var id in OldResults())
        {
            if (_manager.Enqueue(id, force: true, ScanReason.Rescan, setAsideOtherEdl: true) == DetectionManager.EnqueueResult.Queued)
            {
                queued++;
            }
        }

        _logger.LogInformation("Re-scan of old Comskip results asked for: {Queued} queued behind every other scan", queued);
        return Ok(new OldDto(queued, WaitingRescans()));
    }

    private int WaitingRescans() => _manager.Snapshot().Waiting.Count(e => e.Reason == ScanReason.Rescan);

    private List<Guid> OldResults() =>
        _store.All()
            .Where(r => r.Value.Status == DetectionStatus.Detected && r.Value.Breaks.Count > 0 && r.Value.ClockOffset is null
                && (r.Value.Source ?? DetectionRecord.SourceComskip) == DetectionRecord.SourceComskip)
            .Where(r => _libraryManager.GetItemById(r.Key) is { Path: { } path } && System.IO.File.Exists(path))
            .Select(r => r.Key)
            .ToList();

    /// <param name="Name">What the breaks are: Comskip before or after lining up, set by hand, another program's .edl.</param>
    /// <param name="On15Percent">Breaks a whole number of 15-second units long.</param>
    /// <param name="On5Percent">...of 5-second units.</param>
    public sealed record GroupDto(string Key, string Name, int Recordings, int Breaks, double On15Percent, double On5Percent);

    public sealed record ShowDto(string Name, int Recordings, int Breaks, double On15Percent, double On5Percent);

    /// <param name="Chance15Percent">What a break of any length scores by luck on the 15-second grid (5-second: three times it).</param>
    public sealed record RulerDto(double ToleranceSeconds, double Chance15Percent, double Chance5Percent, IReadOnlyList<GroupDto> Groups, IReadOnlyList<ShowDto> Shows);

    private static readonly (string Key, string Name)[] GroupNames =
    {
        ("aligned", "Comskip, lined up and snapped"),
        ("clock", "Comskip, lined up, edges not snapped"),
        ("old", "Comskip, scanned before lining up"),
        ("edited", "Set by hand"),
        ("edl", "Another program's .edl")
    };

    [HttpGet]
    public ActionResult<RulerDto> Get()
    {
        var padding = (Plugin.Instance?.Configuration ?? new PluginConfiguration()).PaddingSeconds;
        var groups = GroupNames.ToDictionary(g => g.Key, _ => BreakRuler.Tally.Empty);
        var shows = new Dictionary<string, BreakRuler.Tally>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, record) in _store.All())
        {
            if (record.Status != DetectionStatus.Detected || record.Breaks.Count == 0)
            {
                continue;
            }

            var key = (record.Source ?? DetectionRecord.SourceComskip) switch
            {
                DetectionRecord.SourceComskip => record.ClockOffset is null ? "old" : record.EdgesSnapped ? "aligned" : "clock",
                DetectionRecord.SourceEdited => "edited",
                DetectionRecord.SourceEdl => "edl",
                _ => null
            };
            if (key is null || _libraryManager.GetItemById(id) is not BaseItem item)
            {
                continue;
            }

            var fps = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video) is { } v
                ? (double?)(v.RealFrameRate ?? v.AverageFrameRate)
                : null;
            var tally = BreakRuler.Measure(
                record.Breaks,
                (item.RunTimeTicks ?? 0) / 1e7,
                fps,
                key is "aligned" or "clock" or "old" ? padding : 0,
                record.AdSpans);
            groups[key] = groups[key].Add(tally);
            if (key == "aligned" && tally.Breaks > 0)
            {
                var show = item is Episode e && !string.IsNullOrWhiteSpace(e.SeriesName) ? e.SeriesName : item.Name;
                shows[show] = (shows.TryGetValue(show, out var t) ? t : BreakRuler.Tally.Empty).Add(tally);
            }
        }

        return new RulerDto(
            BreakRuler.ToleranceSeconds,
            Percent(BreakRuler.Chance(15.015)),
            Percent(Math.Min(1, BreakRuler.Chance(5.005))),
            GroupNames.Select(g => Dto(g.Key, g.Name, groups[g.Key])).ToList(),
            shows.OrderByDescending(s => s.Value.Breaks).Take(25)
                .Select(s => new ShowDto(s.Key, s.Value.Recordings, s.Value.Breaks, Share(s.Value.On15, s.Value.Breaks), Share(s.Value.On5, s.Value.Breaks)))
                .ToList());
    }

    private static GroupDto Dto(string key, string name, BreakRuler.Tally t) =>
        new(key, name, t.Recordings, t.Breaks, Share(t.On15, t.Breaks), Share(t.On5, t.Breaks));

    private static double Share(int part, int whole) => whole == 0 ? 0 : Percent((double)part / whole);

    private static double Percent(double fraction) => Math.Round(100 * fraction, 1);
}
