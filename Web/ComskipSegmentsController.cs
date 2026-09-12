using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Detection;
using MediaBrowser.Common.Api;
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
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ComskipSegmentsController : ControllerBase
{
    private readonly ILogger<ComskipSegmentsController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly DetectionManager _manager;
    private readonly DetectionStore _store;
    private readonly IServiceProvider _serviceProvider;

    public ComskipSegmentsController(
        ILogger<ComskipSegmentsController> logger,
        ILibraryManager libraryManager,
        DetectionManager manager,
        DetectionStore store,
        IServiceProvider serviceProvider)
    {
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

    public sealed record StatusResponse(bool InFlight, string? Status, int? BreakCount, DateTime? UpdatedUtc);

    [HttpPost("Validate")]
    public ActionResult<ValidateResponse> Validate([FromBody] ValidateRequest request)
    {
        return Ok(new ValidateResponse(
            CheckBinary(request.ComskipPath?.Trim()),
            CheckReadableFile(request.ComskipIniPath?.Trim()),
            CheckWritableDirectory(request.WorkDirectory?.Trim())));
    }

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

        if (item.Path is null || !System.IO.File.Exists(item.Path))
        {
            return BadRequest(new DetectResponse(false, "Item has no resolvable file on disk"));
        }

        if (force)
        {
            try
            {
                var segmentManager = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
                await segmentManager.DeleteSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not clear existing segments before forced re-detect of {ItemId}", itemId);
            }

            _store.Remove(itemId);
        }

        _manager.Enqueue(itemId, force);

        // Enqueue() no-ops silently when a non-forced request is filtered by the
        // show/channel gate or the item is already complete, so check what actually
        // happened rather than always claiming success.
        if (_manager.IsInFlight(itemId))
        {
            _logger.LogInformation("Manual detection queued for {Path} (force={Force})", item.Path, force);
            return Ok(new DetectResponse(true, "Queued"));
        }

        var message = force
            ? "Queued" // forced enqueues land in the queue synchronously before this check
            : "Not queued: item is already complete, or filtered by the show/channel allow- or block-list. Check Force to bypass.";
        _logger.LogInformation("Manual detection for {Path} (force={Force}): {Message}", item.Path, force, message);

        return Ok(new DetectResponse(force, message));
    }

    [HttpGet("Status/{itemId}")]
    public ActionResult<StatusResponse> Status([FromRoute] Guid itemId)
    {
        var inFlight = _manager.IsInFlight(itemId);

        if (_store.TryGet(itemId, out var record))
        {
            return Ok(new StatusResponse(inFlight, record.Status.ToString(), record.Breaks.Count, record.UpdatedUtc));
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
