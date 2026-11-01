using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Plugin.ComskipSegments.Editing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// Admin-only control surface for the audio-format trim feature: the config page's
/// manual-trim button and the item-details "Fix opening/closing audio format" menu
/// entry both land here.
///
/// Unlike <see cref="ComskipSegmentsController"/>'s Detect endpoint, this always runs
/// synchronously and returns the outcome directly — the scan itself is cheap (a few
/// seconds of audio-only decode even for a multi-hour recording) and the point of a
/// manual click is to see the result of THIS click, not to poll a queue.
/// </summary>
[ApiController]
[Route("ComskipSegments/AudioTrim")]
[Authorize]
[RecordingEditor]
public sealed class AudioTrimController : ControllerBase
{
    private readonly ILogger<AudioTrimController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly AudioTrimService _audioTrim;
    private readonly AudioTrimStore _audioTrimStore;
    private readonly DetectionManager _detectionManager;
    private readonly DetectionStore _detectionStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly CutTracker _cuts;

    public AudioTrimController(
        ILogger<AudioTrimController> logger,
        ILibraryManager libraryManager,
        AudioTrimService audioTrim,
        AudioTrimStore audioTrimStore,
        DetectionManager detectionManager,
        DetectionStore detectionStore,
        IServiceProvider serviceProvider,
        CutTracker cuts)
    {
        _cuts = cuts;
        _logger = logger;
        _libraryManager = libraryManager;
        _audioTrim = audioTrim;
        _audioTrimStore = audioTrimStore;
        _detectionManager = detectionManager;
        _detectionStore = detectionStore;
        _serviceProvider = serviceProvider;
    }

    public sealed record ApplyResponse(bool Attempted, bool Trimmed, double LeadTrimSeconds, double TrailTrimSeconds, string Message);

    public sealed record StatusResponse(string? Status, double? LeadTrimSeconds, double? TrailTrimSeconds, string? BackupPath, string? Message, DateTime? UpdatedUtc);

    /// <summary>
    /// Scans (and, if warranted, trims) one item right now. Always forced — same
    /// one-click-does-something expectation as the item menu's "Scan for commercials"
    /// entry — so a repeat click after a NotNeeded/Applied result re-scans rather than
    /// silently no-op'ing.
    ///
    /// If a trim actually happens and Comskip has already published commercial-skip
    /// segments for this item, those timestamps are now wrong (the file's timeline
    /// shifted by the lead trim), so this clears them and force-requeues Comskip —
    /// the automatic pipeline never hits this because it always trims BEFORE Comskip
    /// runs for the first time.
    /// </summary>
    [HttpPost("Apply/{itemId}")]
    public async Task<ActionResult<ApplyResponse>> Apply([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnableAudioFormatTrim)
        {
            _logger.LogWarning("Audio-trim Apply rejected for {ItemId}: feature disabled in plugin settings", itemId);
            return BadRequest(new ApplyResponse(false, false, 0, 0, "Audio-format trim is disabled in plugin settings."));
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogWarning("Audio-trim Apply rejected: no library item with ID {ItemId}", itemId);
            return NotFound(new ApplyResponse(false, false, 0, 0, $"No library item with ID {itemId}"));
        }

        if (item.Path is null || !System.IO.File.Exists(item.Path))
        {
            _logger.LogWarning("Audio-trim Apply rejected for {ItemId}: no resolvable file on disk (Path={Path})", itemId, item.Path);
            return BadRequest(new ApplyResponse(false, false, 0, 0, "Item has no resolvable file on disk"));
        }

        // Claimed like the other cuts (one action that may rewrite a recording at a time), and
        // run in the background: the scan is quick but a cut takes minutes, so the click
        // returns and the apps follow it in the cut list.
        if (!_cuts.TryStart(item, "audio-format", out var job))
        {
            return Conflict(new ApplyResponse(false, false, 0, 0, "Already working on this recording; wait for that to finish."));
        }

        // Resolved now, while the request's services are alive: a cut can outlast the request,
        // and its scope is disposed with it (seen live: clearing the old breaks failed after a
        // trim with "Cannot access a disposed object").
        var segmentManager = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
        var work = Task.Run(async () =>
        {
            try
            {
                var result = await _audioTrim.ApplyIfNeededAsync(item, cfg, force: true, job.Cancel.Token, job).ConfigureAwait(false);
                if (result.Trimmed && result.LeadTrimSeconds > 0)
                {
                    await InvalidateStaleCommercialBreaks(segmentManager, itemId, CancellationToken.None).ConfigureAwait(false);
                }

                _logger.LogInformation("Manual audio-trim for {Path}: {Message}", item.Path, result.Message);
                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Manual audio-trim for {Path} stopped", item.Path);
                return new AudioTrimApplyResult(true, false, 0, 0, "Stopped; the recording is untouched.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during manual audio-trim for {Path}", item.Path);
                return new AudioTrimApplyResult(true, false, 0, 0, $"Unexpected error: {ex.Message}");
            }
            finally
            {
                _cuts.Finish(job);
            }
        });

        // The check takes seconds, so its answer ("uniform, nothing to trim") still comes back
        // to the click; a cut that runs longer carries on and the apps show its progress. 8 s:
        // JDVR's client gives up on a silent request after about 10 s.
        if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(8), cancellationToken)).ConfigureAwait(false) == work)
        {
            var r = await work.ConfigureAwait(false);
            return Ok(new ApplyResponse(r.Attempted, r.Trimmed, r.LeadTrimSeconds, r.TrailTrimSeconds, r.Message));
        }

        return Ok(new ApplyResponse(true, false, 0, 0, "Cutting the stray audio off. The recording shows the progress, with Stop until the cut copy goes in."));
    }

    [HttpGet("Status/{itemId}")]
    public ActionResult<StatusResponse> Status([FromRoute] Guid itemId)
    {
        if (_audioTrimStore.TryGet(itemId, out var record))
        {
            return Ok(new StatusResponse(
                record.Status.ToString(), record.LeadTrimSeconds, record.TrailTrimSeconds, record.BackupPath, record.Message, record.UpdatedUtc));
        }

        return Ok(new StatusResponse(null, null, null, null, null, null));
    }

    /// <summary>
    /// A lead trim shifts every timestamp in the file by however many seconds were cut
    /// from the front, so any commercial-skip segments Comskip already published against
    /// the pre-trim file are now pointing at the wrong moments. Clear them and force a
    /// fresh Comskip pass against the corrected file.
    /// </summary>
    private async Task InvalidateStaleCommercialBreaks(IMediaSegmentManager segmentManager, Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            await segmentManager.DeleteSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear stale commercial-skip segments after audio-trim for {ItemId}", itemId);
        }

        _detectionStore.Remove(itemId);

        // NOT force: true here. Clearing the stale record above is unconditional (a
        // wrong segment is worse than no segment, regardless of channel), but RE-RUNNING
        // Comskip specifically is exactly what the show/channel block-lists exist to
        // prevent (e.g. a commercial-free premium movie channel) — a forced re-enqueue
        // would silently override that filter as a side effect of an unrelated feature.
        // A non-forced enqueue re-applies the same gate the item passed (or didn't)
        // originally.
        _detectionManager.Enqueue(itemId, force: false, reason: ScanReason.AfterTrim);
    }
}
