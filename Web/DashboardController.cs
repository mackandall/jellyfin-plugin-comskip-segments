using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Plugin.ComskipSegments.Editing;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// The segments dashboard: every recording this plugin knows, its commercial breaks to check and
/// edit, and "Cut commercials out". For administrators and the users allowed to edit recordings.
/// Pictures and keyframes come from the program-start endpoints; Re-run Comskip is Detect with force.
/// </summary>
[ApiController]
[Route("ComskipSegments/Dashboard")]
[Authorize]
[RecordingEditor]
public sealed class DashboardController : ControllerBase
{
    private readonly ILogger<DashboardController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly DetectionStore _detections;
    private readonly DetectionManager _manager;
    private readonly CommercialCutStore _cutStore;
    private readonly KeptOriginals _kept;
    private readonly CommercialCutService _cutter;
    private readonly CutTracker _cuts;
    private readonly TrimBackups _backups;
    private readonly IServiceProvider _serviceProvider;

    public DashboardController(
        ILogger<DashboardController> logger,
        ILibraryManager libraryManager,
        DetectionStore detections,
        DetectionManager manager,
        CommercialCutStore cutStore,
        KeptOriginals kept,
        CommercialCutService cutter,
        CutTracker cuts,
        TrimBackups backups,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _detections = detections;
        _manager = manager;
        _cutStore = cutStore;
        _kept = kept;
        _cutter = cutter;
        _cuts = cuts;
        _backups = backups;
        _serviceProvider = serviceProvider;
    }

    /// <summary>A break, in seconds from the start of the file.</summary>
    public sealed record Span(double Start, double End);

    /// <param name="Status">detected, none, failed, cut.</param>
    /// <param name="Source">comskip, edl, edited, cut, cleared.</param>
    public sealed record Row(
        Guid Id,
        string Name,
        string? Series,
        string? Episode,
        string Kind,
        DateTime? Recorded,
        double Seconds,
        IReadOnlyList<Span> Breaks,
        string Status,
        string Source,
        bool Busy,
        CutRow? Cut = null,
        CutResult? LastCut = null);

    /// <param name="OriginalBytes">The kept original's size (null: none kept any more).</param>
    /// <param name="UndoUntil">When the kept original goes (null: kept for good, or none kept).</param>
    public sealed record CutRow(double SecondsBefore, long? OriginalBytes, DateTime? UndoUntil);

    /// <param name="Settings">How cuts are made (for the Cut… confirmations).</param>
    /// <param name="Batch">The cuts waiting their turn and the one under way.</param>
    public sealed record ListResponse(IReadOnlyList<Row> Items, CutSettings? Settings = null, CutBatchState? Batch = null);

    public sealed record CutBatchRequest(IReadOnlyList<Guid>? Ids);

    public sealed record CutBatchResponse(int Queued, CutBatchState Batch);

    /// <param name="Edl">ours (this plugin's), other (another program's, never overwritten), none.</param>
    /// <param name="Others">Segments other than commercials (another plugin's intro, a program-start preview): shown, not edited here.</param>
    public sealed record Detail(
        Row Row,
        string? Path,
        string Edl,
        DateTime? Scanned,
        IReadOnlyList<OtherSegment> Others,
        bool Scanning,
        Cut? Cut,
        CutResult? LastCut,
        string? CutRefusal,
        CutSettings Settings,
        IReadOnlyList<Join>? Joins = null);

    /// <summary>
    /// Where a cut recording's kept parts meet, in seconds of the cut file, and how much was
    /// taken out there (also at the very start and end, when something was taken there).
    /// </summary>
    public sealed record Join(double At, double Removed);

    public sealed record OtherSegment(string Type, double Start, double End);

    /// <param name="UndoUntil">When the kept original goes (null: kept for good, or none kept).</param>
    public sealed record Cut(DateTime CutUtc, double SecondsBefore, int Breaks, bool CanUndo, DateTime? UndoUntil);

    /// <param name="Rounding">keep-ad or keep-show (see <see cref="PluginConfiguration.CutRounding"/>).</param>
    /// <param name="UndoDays">Days a cut can be undone when originals aren't kept for good (0: it can't).</param>
    public sealed record CutSettings(string Rounding, bool KeepOriginal, int UndoDays, bool WriteEdl);

    public sealed record SaveRequest(IReadOnlyList<Span>? Breaks, bool WriteEdl);

    [HttpGet]
    public ActionResult<ListResponse> List()
    {
        var cuts = _cutStore.All();
        var ids = _detections.All().Select(r => r.Key).Concat(cuts.Keys).Distinct();
        var rows = new List<Row>();
        foreach (var id in ids)
        {
            if (_libraryManager.GetItemById(id) is BaseItem item && item is Video)
            {
                rows.Add(RowOf(item, cuts.ContainsKey(id)));
            }
        }

        return Ok(new ListResponse(rows.OrderByDescending(r => r.Recorded).ToList(), Settings(), _cutter.Batch()));
    }

    [HttpGet("{itemId}")]
    public async Task<ActionResult<Detail>> Get([FromRoute] Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item)
        {
            return NotFound();
        }

        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var hasCut = _cutStore.TryGet(itemId, out var cutRecord);

        // Undone some other way (the item menu's Undo trim puts the original back too): not cut any more.
        if (hasCut && TrimBackups.Find(item.Path) is null && (!_detections.TryGet(itemId, out var now) || now.Source != DetectionRecord.SourceCut))
        {
            _cutStore.Remove(itemId);
            hasCut = false;
        }
        var edl = "none";
        if (item.Path is not null && DetectionManager.SiblingEdlPath(item.Path) is { } edlPath && System.IO.File.Exists(edlPath))
        {
            edl = _detections.IsOwnedEdl(edlPath) ? "ours" : "other";
        }

        Cut? cut = null;
        if (hasCut)
        {
            var backup = TrimBackups.Find(item.Path);
            cut = new Cut(cutRecord.CutUtc, cutRecord.SecondsBefore, cutRecord.BreaksBefore.Count, backup is not null, backup is null ? null : _backups.ExpiresUtc(backup));
        }

        return Ok(new Detail(
            RowOf(item, hasCut),
            item.Path,
            edl,
            _detections.TryGet(itemId, out var record) ? record.UpdatedUtc : null,
            await OthersAsync(item).ConfigureAwait(false),
            _manager.IsInFlight(itemId),
            cut,
            _cutter.LastResult(itemId),
            _cutter.Refusal(item),
            Settings(),
            hasCut ? JoinsOf(cutRecord) : null));
    }

    private static List<Join> JoinsOf(CommercialCutRecord record)
    {
        var joins = new List<Join>();
        var parts = record.Parts;
        if (parts.Count == 0)
        {
            return joins;
        }

        if (parts[0].Start > 0.5)
        {
            joins.Add(new Join(0, parts[0].Start));
        }

        var at = 0.0;
        for (var i = 0; i < parts.Count; i++)
        {
            at += parts[i].End - parts[i].Start;
            var next = i + 1 < parts.Count ? parts[i + 1].Start : record.SecondsBefore;
            if (next - parts[i].End > 0.5)
            {
                joins.Add(new Join(at, next - parts[i].End));
            }
        }

        return joins;
    }

    /// <summary>Saves the breaks as edited (empty: no commercials).</summary>
    [HttpPut("{itemId}/Breaks")]
    public async Task<ActionResult<EditResult>> Save([FromRoute] Guid itemId, [FromBody] SaveRequest request, CancellationToken ct)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item)
        {
            return NotFound();
        }

        var breaks = (request.Breaks ?? Array.Empty<Span>())
            .Select(b => new CommercialBreak((long)(b.Start * 1e7), (long)(b.End * 1e7)))
            .ToList();
        return Ok(await _cutter.SaveBreaksAsync(item, breaks, request.WriteEdl, ct).ConfigureAwait(false));
    }

    /// <summary>Starts cutting the commercials out (follow it in ComskipSegments/Cuts).</summary>
    [HttpPost("{itemId}/Cut")]
    public ActionResult<EditResult> CutOut([FromRoute] Guid itemId) =>
        _libraryManager.GetItemById(itemId) is BaseItem item ? Ok(_cutter.Cut(item)) : NotFound();

    /// <summary>"Cut…" and "Cut all shown…": puts the recordings in line to be cut, one at a time.</summary>
    [HttpPost("CutBatch")]
    public ActionResult<CutBatchResponse> CutBatch([FromBody] CutBatchRequest request)
    {
        var ids = (request.Ids ?? Array.Empty<Guid>()).Where(id => _libraryManager.GetItemById(id) is Video).ToList();
        var queued = _cutter.Queue(ids);
        _logger.LogInformation("{Count} recording(s) put in line to be cut", queued);
        return Ok(new CutBatchResponse(queued, _cutter.Batch()));
    }

    [HttpGet("CutBatch")]
    public ActionResult<CutBatchState> CutBatchState() => Ok(_cutter.Batch());

    /// <summary>Drops the cuts still waiting; the one under way carries on (its own Stop stops it).</summary>
    [HttpPost("CutBatch/Stop")]
    public ActionResult CutBatchStop() => Ok(new { Dropped = _cutter.StopBatch() });

    [HttpPost("{itemId}/UndoCut")]
    public async Task<ActionResult<EditResult>> UndoCut([FromRoute] Guid itemId) =>
        _libraryManager.GetItemById(itemId) is BaseItem item ? Ok(await _cutter.UndoAsync(item).ConfigureAwait(false)) : NotFound();

    /// <param name="Count">Kept originals in the library folders (from cuts and trims).</param>
    /// <param name="Bytes">The space they take.</param>
    /// <param name="Orphans">Of those, left without their recording (deleted once a day has passed).</param>
    /// <param name="KeptForGood">Originals are kept for good (the setting), not for some days.</param>
    /// <param name="UndoDays">Otherwise, how many days.</param>
    public sealed record OriginalsSummary(int Count, long Bytes, int Orphans, long OrphanBytes, bool KeptForGood, int UndoDays);

    /// <summary>How many kept originals there are and the space they take (a scan of the library folders, reused for 2 minutes).</summary>
    [HttpGet("Originals")]
    public ActionResult<OriginalsSummary> Originals()
    {
        var list = _kept.List(TimeSpan.FromMinutes(2));
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Ok(new OriginalsSummary(
            list.Count, list.Sum(o => o.Bytes), list.Count(o => o.Orphan), list.Where(o => o.Orphan).Sum(o => o.Bytes),
            cfg.AudioTrimKeepOriginal, Math.Max(0, cfg.TrimUndoDays)));
    }

    /// <summary>Deletes every kept original (none of those trims or cuts can be undone after), except for recordings being cut now.</summary>
    [HttpPost("Originals/DeleteAll")]
    public ActionResult<EditResult> DeleteAllOriginals()
    {
        var list = _kept.List(TimeSpan.Zero);
        int deleted = 0, skipped = 0;
        long bytes = 0;
        foreach (var o in list)
        {
            if (_libraryManager.FindByPath(o.RecordingPath, false) is { } item && (_cuts.IsBusy(item.Id) || _manager.IsInFlight(item.Id)))
            {
                skipped++;
                continue;
            }

            if (_kept.Delete(o.Path))
            {
                deleted++;
                bytes += o.Bytes;
            }
            else
            {
                skipped++;
            }
        }

        _kept.Changed();
        return Ok(new EditResult(deleted > 0 || list.Count == 0,
            $"Deleted {deleted} original(s): {bytes / 1e9:0.0} GB freed." + (skipped > 0 ? $" {skipped} left (being worked on, or couldn't be deleted)." : "")));
    }

    /// <summary>Deletes a cut recording's kept original, to free its space; the cut can't be undone after.</summary>
    [HttpPost("{itemId}/DeleteOriginal")]
    public ActionResult<EditResult> DeleteOriginal([FromRoute] Guid itemId) =>
        _libraryManager.GetItemById(itemId) is BaseItem item ? Ok(_cutter.DeleteOriginal(item)) : NotFound();

    /// <summary>A frame for the editor's frame-by-frame stepping: its time, keyframe or not, a JPEG (base64).</summary>
    public sealed record FrameDto(double T, bool Key, string Jpeg);

    public sealed record FramesResponse(IReadOnlyList<FrameDto> Frames);

    /// <summary>
    /// <paramref name="count"/> consecutive frames from <paramref name="seconds"/> on, each with its
    /// exact time, decoded in one go (the editor steps through them without asking again).
    /// </summary>
    /// <remarks>
    /// For playing: <paramref name="every"/> takes only every so many frames, and <paramref name="gpu"/>
    /// decodes on Jellyfin's GPU when it has one (stepping keeps the CPU, which is exact on dark fades).
    /// </remarks>
    [HttpGet("{itemId}/Frames")]
    public async Task<ActionResult<FramesResponse>> Frames(
        [FromRoute] Guid itemId, [FromQuery] double seconds, [FromQuery] int count = 120, [FromQuery] int width = 640,
        [FromQuery] int every = 1, [FromQuery] bool gpu = false, CancellationToken ct = default)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item.Path is null || !System.IO.File.Exists(item.Path))
        {
            return NotFound();
        }

        var encoder = _serviceProvider.GetRequiredService<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>();
        var frames = await FrameStrip.DecodeAsync(
            encoder.EncoderPath, encoder.ProbePath, item.Path, Math.Max(0, seconds), count, width, ct, Math.Clamp(every, 1, 64), gpu ? GpuDevice() : null).ConfigureAwait(false);
        return Ok(new FramesResponse(frames.Select(f => new FrameDto(f.T, f.Key, Convert.ToBase64String(f.Jpeg))).ToList()));
    }

    public sealed record OverviewResponse(bool Done, string? Error, IReadOnlyList<FrameDto> Frames);

    /// <summary>
    /// The recording's keyframe pictures from number <paramref name="from"/> on (see
    /// <see cref="OverviewService"/>); the first ask starts making them, later asks get the new ones.
    /// </summary>
    [HttpGet("{itemId}/Overview")]
    public ActionResult<OverviewResponse> Overview([FromRoute] Guid itemId, [FromQuery] int from = 0)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item.Path is null || !System.IO.File.Exists(item.Path))
        {
            return NotFound();
        }

        var snap = _serviceProvider.GetRequiredService<OverviewService>().Get(itemId, item.Path, from);
        return Ok(new OverviewResponse(snap.Done, snap.Error, snap.Frames.Select(f => new FrameDto(f.T, true, Convert.ToBase64String(f.Jpeg))).ToList()));
    }

    /// <summary>The nearest run of black frames after (<paramref name="forward"/>) or before <paramref name="seconds"/>, up to 10 minutes away.</summary>
    [HttpGet("{itemId}/Black")]
    public async Task<ActionResult<FrameStrip.BlackRun?>> Black([FromRoute] Guid itemId, [FromQuery] double seconds, [FromQuery] bool forward = true, CancellationToken ct = default)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item.Path is null || !System.IO.File.Exists(item.Path))
        {
            return NotFound();
        }

        var encoder = _serviceProvider.GetRequiredService<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>();
        var run = await FrameStrip.FindBlackAsync(encoder.EncoderPath, encoder.ProbePath, item.Path, seconds, forward, 600, GpuDevice(), ct).ConfigureAwait(false);
        return run is null ? NoContent() : Ok(run);
    }

    /// <summary>
    /// The recording's sound from <paramref name="seconds"/> on, as MP3 made while it's sent, for
    /// the editor's Play (an audio element plays it and keeps the pictures in time with it).
    /// </summary>
    [HttpGet("{itemId}/Audio")]
    public async Task Audio([FromRoute] Guid itemId, [FromQuery] double seconds)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item.Path is null || !System.IO.File.Exists(item.Path))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var encoder = _serviceProvider.GetRequiredService<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = encoder.EncoderPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-ss", Math.Max(0, seconds).ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", item.Path,
            "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "2", "-c:a", "libmp3lame", "-b:a", "160k", "-f", "mp3", "pipe:1"
        })
        {
            psi.ArgumentList.Add(a);
        }

        var ct = HttpContext.RequestAborted;
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
        {
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        Response.ContentType = "audio/mpeg";
        Response.Headers.CacheControl = "no-store";
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The editor stopped playing.
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    // The GPU Jellyfin transcodes with, when it's an Intel/AMD one on VAAPI (QSV sits on VAAPI on Linux).
    private string? GpuDevice()
    {
        try
        {
            var enc = _serviceProvider.GetRequiredService<MediaBrowser.Common.Configuration.IConfigurationManager>().GetEncodingOptions();
            var type = enc.HardwareAccelerationType.ToString();
            if (OperatingSystem.IsLinux() && (type.Equals("vaapi", StringComparison.OrdinalIgnoreCase) || type.Equals("qsv", StringComparison.OrdinalIgnoreCase)))
            {
                return string.IsNullOrWhiteSpace(enc.VaapiDevice) ? "/dev/dri/renderD128" : enc.VaapiDevice;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Jellyfin's hardware acceleration settings");
        }

        return null;
    }

    private Row RowOf(BaseItem item, bool cut)
    {
        var hasRecord = _detections.TryGet(item.Id, out var record);
        var status = cut && record?.Source == DetectionRecord.SourceCut ? "cut"
            : !hasRecord ? "none"
            : record!.Status switch
            {
                DetectionStatus.Detected => "detected",
                DetectionStatus.Failed => "failed",
                _ => "none"
            };
        var episode = item is Episode e && e.ParentIndexNumber is { } s && e.IndexNumber is { } n ? $"S{s:00}E{n:00}" : null;
        var breaks = hasRecord && record!.Status == DetectionStatus.Detected
            ? record.Breaks.Select(b => new Span(b.StartTicks / 1e7, b.EndTicks / 1e7)).ToList()
            : new List<Span>();
        return new Row(
            item.Id,
            item.Name,
            (item as Episode)?.SeriesName,
            episode,
            item is Episode ? "episode" : item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" : "video",
            item.DateCreated == default ? null : item.DateCreated,
            (item.RunTimeTicks ?? 0) / 1e7,
            breaks,
            status,
            record?.Source ?? (hasRecord ? DetectionRecord.SourceComskip : "none"),
            _cuts.IsBusy(item.Id) || _manager.IsInFlight(item.Id),
            status == "cut" ? CutRowOf(item) : null,
            _cutter.LastResult(item.Id));
    }

    private static CutSettings Settings()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return new CutSettings(cfg.CutRounding, cfg.AudioTrimKeepOriginal, cfg.AudioTrimKeepOriginal ? 0 : Math.Max(0, cfg.TrimUndoDays), cfg.PlaceEdlBesideRecording);
    }

    private CutRow? CutRowOf(BaseItem item)
    {
        if (!_cutStore.TryGet(item.Id, out var record))
        {
            return null;
        }

        var backup = TrimBackups.Find(item.Path);
        long? bytes = null;
        try
        {
            bytes = backup is null ? null : new System.IO.FileInfo(backup).Length;
        }
        catch (System.IO.IOException)
        {
        }

        return new CutRow(record.SecondsBefore, bytes, backup is null ? null : _backups.ExpiresUtc(backup));
    }

    private async Task<IReadOnlyList<OtherSegment>> OthersAsync(BaseItem item)
    {
        try
        {
            var segments = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
            var types = Enum.GetValues<MediaSegmentType>().Where(t => t != MediaSegmentType.Commercial).ToArray();
            var found = await segments.GetSegmentsAsync(item, types, _libraryManager.GetLibraryOptions(item), filterByProvider: false).ConfigureAwait(false);
            return found
                .OrderBy(s => s.StartTicks)
                .Select(s => new OtherSegment(s.Type.ToString(), s.StartTicks / 1e7, s.EndTicks / 1e7))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the other segments of {Path}", item.Path);
            return Array.Empty<OtherSegment>();
        }
    }
}
