using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Plugin.ComskipSegments.ProgramStart;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Editing;

/// <summary>The outcome of saving, cutting or undoing; <see cref="Background"/>: carries on, follow it in the cut list.</summary>
public sealed record EditResult(bool Ok, string Message, bool Background = false);

/// <summary>How the last cut of a recording ended, for the dashboard (kept until the next one, or a restart).</summary>
public sealed record CutResult(bool Ok, string Message, DateTime FinishedUtc);

/// <param name="Waiting">Recordings waiting their turn to be cut, in order.</param>
/// <param name="Current">The one being cut now (null: none).</param>
public sealed record CutBatchState(IReadOnlyList<Guid> Waiting, Guid? Current);

/// <summary>
/// The segments dashboard's changes to a recording: its commercial breaks as edited by hand, and
/// "Cut commercials out", which removes them from the file itself (a fast cut: the video is
/// copied, not re-encoded, so each kept part starts on a keyframe; see
/// <see cref="PluginConfiguration.CutRounding"/>). The original is kept for Undo like a trim's,
/// and resume positions and a program-start marker move with the cut.
/// </summary>
public sealed class CommercialCutService
{
    private readonly ILogger<CommercialCutService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly DetectionStore _detections;
    private readonly ProgramStartStore _programStarts;
    private readonly RecordingTrimmer _trimmer;
    private readonly CutTracker _cuts;
    private readonly CommercialCutStore _store;
    private readonly TrimBackups _backups;
    private readonly ConcurrentDictionary<Guid, CutResult> _results = new();

    public CommercialCutService(
        ILogger<CommercialCutService> logger,
        IServiceProvider serviceProvider,
        ILibraryManager libraryManager,
        DetectionStore detections,
        ProgramStartStore programStarts,
        RecordingTrimmer trimmer,
        CutTracker cuts,
        CommercialCutStore store,
        TrimBackups backups)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _libraryManager = libraryManager;
        _detections = detections;
        _programStarts = programStarts;
        _trimmer = trimmer;
        _cuts = cuts;
        _store = store;
        _backups = backups;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public CutResult? LastResult(Guid itemId) => _results.TryGetValue(itemId, out var r) ? r : null;

    // "Cut" and "Cut all shown" on the Recordings list: cuts waiting their turn, done one at a time
    // so that a batch doesn't read and write every file at once.
    private readonly object _batchLock = new();
    private readonly List<Guid> _waiting = new();
    private Guid? _current;
    private bool _batchRunning;

    /// <summary>The cuts waiting their turn, and the one being done (null: none).</summary>
    public CutBatchState Batch()
    {
        lock (_batchLock)
        {
            return new CutBatchState(_waiting.ToList(), _current);
        }
    }

    /// <summary>Puts recordings in line to be cut, one after another; returns how many were added.</summary>
    public int Queue(IEnumerable<Guid> itemIds)
    {
        var added = 0;
        lock (_batchLock)
        {
            foreach (var id in itemIds)
            {
                if (id != _current && !_waiting.Contains(id))
                {
                    _waiting.Add(id);
                    _results.TryRemove(id, out _);
                    added++;
                }
            }

            if (!_batchRunning && _waiting.Count > 0)
            {
                _batchRunning = true;
                _ = Task.Run(RunBatchAsync);
            }
        }

        return added;
    }

    /// <summary>Drops the cuts still waiting (the one under way carries on); returns how many.</summary>
    public int StopBatch()
    {
        lock (_batchLock)
        {
            var n = _waiting.Count;
            _waiting.Clear();
            return n;
        }
    }

    private async Task RunBatchAsync()
    {
        while (true)
        {
            Guid id;
            lock (_batchLock)
            {
                if (_waiting.Count == 0)
                {
                    _current = null;
                    _batchRunning = false;
                    return;
                }

                id = _waiting[0];
                _waiting.RemoveAt(0);
                _current = id;
            }

            try
            {
                if (_libraryManager.GetItemById(id) is not BaseItem item)
                {
                    _results[id] = new CutResult(false, "The recording isn't in the library any more.", DateTime.UtcNow);
                    continue;
                }

                var started = Cut(item);
                if (!started.Ok)
                {
                    // Skipped (being watched, still recording, nothing to cut): said on its row.
                    _results[id] = new CutResult(false, started.Message, DateTime.UtcNow);
                    continue;
                }

                while (_cuts.IsBusy(id))
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queued commercial cut of {ItemId} failed", id);
                _results[id] = new CutResult(false, $"Unexpected error: {ex.Message}", DateTime.UtcNow);
            }
        }
    }

    /// <summary>
    /// The breaks as edited on the dashboard. Stored as "edited": automatic detection leaves them
    /// alone, only Re-run Comskip replaces them. With <paramref name="writeEdl"/>, the .edl beside
    /// the recording is rewritten too (not another program's).
    /// </summary>
    public async Task<EditResult> SaveBreaksAsync(BaseItem item, IReadOnlyList<CommercialBreak> breaks, bool writeEdl, CancellationToken ct)
    {
        if (_cuts.IsBusy(item.Id))
        {
            return new EditResult(false, "This recording is being cut or scanned; wait for that to finish.");
        }

        var sorted = breaks.OrderBy(b => b.StartTicks).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].EndTicks <= sorted[i].StartTicks || sorted[i].StartTicks < 0)
            {
                return new EditResult(false, $"Break {i + 1} ends before it starts.");
            }

            if (i > 0 && sorted[i].StartTicks < sorted[i - 1].EndTicks)
            {
                return new EditResult(false, $"Breaks {i} and {i + 1} overlap.");
            }
        }

        var status = sorted.Count > 0 ? DetectionStatus.Detected : DetectionStatus.NoCommercials;
        _detections.Save(item.Id, new DetectionRecord(status, sorted, DateTime.UtcNow, DetectionRecord.SourceEdited));

        var note = string.Empty;
        if (writeEdl && item.Path is not null && DetectionManager.SiblingEdlPath(item.Path) is { } edl)
        {
            try
            {
                if (File.Exists(edl) && !_detections.IsOwnedEdl(edl))
                {
                    note = " The .edl beside it belongs to another program and was left as it was.";
                }
                else
                {
                    EdlWriter.Write(edl, sorted);
                    _detections.MarkOwnedEdl(edl);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not write {Edl}", edl);
                note = $" The .edl couldn't be written: {ex.Message}";
            }
        }

        await PublishAsync(item, ct).ConfigureAwait(false);
        _logger.LogInformation("Commercial breaks of {Path} edited by hand: {Count}", item.Path, sorted.Count);
        return new EditResult(true, (sorted.Count == 0 ? "Saved: no commercials." : $"Saved {sorted.Count} break(s).") + note);
    }

    /// <summary>Why a cut or undo can't start now, or null.</summary>
    public string? Refusal(BaseItem item)
    {
        if (item.Path is null || !File.Exists(item.Path))
        {
            return "The recording's file isn't there.";
        }

        if (_cuts.IsBusy(item.Id))
        {
            return "This recording is already being cut or scanned; wait for that to finish.";
        }

        if (_serviceProvider.GetService<ISessionManager>()?.Sessions.Any(s => s.NowPlayingItem?.Id == item.Id) == true)
        {
            return "Someone is watching this recording. Try again when they've stopped.";
        }

        if (_serviceProvider.GetService<IRecordingsManager>()?.GetActiveRecordingInfo(item.Path) is not null)
        {
            return "This is still recording. Try again once it has finished.";
        }

        return null;
    }

    /// <summary>"Cut commercials out": starts the cut and returns; it carries on in the background.</summary>
    public EditResult Cut(BaseItem item)
    {
        if (Refusal(item) is { } why)
        {
            return new EditResult(false, why);
        }

        if (!_detections.TryGet(item.Id, out var record) || record.Status != DetectionStatus.Detected || record.Breaks.Count == 0)
        {
            return new EditResult(false, "There are no commercial breaks to cut.");
        }

        if (!_cuts.TryStart(item, "commercials", out var job))
        {
            return new EditResult(false, "This recording is already being cut or scanned; wait for that to finish.");
        }

        _results.TryRemove(item.Id, out _);
        _ = Task.Run(async () =>
        {
            CutResult result;
            try
            {
                var r = await CutCoreAsync(item, record, job).ConfigureAwait(false);
                result = new CutResult(r.Ok, r.Message, DateTime.UtcNow);
            }
            catch (OperationCanceledException)
            {
                result = new CutResult(false, "Stopped; the recording is as it was.", DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Commercial cut of {Path} failed", item.Path);
                result = new CutResult(false, $"Unexpected error: {ex.Message}", DateTime.UtcNow);
            }
            finally
            {
                _cuts.Finish(job);
            }

            _results[item.Id] = result;
        });

        return new EditResult(true, $"Cutting {record.Breaks.Count} break(s) out.", Background: true);
    }

    private async Task<EditResult> CutCoreAsync(BaseItem item, DetectionRecord record, CutJob job)
    {
        var ct = job.Cancel.Token;
        var path = item.Path!;
        var encoder = _serviceProvider.GetRequiredService<IMediaEncoder>();
        job.Detail = "Finding the keyframes";
        var (_, duration) = await AudioChannelScanner.ProbeFormatTimes(encoder.ProbePath, path, ct).ConfigureAwait(false);
        if (duration <= 0)
        {
            return new EditResult(false, "Couldn't read the recording's length.");
        }

        var wanted = CutMap.Between(record.Breaks.Select(b => (b.StartTicks / 1e7, b.EndTicks / 1e7)), duration);
        var parts = await OnKeyframesAsync(encoder.ProbePath, path, wanted.Parts, ct).ConfigureAwait(false);
        var hadBackup = TrimBackups.Find(path) is not null;
        var cfg = Config;
        _logger.LogInformation(
            "Cutting {Count} break(s) out of {Path}: keeping {Parts}",
            record.Breaks.Count, path, string.Join(", ", parts.Select(p => $"{p.Start:F2}-{p.End:F2}")));

        var result = await _trimmer.ApplyPartsAsync(encoder.EncoderPath, cfg.WorkDirectory, path, parts, duration, cfg.AudioTrimKeepOriginal, ct, job).ConfigureAwait(false);
        var outcome = result.Outcome;
        if (!outcome.Success)
        {
            _logger.LogWarning("Commercial cut of {Path} not done: {Message}", path, outcome.Message);
            return new EditResult(false, $"The cut didn't finish ({outcome.Message}). Nothing was changed.");
        }

        // The file is cut: from here, bring everything that pointed into it in line. Jellyfin
        // re-reading the changed file prunes every segment provider's data for it (our detection
        // record included), so the records are written after that.
        var foreignAside = PutEdlAside(path);
        var map = new CutMap(result.Kept);
        _store.Save(item.Id, new CommercialCutRecord(result.Kept, record.Breaks, record.Source, duration, !hadBackup, foreignAside, DateTime.UtcNow, path));
        await RefreshAsync(item).ConfigureAwait(false);
        _detections.Save(item.Id, new DetectionRecord(DetectionStatus.NoCommercials, Array.Empty<CommercialBreak>(), DateTime.UtcNow, DetectionRecord.SourceCut));
        MoveProgramStart(item, map.Forward);
        await MoveResumePositionsAsync(item, map.Forward).ConfigureAwait(false);
        await PublishAsync(item, CancellationToken.None).ConfigureAwait(false);

        var removed = duration - map.KeptSeconds;
        _logger.LogInformation("Cut {Removed:F1}s of commercials out of {Path}: {Message}", removed, path, outcome.Message);
        return new EditResult(true, $"Cut {record.Breaks.Count} break(s) out: {Clock(duration)} → {Clock(map.KeptSeconds)}.");
    }

    /// <summary>
    /// Starts each part on a keyframe: at or before the end of the break (keeping a little ad), or
    /// the one after it (losing a little show). A break shorter than the gap between keyframes
    /// can't be cut on its own, so the parts either side join up.
    /// </summary>
    private async Task<List<KeptPart>> OnKeyframesAsync(string probe, string path, IReadOnlyList<KeptPart> parts, CancellationToken ct)
    {
        var keepShow = Config.CutRounding == PluginConfiguration.CutKeepShow;
        var result = new List<KeptPart>();
        foreach (var part in parts)
        {
            var start = part.Start;
            if (start > 0)
            {
                double? key;
                if (keepShow)
                {
                    // Clear of the break by more than the part's sound runs ahead of its picture (see ApplyPartsAsync).
                    var keys = await Keyframes.AroundAsync(probe, path, start, 20, ct).ConfigureAwait(false);
                    key = keys.Where(k => k >= start + 0.3).Cast<double?>().FirstOrDefault();
                }
                else
                {
                    key = await Keyframes.AtOrBeforeAsync(probe, path, start, ct).ConfigureAwait(false);
                }

                if (key is { } k)
                {
                    start = k;
                }
                else
                {
                    _logger.LogWarning("No keyframe near {Start:F2}s in {Path}; cutting there as marked", part.Start, path);
                }
            }

            if (result.Count > 0 && start <= result[^1].End)
            {
                result[^1] = result[^1] with { End = part.End };
            }
            else if (part.End - start >= 1)
            {
                result.Add(new KeptPart(start, part.End));
            }
        }

        return result;
    }

    /// <summary>
    /// "Undo cut": puts the original back. When the cut kept it, the breaks come back as they were;
    /// when an earlier trim already had (the original is as recorded, before that trim too), the
    /// trim's own undo does it and commercials are looked for again.
    /// </summary>
    public async Task<EditResult> UndoAsync(BaseItem item)
    {
        if (!_store.TryGet(item.Id, out var record))
        {
            return new EditResult(false, "This recording's commercials weren't cut here.");
        }

        if (TrimBackups.Find(item.Path) is not { } backup)
        {
            return new EditResult(false, "There's no kept original to put back: its undo time is up, or it wasn't kept.");
        }

        if (Refusal(item) is { } why)
        {
            return new EditResult(false, why);
        }

        var map = new CutMap(record.Parts);
        if (!record.OwnBackup)
        {
            var undone = await _serviceProvider.GetRequiredService<ProgramStartService>().UndoTrimAsync(item).ConfigureAwait(false);
            if (!undone.Ok)
            {
                return new EditResult(false, undone.Message);
            }

            // Resume positions stay: the original is from before that trim as well, so the cut's map doesn't fit it.
            _store.Remove(item.Id);
            RestoreEdl(item.Path!, record.ForeignEdlAside, null);
            return new EditResult(true, "The original recording is back, from before any trim too. Commercials are looked for again.");
        }

        if (!_cuts.TryStart(item, "commercials", out var job))
        {
            return new EditResult(false, "This recording is already being cut or scanned; wait for that to finish.");
        }

        try
        {
            job.Phase = CutJob.PhaseFinishing;
            File.Move(backup, item.Path!, overwrite: true);
            _backups.Forget(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not undo the commercial cut of {Path}", item.Path);
            return new EditResult(false, $"Couldn't put the original back: {ex.Message}");
        }
        finally
        {
            _cuts.Finish(job);
        }

        // As after a cut: Jellyfin's re-read prunes the segment data, so the breaks go back after it.
        _store.Remove(item.Id);
        await RefreshAsync(item).ConfigureAwait(false);
        _detections.Save(item.Id, new DetectionRecord(DetectionStatus.Detected, record.BreaksBefore, DateTime.UtcNow, record.SourceBefore));
        RestoreEdl(item.Path!, record.ForeignEdlAside, record.BreaksBefore);
        MoveProgramStart(item, map.Back);
        await MoveResumePositionsAsync(item, map.Back).ConfigureAwait(false);
        await PublishAsync(item, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Commercial cut undone: {Path} is back to the original with {Count} break(s)", item.Path, record.BreaksBefore.Count);
        return new EditResult(true, $"The original is back, with its {record.BreaksBefore.Count} commercial break(s).");
    }

    /// <summary>
    /// "Delete original": frees the space the kept original takes before its time is up. The
    /// recording stays cut; it just can't be undone any more.
    /// </summary>
    public EditResult DeleteOriginal(BaseItem item)
    {
        if (!_store.TryGet(item.Id, out var record))
        {
            return new EditResult(false, "This recording's commercials weren't cut here.");
        }

        if (TrimBackups.Find(item.Path) is not { } backup)
        {
            return new EditResult(false, "The original is already gone.");
        }

        if (_cuts.IsBusy(item.Id))
        {
            return new EditResult(false, "This recording is being cut or scanned; wait for that to finish.");
        }

        try
        {
            var bytes = new FileInfo(backup).Length;
            File.Delete(backup);
            var edl = TrimBackups.EdlFor(backup);
            if (File.Exists(edl))
            {
                File.Delete(edl);
            }

            _backups.Forget(backup);
            _serviceProvider.GetService<KeptOriginals>()?.Changed();
            _store.Save(item.Id, record with { ForeignEdlAside = null });
            _logger.LogInformation("Deleted the kept original {Path} ({Bytes} bytes) on request", backup, bytes);
            return new EditResult(true, $"The original is deleted: {bytes / 1e9:0.0} GB freed. The cut can't be undone now.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the kept original {Path}", backup);
            return new EditResult(false, $"Couldn't delete the original: {ex.Message}");
        }
    }

    /// <summary>
    /// The .edl beside a cut recording no longer matches it. This plugin's own is deleted (its
    /// breaks are in the cut record). Another program's goes with the kept original, named after
    /// it (returned), so it's never read again, Undo puts it back, and it expires, moves and is
    /// deleted along with the original. With no original kept there's no Undo, so it's deleted.
    /// </summary>
    private string? PutEdlAside(string path)
    {
        if (DetectionManager.SiblingEdlPath(path) is not { } edl || !File.Exists(edl))
        {
            return null;
        }

        try
        {
            if (_detections.IsOwnedEdl(edl))
            {
                File.Delete(edl);
                return null;
            }

            if (TrimBackups.Find(path) is not { } backup)
            {
                File.Delete(edl);
                _logger.LogInformation("Deleted {Edl}: it no longer matches the cut recording, and no original was kept to undo to", edl);
                return null;
            }

            var aside = TrimBackups.EdlFor(backup);
            File.Move(edl, aside, overwrite: true);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not move the .edl beside {Path} out of the way", path);
            return null;
        }
    }

    private void RestoreEdl(string path, string? foreignAside, IReadOnlyList<CommercialBreak>? breaks)
    {
        if (DetectionManager.SiblingEdlPath(path) is not { } edl)
        {
            return;
        }

        try
        {
            if (foreignAside is not null && File.Exists(foreignAside))
            {
                File.Move(foreignAside, edl, overwrite: true);
            }
            else if (breaks is not null && Config.PlaceEdlBesideRecording && !File.Exists(edl))
            {
                EdlWriter.Write(edl, breaks);
                _detections.MarkOwnedEdl(edl);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not put the .edl beside {Path} back", path);
        }
    }

    private void MoveProgramStart(BaseItem item, Func<double, double> move)
    {
        if (_programStarts.TryGet(item.Id, out var start) && start.Action == PluginConfiguration.ProgramStartMarker && start.StartSeconds > 0)
        {
            _programStarts.Save(item.Id, start with { StartSeconds = move(start.StartSeconds), UpdatedUtc = DateTime.UtcNow });
        }
    }

    /// <summary>Everyone part-way through the recording carries on from the same scene, not the same minute.</summary>
    private async Task MoveResumePositionsAsync(BaseItem item, Func<double, double> move)
    {
        try
        {
            var users = _serviceProvider.GetRequiredService<IUserManager>();
            var userData = _serviceProvider.GetRequiredService<IUserDataManager>();
            foreach (var user in users.GetUsers())
            {
                var data = userData.GetUserData(user, item);
                if (data is null || data.PlaybackPositionTicks <= 0)
                {
                    continue;
                }

                data.PlaybackPositionTicks = (long)(move(data.PlaybackPositionTicks / 1e7) * 1e7);
                userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not move the resume positions of {Path} after a cut", item.Path);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // The file changed under the library item (new length): have Jellyfin read it again now.
    private async Task RefreshAsync(BaseItem item)
    {
        try
        {
            var fileSystem = _serviceProvider.GetService<MediaBrowser.Model.IO.IFileSystem>();
            if (fileSystem is null)
            {
                return;
            }

            await item.RefreshMetadata(
                new MediaBrowser.Controller.Providers.MetadataRefreshOptions(new MediaBrowser.Controller.Providers.DirectoryService(fileSystem))
                {
                    MetadataRefreshMode = MediaBrowser.Controller.Providers.MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MediaBrowser.Controller.Providers.MetadataRefreshMode.ValidationOnly,
                    ReplaceAllMetadata = false,
                    ForceSave = true,
                    EnableRemoteContentProbe = true
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh {Path} after it changed", item.Path);
        }
    }

    // Re-asks the segment providers so players see the change now. After a cut, Jellyfin's
    // re-read has already dropped every segment (other plugins' too, whose times are off now).
    private async Task PublishAsync(BaseItem item, CancellationToken ct)
    {
        try
        {
            var segments = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
            await segments.RunSegmentPluginProviders(item, _libraryManager.GetLibraryOptions(item), true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Segments of {Path} could not be refreshed", item.Path);
        }
    }

    private static string Clock(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
}
