using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Plugin.ComskipSegments.Editing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// The segments dashboard's Queue tab: the scans running now, the ones waiting (in order), the
/// ones waiting for their recording to end, and the last <see cref="ScanQueue.HistorySize"/> finished.
/// </summary>
[ApiController]
[Route("ComskipSegments/Queue")]
[Authorize]
[RecordingEditor]
public sealed class QueueController : ControllerBase
{
    private const int DefaultShown = 100;
    private readonly ILibraryManager _libraryManager;
    private readonly DetectionManager _manager;

    public QueueController(ILibraryManager libraryManager, DetectionManager manager)
    {
        _libraryManager = libraryManager;
        _manager = manager;
    }

    /// <param name="Stage">starting, audio, program-start, comskip, saving.</param>
    /// <param name="Progress">How far Comskip has read the file, 0–1 (null: not known yet).</param>
    /// <param name="LeftSeconds">A guess from the progress so far (null: too early to tell).</param>
    /// <param name="PausedSeconds">How long it has been paused (null: not paused).</param>
    public sealed record RunningDto(
        int Worker, Guid Id, string Name, string? Folder, string Reason, string Stage,
        DateTime StartedUtc, double Seconds, double? Progress, double? LeftSeconds, bool CanPause, double? PausedSeconds);

    /// <param name="Recording">Still being recorded: it joins the queue when the recording ends.</param>
    public sealed record WaitingDto(Guid Id, string Name, string? Library, string? Folder, string Reason, DateTime QueuedUtc, bool Recording);

    /// <param name="AverageSeconds">How long a Comskip scan has taken lately (null: none finished yet).</param>
    /// <param name="WaitingCount">All waiting, also those not in <see cref="Waiting"/> when it's cut short.</param>
    /// <param name="Done24">Finished in the last 24 hours (scanned, found breaks or not).</param>
    /// <param name="Paused">The queue is paused: nothing new starts.</param>
    public sealed record QueueResponse(
        bool Paused,
        int Workers,
        IReadOnlyList<RunningDto> Running,
        IReadOnlyList<WaitingDto> Recording,
        IReadOnlyList<WaitingDto> Waiting,
        int WaitingCount,
        double? AverageSeconds,
        IReadOnlyList<FinishedEntry> History,
        int Done24,
        int Failed24);

    public sealed record ActionResponse(bool Ok, string Message);

    /// <param name="all">Every waiting scan, not just the first 100.</param>
    [HttpGet]
    public ActionResult<QueueResponse> Get([FromQuery] bool all = false)
    {
        var snap = _manager.Snapshot();
        var now = DateTime.UtcNow;

        var running = snap.Running.Select(j =>
        {
            var progress = j.Stage == "comskip" ? DetectionManager.Progress(j) : null;
            double? left = null;
            if (progress is > 0.02 and < 1)
            {
                var spent = j.ComskipActive.Elapsed.TotalSeconds;
                left = spent / progress.Value - spent;
            }

            var paused = j.PausedUtc is { } p ? (now - p).TotalSeconds : (double?)null;
            return new RunningDto(
                j.Worker, j.Entry.Id, j.Name.Length > 0 ? j.Name : NameOf(j.Entry.Id), FolderOf(j.Path),
                j.Entry.Reason, j.Stage, j.StartedUtc, (now - j.StartedUtc).TotalSeconds, progress, left,
                j.CanPause, paused);
        }).ToList();

        var recording = snap.Recording.Select(r => Row(r.Entry, true, r.Since)).ToList();
        var waiting = (all ? snap.Waiting : snap.Waiting.Take(DefaultShown)).Select(e => Row(e, false, e.QueuedUtc)).ToList();

        var recent = snap.History.Where(h => h.Comskip).Take(20).ToList();
        double? average = recent.Count == 0 ? null : recent.Average(h => (h.FinishedUtc - h.StartedUtc).TotalSeconds);

        var day = snap.History.Where(h => now - h.FinishedUtc < TimeSpan.FromHours(24)).ToList();
        return Ok(new QueueResponse(
            _manager.QueuePaused,
            snap.Workers,
            running,
            recording,
            waiting,
            snap.Waiting.Count,
            average,
            snap.History,
            day.Count(h => h.Result is "breaks" or "none"),
            day.Count(h => h.Result == "failed")));
    }

    /// <summary>Cancels a running scan. The recording keeps the breaks it had.</summary>
    [HttpPost("{itemId}/Cancel")]
    public ActionResult<ActionResponse> Cancel([FromRoute] Guid itemId) =>
        Ok(_manager.Cancel(itemId) ? new ActionResponse(true, "Cancelling…") : new ActionResponse(false, "It isn't running any more."));

    /// <summary>Freezes a running Comskip where it is.</summary>
    [HttpPost("{itemId}/Pause")]
    public ActionResult<ActionResponse> Pause([FromRoute] Guid itemId) =>
        Ok(_manager.PauseScan(itemId, true) ? new ActionResponse(true, "Paused.") : new ActionResponse(false, "Only a running Comskip can be paused."));

    [HttpPost("{itemId}/Resume")]
    public ActionResult<ActionResponse> Resume([FromRoute] Guid itemId) =>
        Ok(_manager.PauseScan(itemId, false) ? new ActionResponse(true, "Resumed.") : new ActionResponse(false, "It isn't paused."));

    /// <summary>Pauses the queue: nothing new starts; the scans running now go on (pause those on their own).</summary>
    [HttpPost("Pause")]
    public ActionResult<ActionResponse> PauseQueue()
    {
        _manager.PauseQueue(true);
        return Ok(new ActionResponse(true, "Queue paused: nothing new starts until you resume it."));
    }

    [HttpPost("Resume")]
    public ActionResult<ActionResponse> ResumeQueue()
    {
        _manager.PauseQueue(false);
        return Ok(new ActionResponse(true, "Queue resumed."));
    }

    /// <summary>Moves a waiting scan to the top (it still waits for a free worker).</summary>
    [HttpPost("{itemId}/Next")]
    public ActionResult<ActionResponse> Next([FromRoute] Guid itemId) =>
        Ok(_manager.ScanNext(itemId) ? new ActionResponse(true, "Moved to the top.") : new ActionResponse(false, "It isn't waiting any more."));

    [HttpPost("{itemId}/Remove")]
    public ActionResult<ActionResponse> Remove([FromRoute] Guid itemId) =>
        Ok(_manager.Remove(e => e.Id == itemId) > 0 ? new ActionResponse(true, "Removed.") : new ActionResponse(false, "It isn't waiting any more."));

    /// <summary>Empties the queue (running scans go on), or only the scans queued for <paramref name="reason"/>.</summary>
    [HttpPost("Clear")]
    public ActionResult<ActionResponse> Clear([FromQuery] string? reason = null)
    {
        var n = _manager.Remove(e => reason is null || e.Reason == reason);
        return Ok(new ActionResponse(true, $"Removed {n} from the queue."));
    }

    private WaitingDto Row(QueueEntry e, bool recording, DateTime when)
    {
        var item = _libraryManager.GetItemById(e.Id);
        string? library = null;
        if (item is not null)
        {
            try
            {
                library = _libraryManager.GetCollectionFolders(item).FirstOrDefault()?.Name;
            }
            catch (Exception)
            {
                // Only a label.
            }
        }

        return new WaitingDto(
            e.Id,
            item is null ? "(no longer in the library)" : DetectionManager.Describe(item),
            library,
            FolderOf(item?.Path),
            e.Reason,
            when,
            recording);
    }

    private string NameOf(Guid id) =>
        _libraryManager.GetItemById(id) is BaseItem item ? DetectionManager.Describe(item) : "…";

    private static string? FolderOf(string? path) =>
        string.IsNullOrEmpty(path) ? null : System.IO.Path.GetDirectoryName(path);
}
