using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ComskipSegments.Detection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Providers;

// ============================================================================
//  VERSION-SENSITIVE FILE — this is the ONLY place the Jellyfin API surface for
//  media segments is touched. If the plugin fails to compile against your server,
//  it will almost certainly be here. To confirm the exact contract for your build,
//  open the IMediaSegmentProvider interface in your server's MediaBrowser.Controller.dll
//  (or the matching tag at github.com/jellyfin/jellyfin under
//   MediaBrowser.Controller/MediaSegments/IMediaSegmentProvider.cs) and match:
//    - method name / return type of GetMediaSegments
//    - the request type (MediaSegmentGenerationRequest) and its ItemId member
//    - the Supports return type (ValueTask<bool> on 10.10/10.11/12.0)
//  Everything else in the project is plain .NET and won't move between versions.
//
//  Matched against Jellyfin 12.0.0 (Jellyfin.Controller 12.0.0, net10.0). Changes
//  from the 10.11 contract this file was first written for:
//    - MediaSegmentGenerationRequest namespace: MediaBrowser.Model.MediaSegments
//      -> MediaBrowser.Model
//    - MediaSegmentType namespace: MediaBrowser.Model.MediaSegments
//      -> Jellyfin.Database.Implementations.Enums
//    - new interface member: CleanupExtractedData(Guid, CancellationToken) -> Task
// ============================================================================

/// <summary>
/// Serves already-computed commercial segments from our store. It never runs Comskip
/// itself — detection is done out of band by <see cref="DetectionManager"/>. If an item
/// hasn't been processed yet, this quietly enqueues it and returns nothing for now, so
/// the Media Segment Scan stays fast and the results appear on the next pass.
/// </summary>
public sealed class ComskipSegmentProvider : IMediaSegmentProvider
{
    private readonly ILogger<ComskipSegmentProvider> _logger;
    private readonly DetectionStore _store;
    private readonly DetectionManager _manager;

    public ComskipSegmentProvider(
        ILogger<ComskipSegmentProvider> logger,
        DetectionStore store,
        DetectionManager manager)
    {
        _logger = logger;
        _store = store;
        _manager = manager;
    }

    public string Name => "Comskip Commercial Segments";

    public ValueTask<bool> Supports(BaseItem item)
    {
        // Only video items with a real file are candidates. Keep this permissive;
        // the actual "is this a recording / is it whitelisted" gating happens at
        // enqueue time (RecordingWatcher and the scan task), not here.
        var ok = item is Video && item.IsFileProtocol;
        return ValueTask.FromResult(ok);
    }

    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var itemId = request.ItemId;

        if (_store.TryGet(itemId, out var record) && record.Status == DetectionStatus.Detected)
        {
            var segments = new List<MediaSegmentDto>(record.Breaks.Count);
            foreach (var b in record.Breaks)
            {
                segments.Add(new MediaSegmentDto
                {
                    ItemId = itemId,
                    Type = MediaSegmentType.Commercial,
                    StartTicks = b.StartTicks,
                    EndTicks = b.EndTicks
                });
            }

            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(segments);
        }

        // Not yet processed (and not a known Failed/NoCommercials result): kick off
        // detection out of band and return empty. Enqueue() no-ops if it's already
        // completed or in flight (and applies the show/folder filter), so this is safe
        // to call on every scan. Skipped entirely when the user has turned off
        // auto-enqueue from Jellyfin's own Media Segment Scan.
        if (!_store.IsCompleted(itemId)
            && (Plugin.Instance?.Configuration.EnableMediaSegmentScanEnqueue ?? true))
        {
            _manager.Enqueue(itemId);
        }

        return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(Array.Empty<MediaSegmentDto>());
    }

    /// <summary>
    /// Called by Jellyfin when it prunes extracted segment data for an item. Drop our
    /// stored detection record so the item is re-detected from scratch on the next scan
    /// instead of serving stale breaks.
    /// </summary>
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        _store.Remove(itemId);
        return Task.CompletedTask;
    }
}
