using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ComskipSegments.Editing;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// Admin-only control surface for the config page: validating configured paths "as the
/// Jellyfin service account" (the process itself, since Comskip runs under that same
/// account) and manually (re)running detection for one item by ID, bypassing the
/// show/channel filters the same way a forced enqueue always has.
/// </summary>
[ApiController]
[Route("ComskipSegments")]
[Authorize]
[RecordingEditor]
public sealed class ComskipSegmentsController : ControllerBase
{
    private readonly ILogger<ComskipSegmentsController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly DetectionManager _manager;
    private readonly DetectionStore _store;
    private readonly IServiceProvider _serviceProvider;
    private readonly Comskip.ComskipInstaller _installer;
    private readonly WebOverlayPatcher _overlay;

    public ComskipSegmentsController(
        ILogger<ComskipSegmentsController> logger,
        ILibraryManager libraryManager,
        DetectionManager manager,
        DetectionStore store,
        IServiceProvider serviceProvider,
        Comskip.ComskipInstaller installer,
        WebOverlayPatcher overlay)
    {
        _installer = installer;
        _overlay = overlay;
        _logger = logger;
        _libraryManager = libraryManager;
        _manager = manager;
        _store = store;
        _serviceProvider = serviceProvider;
    }

    public sealed record ValidateRequest(string? ComskipPath, string? ComskipIniPath, string? WorkDirectory);

    public sealed record CheckResult(bool Ok, string Message);

    public sealed record ValidateResponse(CheckResult Binary, CheckResult Ini, CheckResult WorkDir);

    public sealed record DetectResponse(bool Queued, string Message);

    public sealed record StatusResponse(bool InFlight, string? Status, int? BreakCount, DateTime? UpdatedUtc, string? Source = null);

    // The config page's path check: administrators only, like the page.
    [HttpPost("Validate")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<ValidateResponse> Validate([FromBody] ValidateRequest request)
    {
        return Ok(new ValidateResponse(
            CheckBinary(request.ComskipPath?.Trim()),
            CheckReadableFile(request.ComskipIniPath?.Trim()),
            CheckWritableDirectory(request.WorkDirectory?.Trim())));
    }

    /// <summary>Whether Comskip is installed (and which), and whether Install Comskip can install it here.</summary>
    [HttpGet("ComskipInfo")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<Comskip.ComskipInstaller.ComskipInfo> ComskipInfo() =>
        Ok(_installer.Info(Plugin.Instance!.Configuration));

    /// <summary>The settings page's Install Comskip: downloads, checks and sets it up, then saves the paths.</summary>
    [HttpPost("InstallComskip")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<Comskip.ComskipInstaller.InstallResult>> InstallComskip(CancellationToken cancellationToken) =>
        Ok(await _installer.InstallAsync(Plugin.Instance!.Configuration, cancellationToken).ConfigureAwait(false));

    /// <summary>Whether jellyfin-web's index.html loads the web overlay, and whether Jellyfin may change it.</summary>
    [HttpGet("WebOverlay")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<WebOverlayPatcher.OverlayStatus> WebOverlay() => Ok(_overlay.Status());

    /// <summary>The settings page's Install web overlay: patches index.html where Jellyfin may, else says what to run.</summary>
    [HttpPost("WebOverlay/Install")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<WebOverlayPatcher.PatchResult> InstallWebOverlay() => Ok(_overlay.Install());

    /// <summary>
    /// Force a (re)run of detection for one item, bypassing the show/channel allow- and
    /// block-lists and any existing completed result the same way every other forced
    /// enqueue in this plugin does. On force, also clears Jellyfin's currently-published
    /// segments for the item so the UI doesn't keep serving stale breaks while the new
    /// run is in flight.
    /// </summary>
    [HttpPost("Detect/{itemId}")]
    public async Task<ActionResult<DetectResponse>> Detect(
        [FromRoute] Guid itemId,
        [FromQuery] bool force,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new DetectResponse(false, $"No library item with ID {itemId}"));
        }

        if (!Comskip.ComskipInstaller.IsInstalled(Plugin.Instance!.Configuration))
        {
            return Ok(new DetectResponse(false, Comskip.ComskipInstaller.NotInstalledMessage));
        }

        if (item is Folder)
        {
            return await DetectFolder(item, force, cancellationToken).ConfigureAwait(false);
        }

        if (item.Path is null || !System.IO.File.Exists(item.Path))
        {
            return BadRequest(new DetectResponse(false, "Item has no resolvable file on disk"));
        }

        var result = await Enqueue(item, force, cancellationToken, setAsideOtherEdl: true).ConfigureAwait(false);
        var (queued, message) = result switch
        {
            DetectionManager.EnqueueResult.ImportedEdl => (true, ImportedMessage(itemId)),
            DetectionManager.EnqueueResult.Queued => (true, "Queued"),
            DetectionManager.EnqueueResult.AlreadyDone => (false, "Not queued: item is already complete. Check Force to re-scan."),
            _ => (false, "Not queued: outside the recordings folder and scan paths, or filtered by the show/channel allow- or block-list. Check Force to bypass."),
        };
        _logger.LogInformation("Manual detection for {Path} (force={Force}): {Message}", item.Path, force, message);

        return Ok(new DetectResponse(queued, message));
    }

    /// <summary>
    /// A show, season or other folder (e.g. "Scan for commercials" on a series page):
    /// queues every video file under it.
    /// </summary>
    private async Task<ActionResult<DetectResponse>> DetectFolder(BaseItem folder, bool force, CancellationToken cancellationToken)
    {
        var videos = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = new[] { folder.Id },
                IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video },
                MediaTypes = new[] { MediaType.Video },
                Recursive = true,
                IsVirtualItem = false
            })
            .Where(i => i.Path is not null && System.IO.File.Exists(i.Path))
            .ToList();
        if (videos.Count == 0)
        {
            return BadRequest(new DetectResponse(false, $"No video files found under \"{folder.Name}\""));
        }

        int queued = 0, imported = 0;
        foreach (var video in videos)
        {
            switch (await Enqueue(video, force, cancellationToken).ConfigureAwait(false))
            {
                case DetectionManager.EnqueueResult.Queued:
                    queued++;
                    break;
                case DetectionManager.EnqueueResult.ImportedEdl:
                    imported++;
                    break;
            }
        }

        var parts = new List<string>();
        if (queued > 0)
        {
            parts.Add($"queued {queued} for Comskip");
        }

        if (imported > 0)
        {
            parts.Add($"used the existing .edl for {imported}");
        }

        var skipped = videos.Count - queued - imported;
        if (skipped > 0)
        {
            parts.Add($"skipped {skipped} (already complete, or filtered; check Force to bypass)");
        }

        var message = $"{videos.Count} videos: " + string.Join(", ", parts) + ".";
        _logger.LogInformation("Manual detection for {Name} (force={Force}): {Message}", folder.Name, force, message);
        return Ok(new DetectResponse(queued + imported > 0, message));
    }

    private string ImportedMessage(Guid itemId) =>
        _store.TryGet(itemId, out var record)
            ? $"Used the existing .edl beside the video: {record.Breaks.Count} commercial break(s)."
            : "Used the existing .edl beside the video.";

    // A forced re-scan keeps the breaks the recording has until the new ones are in (the scan
    // replaces them when it finishes), so a re-scan cancelled from the Queue tab loses nothing.
    private Task<DetectionManager.EnqueueResult> Enqueue(BaseItem item, bool force, CancellationToken cancellationToken, bool setAsideOtherEdl = false) =>
        Task.FromResult(_manager.Enqueue(item.Id, force, ScanReason.Manual, setAsideOtherEdl));

    public sealed record ClearResponse(bool Cleared, string Message);

    /// <summary>
    /// Wipes an item's published commercial-skip segments and stored detection record
    /// WITHOUT re-enqueueing — unlike <see cref="Detect"/> with Force checked, which
    /// clears and then unconditionally re-scans, bypassing the show/channel gate as a
    /// side effect. Useful when a result was produced by something that shouldn't have
    /// run in the first place (a stale channel-cache entry, a manually-forced action)
    /// and you want it gone without immediately reproducing it. The item becomes
    /// eligible for a normal, gate-respecting automatic re-detect afterward, same as any
    /// item with no stored record.
    /// </summary>
    /// <remarks>
    /// <paramref name="keep"/> ("Remove commercials" in the apps, for a scan that shouldn't
    /// have happened): removes only the Commercial segments (intros, recaps and the like
    /// stay), deletes the .edl this plugin placed beside the video, and records the item as
    /// having no commercials, so automatic detection leaves it alone. A forced scan
    /// (Detect with force) still re-scans it.
    /// </remarks>
    [HttpPost("Clear/{itemId}")]
    public async Task<ActionResult<ClearResponse>> Clear([FromRoute] Guid itemId, [FromQuery] bool keep, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new ClearResponse(false, $"No library item with ID {itemId}"));
        }

        if (keep)
        {
            try
            {
                var segments = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
                var commercials = await segments.GetSegmentsAsync(item, new[] { Jellyfin.Database.Implementations.Enums.MediaSegmentType.Commercial }, _libraryManager.GetLibraryOptions(item), filterByProvider: false).ConfigureAwait(false);
                foreach (var segment in commercials)
                {
                    await segments.DeleteSegmentAsync(segment.Id).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove commercial segments for {ItemId}", itemId);
                return Ok(new ClearResponse(false, $"Could not remove the commercials: {ex.Message}"));
            }

            if (item.Path is not null && DetectionManager.SiblingEdlPath(item.Path) is { } edl && System.IO.File.Exists(edl) && _store.IsOwnedEdl(edl))
            {
                try
                {
                    System.IO.File.Delete(edl);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete {Edl}", edl);
                }
            }

            _store.Save(itemId, new DetectionRecord(DetectionStatus.NoCommercials, Array.Empty<Jellyfin.Plugin.ComskipSegments.Comskip.CommercialBreak>(), DateTime.UtcNow, DetectionRecord.SourceCleared));
            _logger.LogInformation("Removed commercials from {Path}; automatic detection will leave it alone", item.Path);
            return Ok(new ClearResponse(true, "Commercials removed."));
        }

        try
        {
            var segmentManager = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
            await segmentManager.DeleteSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear published segments for {ItemId}", itemId);
            return Ok(new ClearResponse(false, $"Could not clear published segments: {ex.Message}"));
        }

        _store.Remove(itemId);
        _logger.LogInformation("Manually cleared commercial-skip segments and detection record for {Path}", item.Path);
        return Ok(new ClearResponse(true, "Cleared. No re-scan was queued."));
    }

    [HttpGet("Status/{itemId}")]
    public ActionResult<StatusResponse> Status([FromRoute] Guid itemId)
    {
        var inFlight = _manager.IsInFlight(itemId);

        if (_store.TryGet(itemId, out var record))
        {
            return Ok(new StatusResponse(inFlight, record.Status.ToString(), record.Breaks.Count, record.UpdatedUtc, record.Source ?? DetectionRecord.SourceComskip));
        }

        return Ok(new StatusResponse(inFlight, null, null, null));
    }

    private static CheckResult CheckBinary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new CheckResult(false, "not set");
        }

        if (!System.IO.File.Exists(path))
        {
            return new CheckResult(false, "file not found");
        }

        try
        {
            var mode = System.IO.File.GetUnixFileMode(path);
            const UnixFileMode AnyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & AnyExecute) == 0)
            {
                return new CheckResult(false, "found, but not executable");
            }
        }
        catch (Exception ex)
        {
            return new CheckResult(false, $"found, but could not check permissions: {ex.Message}");
        }

        return new CheckResult(true, "found and executable");
    }

    private static CheckResult CheckReadableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new CheckResult(false, "not set");
        }

        if (!System.IO.File.Exists(path))
        {
            return new CheckResult(false, "file not found");
        }

        try
        {
            using var stream = System.IO.File.OpenRead(path);
            return new CheckResult(true, "found and readable");
        }
        catch (Exception ex)
        {
            return new CheckResult(false, $"found, but not readable: {ex.Message}");
        }
    }

    private static CheckResult CheckWritableDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new CheckResult(false, "not set");
        }

        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".comskip-write-test-{Guid.NewGuid():N}");
            System.IO.File.WriteAllText(probe, string.Empty);
            System.IO.File.Delete(probe);
        }
        catch (Exception ex)
        {
            return new CheckResult(false, $"not writable: {ex.Message}");
        }

        return new CheckResult(true, "writable");
    }
}
