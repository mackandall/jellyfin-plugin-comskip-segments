using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.ProgramStart;

/// <summary>The outcome of setting or finding a program's start.</summary>
public sealed record ProgramStartResult(bool Ok, bool Trimmed, double? StartSeconds, string Message)
{
    /// <summary>A trim carries on in the background (follow it in the cut list).</summary>
    public bool Background { get; init; }
}

/// <summary>
/// Where a recording's program really starts, past the promos a channel runs before it, for
/// channels the audio-format check can't help with (Starz is 5.1 or stereo throughout).
///
/// Three ways in: someone pauses on the first frame of the program ("Starts here (this recording)");
/// someone pauses on the card the channel shows just before it ("Remember card (all recordings)"), which
/// is kept so later recordings find their start by themselves; or that search runs on its own
/// after each recording. The start is then applied per <see cref="PluginConfiguration.ProgramStartAction"/>:
/// a Preview segment over the promos (default; the provider serves it), or a trim of the file.
/// Has no dependency on the detection manager (that one calls this): callers re-run Comskip
/// after a trim themselves, as with the audio-format trim.
/// </summary>
public sealed class ProgramStartService
{
    private readonly ILogger<ProgramStartService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly ProgramStartStore _store;
    private readonly RecordingTrimmer _trimmer;
    private readonly CutTracker _cuts;
    private readonly SemaphoreSlim _automaticGate = new(1, 1);

    public ProgramStartService(
        ILogger<ProgramStartService> logger,
        IServiceProvider serviceProvider,
        ILibraryManager libraryManager,
        ProgramStartStore store,
        RecordingTrimmer trimmer,
        CutTracker cuts)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _libraryManager = libraryManager;
        _store = store;
        _trimmer = trimmer;
        _cuts = cuts;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static ProgramStartResult Busy() =>
        new(false, false, null, "Already working on this recording; wait for that to finish.");

    /// <summary>"Starts here (this recording)": the start is exactly <paramref name="seconds"/>.</summary>
    /// <remarks>
    /// <paramref name="keyframe"/>: <paramref name="seconds"/> is a keyframe picked in an editor
    /// (see <see cref="AudioTrim.Keyframes"/>); a trim then starts exactly there, with no lead kept.
    /// </remarks>
    public Task<ProgramStartResult> SetAsync(BaseItem item, double seconds, bool keyframe = false) =>
        Manual(item, job => ApplyAsync(item, seconds, ProgramStartRecord.SourceManual, null, $"marked at {Clock(seconds)}", job, background: true, exact: keyframe));

    /// <summary>
    /// "Remember card (all recordings)": keeps the frame at <paramref name="seconds"/> as a card, finds
    /// where the card ends around it, and applies the program start after it.
    /// </summary>
    public Task<ProgramStartResult> RememberCardAsync(BaseItem item, double seconds, string? label, string? channel) =>
        Manual(item, job => RememberCardCoreAsync(item, seconds, label, channel, job));

    /// <summary>"Find program start": looks for a remembered card in the opening minutes.</summary>
    public Task<ProgramStartResult> FindAsync(BaseItem item) =>
        Manual(item, job => FindCoreAsync(item, automatic: false, job, background: true));

    /// <summary>
    /// After a recording: looks for a remembered card (leaves alone an item that already has a
    /// start, or was looked at). One at a time — a library scan can ask for every recording at
    /// once — and a trim here runs to the end before returning, so Comskip sees the final file.
    /// </summary>
    public async Task<ProgramStartResult> FindAutomaticAsync(BaseItem item, CancellationToken ct)
    {
        await _automaticGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!WantsAutomatic(item))
            {
                return new ProgramStartResult(false, false, null, "nothing to do");
            }

            if (!_cuts.TryStart(item, "program-start", out var job))
            {
                return Busy();
            }

            try
            {
                return await FindCoreAsync(item, automatic: true, job, background: false).ConfigureAwait(false);
            }
            finally
            {
                _cuts.Finish(job);
            }
        }
        finally
        {
            _automaticGate.Release();
        }
    }

    /// <summary>Whether an automatic search would do anything for this item.</summary>
    /// <summary>The program start of this recording is known (marked, found by a card, or trimmed to).</summary>
    public bool HasStart(Guid itemId) => _store.TryGet(itemId, out _);

    public bool WantsAutomatic(BaseItem item) =>
        Config.ProgramStartAutomatic && _store.Cards.Count > 0 && !_store.TryGet(item.Id, out _) && !_cuts.IsBusy(item.Id);

    /// <summary>Drops the item's program start (a marker goes; a trim can't be undone here).</summary>
    public async Task<bool> ClearAsync(BaseItem item, CancellationToken ct)
    {
        if (!_store.Remove(item.Id))
        {
            return false;
        }

        await PublishAsync(item, ct).ConfigureAwait(false);
        return true;
    }

    public bool RemoveCard(Guid id) => _store.RemoveCard(id);

    // A click: claims the recording (refused while anything else may rewrite it), and lets the
    // job go when done — unless a trim took it over in the background.
    private async Task<ProgramStartResult> Manual(BaseItem item, Func<CutJob, Task<ProgramStartResult>> work)
    {
        if (!_cuts.TryStart(item, "program-start", out var job))
        {
            return Busy();
        }

        var handedOff = false;
        try
        {
            var result = await work(job).ConfigureAwait(false);
            handedOff = result.Background;
            return result;
        }
        catch (OperationCanceledException)
        {
            return new ProgramStartResult(false, false, null, "Stopped.");
        }
        catch (Exception ex)
        {
            // Logged here: the endpoint may have answered already ("Still looking…").
            _logger.LogError(ex, "Program start failed for {Path}", item.Path);
            return new ProgramStartResult(false, false, null, $"Unexpected error: {ex.Message}");
        }
        finally
        {
            if (!handedOff)
            {
                _cuts.Finish(job);
            }
        }
    }

    private async Task<ProgramStartResult> RememberCardCoreAsync(BaseItem item, double seconds, string? label, string? channel, CutJob job)
    {
        var ct = job.Cancel.Token;
        var ffmpeg = Ffmpeg();
        var frame = await CardFrames.GrabAsync(ffmpeg, item.Path!, seconds, ct).ConfigureAwait(false);
        if (frame is null)
        {
            return new ProgramStartResult(false, false, null, "Couldn't read a frame there.");
        }

        if (CardFrames.IsBlack(frame))
        {
            return new ProgramStartResult(false, false, null, "That frame is black; pause on the card itself.");
        }

        var card = new StartCard(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(label) ? (channel ?? item.Name) + " card" : label.Trim(),
            string.IsNullOrWhiteSpace(channel) ? null : channel.Trim(),
            Convert.ToBase64String(frame),
            item.Path,
            seconds,
            DateTime.UtcNow);

        // Find the card's run around the paused frame (it can be on screen for several
        // seconds, and the program starts after its fade and the black that follows).
        var from = Math.Max(0, seconds - 30);
        var frames = await CardFrames.SampleAsync(ffmpeg, item.Path!, from, 75, ct).ConfigureAwait(false);
        var hit = LastRun(frames, new[] { frame }, Config.ProgramStartMatchThreshold, around: (int)Math.Round((seconds - from) * CardFrames.FramesPerSecond));
        if (hit is null)
        {
            return new ProgramStartResult(false, false, null, "Couldn't follow the card in the recording; nothing was kept.");
        }

        // The same card again (another film's rating card, say): don't keep a second copy.
        var duplicate = _store.Cards.FirstOrDefault(c => CardFrames.Similarity(Convert.FromBase64String(c.Pixels), frame) >= 0.95);
        if (duplicate is null)
        {
            _store.AddCard(card);
        }
        else
        {
            card = duplicate;
        }

        var start = from + (CardFrames.StartAfterCard(frames, hit.Value.End) / (double)CardFrames.FramesPerSecond);
        _logger.LogInformation("Program start: remembered card \"{Label}\" from {Path} at {Seconds:F1}s", card.Label, item.Path, seconds);
        // Always a skip marker here, whatever the setting: this is where the card is checked
        // (play it, re-teach it, or Forget it). Trims come from "Find program start" and the
        // automatic search once the card is trusted — a first trim can't be taken back.
        return await ApplyAsync(
            item, start, ProgramStartRecord.SourceCard, card.Id,
            duplicate is null ? $"after the card at {Clock(seconds)}; card remembered" : $"after the card at {Clock(seconds)}; already remembered as \"{duplicate.Label}\"",
            job, background: true, markerOnly: true).ConfigureAwait(false);
    }

    private async Task<ProgramStartResult> FindCoreAsync(BaseItem item, bool automatic, CutJob job, bool background)
    {
        var cards = _store.Cards;
        if (cards.Count == 0)
        {
            return new ProgramStartResult(false, false, null, "No program-start cards have been remembered yet.");
        }

        var cfg = Config;
        var frames = await CardFrames.SampleAsync(Ffmpeg(), item.Path!, 0, Math.Clamp(cfg.ProgramStartScanMinutes, 1, 60) * 60, job.Cancel.Token).ConfigureAwait(false);
        var pictures = cards.Select(c => Convert.FromBase64String(c.Pixels)).ToList();
        var hit = LastRun(frames, pictures, cfg.ProgramStartMatchThreshold, around: null);
        if (hit is null)
        {
            if (automatic)
            {
                // Noted, so automatic runs don't decode the opening again on every scan.
                _store.Save(item.Id, new ProgramStartRecord(0, ProgramStartRecord.SourceNone, null, string.Empty, "no card found", DateTime.UtcNow));
            }

            return new ProgramStartResult(false, false, null, $"No remembered card in the first {cfg.ProgramStartScanMinutes} minutes.");
        }

        var (_, end, best, cardIndex) = hit.Value;
        var start = CardFrames.StartAfterCard(frames, end) / (double)CardFrames.FramesPerSecond;
        var card = cards[cardIndex];
        _logger.LogInformation("Program start: card \"{Label}\" found in {Path} (score {Score:F2}); program starts at {Start:F1}s", card.Label, item.Path, best, start);
        return await ApplyAsync(
            item, start, automatic ? ProgramStartRecord.SourceCardAuto : ProgramStartRecord.SourceCard, card.Id,
            $"after \"{card.Label}\" (match {best:F2})", job, background).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks or trims (per the setting). A trim with <paramref name="background"/> takes the
    /// job over and returns at once ("Cutting from …"); the clients follow it from the cut list.
    /// </summary>
    private async Task<ProgramStartResult> ApplyAsync(BaseItem item, double start, string source, Guid? cardId, string how, CutJob job, bool background, bool markerOnly = false, bool exact = false)
    {
        var cfg = Config;
        if (start < 1)
        {
            return new ProgramStartResult(false, false, start, "The program starts at the very beginning; nothing to skip.");
        }

        if (markerOnly || cfg.ProgramStartAction != PluginConfiguration.ProgramStartTrim)
        {
            _store.Save(item.Id, new ProgramStartRecord(start, source, cardId, PluginConfiguration.ProgramStartMarker, how, DateTime.UtcNow));
            await PublishAsync(item, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Program start: {Path} starts at {Start:F1}s ({How}); marked as a preview", item.Path, start, how);
            return new ProgramStartResult(true, false, start, $"Program starts at {Clock(start)} ({how}); the part before it is marked to skip.");
        }

        // A little lead kept, as for the audio-format trim: never clip the program itself. Not
        // for a keyframe someone picked: they saw the frame the recording will start on.
        var lead = exact ? start : Math.Max(0, start - cfg.AudioTrimPaddingSeconds);
        if (!background)
        {
            return await TrimAsync(item, start, lead, source, cardId, how, job).ConfigureAwait(false);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var r = await TrimAsync(item, start, lead, source, cardId, how, job).ConfigureAwait(false);
                if (r.Trimmed)
                {
                    await RedetectAfterTrimAsync(item).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Program-start trim failed for {Path}", item.Path);
            }
            finally
            {
                _cuts.Finish(job);
            }
        });
        return new ProgramStartResult(true, false, start, $"Cutting the first {Clock(lead)} ({how}). The recording shows the progress; Stop is offered until the cut copy goes in.") { Background = true };
    }

    private async Task<ProgramStartResult> TrimAsync(BaseItem item, double start, double lead, string source, Guid? cardId, string how, CutJob job)
    {
        var cfg = Config;
        var encoder = _serviceProvider.GetService<IMediaEncoder>();
        var (_, total) = await AudioChannelScanner.ProbeFormatTimes(encoder!.ProbePath, item.Path!, job.Cancel.Token).ConfigureAwait(false);
        var plan = new TrimPlan(true, lead, 0, total, total - lead, 0, $"program start {how}");
        var outcome = await _trimmer.ApplyAsync(encoder.EncoderPath, cfg.WorkDirectory, item.Path!, plan, cfg.AudioTrimKeepOriginal, job.Cancel.Token, job).ConfigureAwait(false);
        if (!outcome.Success)
        {
            _logger.LogInformation("Program start: trim of {Path} not done ({Message})", item.Path, outcome.Message);
            return new ProgramStartResult(false, false, start, outcome.Message == "stopped" ? "Cutting stopped; the recording is untouched." : $"Couldn't trim it: {outcome.Message}");
        }

        // The file now starts (almost) at the program: nothing left to mark.
        _store.Save(item.Id, new ProgramStartRecord(0, source, cardId, PluginConfiguration.ProgramStartTrim, $"trimmed {lead:F1}s, {how}", DateTime.UtcNow, outcome.BackupPath));
        await RefreshAsync(item).ConfigureAwait(false);
        _logger.LogInformation("Program start: trimmed {Lead:F1}s from {Path} ({How})", lead, item.Path, how);
        return new ProgramStartResult(true, true, start, $"Trimmed the first {Clock(lead)} ({how}).");
    }

    /// <summary>The untouched original a trim kept beside the recording, if there is one.</summary>
    public static string? BackupOf(BaseItem item) => Editing.TrimBackups.Find(item.Path);

    /// <summary>
    /// "Undo trim": puts the kept original back in place of the trimmed recording (a program-start
    /// or audio-format trim; both keep it the same way, see <see cref="Editing.TrimBackups"/>), and leaves the recording alone
    /// afterwards — no automatic program-start or audio trim cuts it again.
    /// </summary>
    public async Task<ProgramStartResult> UndoTrimAsync(BaseItem item)
    {
        if (BackupOf(item) is not { } backup)
        {
            return new ProgramStartResult(false, false, null, "There's no kept original to put back: it was cut without keeping one, or its undo time is up.");
        }

        if (!_cuts.TryStart(item, "program-start", out var job))
        {
            return Busy();
        }

        try
        {
            job.Phase = CutJob.PhaseFinishing;
            // Same folder, so a rename: the original replaces the trimmed copy in one step.
            File.Move(backup, item.Path!, overwrite: true);
            _serviceProvider.GetService<Editing.TrimBackups>()?.Forget(backup);
            _store.Save(item.Id, new ProgramStartRecord(0, ProgramStartRecord.SourceUndone, null, string.Empty, "trim undone", DateTime.UtcNow));
            _serviceProvider.GetService<AudioTrimStore>()?.Save(
                item.Id, new AudioTrimRecord(AudioTrimStatus.NotNeeded, 0, 0, null, "trim undone; left as recorded", DateTime.UtcNow));
            _logger.LogInformation("Trim undone: {Path} is back to the original recording", item.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not undo the trim of {Path}", item.Path);
            return new ProgramStartResult(false, false, null, $"Couldn't put the original back: {ex.Message}");
        }
        finally
        {
            _cuts.Finish(job);
        }

        await RefreshAsync(item).ConfigureAwait(false);
        await RedetectAfterTrimAsync(item).ConfigureAwait(false);
        return new ProgramStartResult(true, false, null, "The original recording is back, uncut. Commercials are looked for again.");
    }

    // The file changed under the library item (new length): have Jellyfin read it again now,
    // as the audio-format trim does.
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

    // A trim moved every timestamp: old commercial breaks no longer line up. Same handling as a
    // manual audio-format trim (not forced, so the channel lists still apply). Resolved here,
    // not injected: the detection manager depends on this service.
    private async Task RedetectAfterTrimAsync(BaseItem item)
    {
        try
        {
            await _serviceProvider.GetRequiredService<IMediaSegmentManager>().DeleteSegmentsAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear stale segments after a program-start trim for {Path}", item.Path);
        }

        _serviceProvider.GetService<Detection.DetectionStore>()?.Remove(item.Id);
        _serviceProvider.GetService<Detection.DetectionManager>()?.Enqueue(item.Id, force: false, reason: Detection.ScanReason.AfterTrim);
    }

    // Re-asks the segment providers (ours serves the marker) so players see it now.
    private async Task PublishAsync(BaseItem item, CancellationToken ct)
    {
        try
        {
            var segments = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
            await segments.RunSegmentPluginProviders(item, _libraryManager.GetLibraryOptions(item), true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Program start saved but segments could not be refreshed for {Path}", item.Path);
        }
    }

    /// <summary>
    /// The last run of frames that match one of <paramref name="cards"/> — the program's own
    /// card comes after any earlier ones (a previous film's tail, a promo). With
    /// <paramref name="around"/>, only the run that contains (or is nearest) that frame.
    /// </summary>
    private static (int Start, int End, double Best, int Card)? LastRun(IReadOnlyList<byte[]> frames, IReadOnlyList<byte[]> cards, double threshold, int? around)
    {
        var runs = new List<(int Start, int End, double Best, int Card)>();
        (int Start, int End, double Best, int Card)? current = null;
        for (var i = 0; i < frames.Count; i++)
        {
            var best = 0.0;
            var which = 0;
            for (var c = 0; c < cards.Count; c++)
            {
                var s = CardFrames.Similarity(frames[i], cards[c]);
                if (s > best)
                {
                    best = s;
                    which = c;
                }
            }

            if (best >= threshold)
            {
                current = current is { } r
                    ? (r.Start, i, Math.Max(r.Best, best), best > r.Best ? which : r.Card)
                    : (i, i, best, which);
            }
            else if (current is { } done)
            {
                runs.Add(done);
                current = null;
            }
        }

        if (current is { } last)
        {
            runs.Add(last);
        }

        // A single frame is a fluke; a card stays up for seconds.
        runs.RemoveAll(r => r.End - r.Start + 1 < CardFrames.FramesPerSecond);
        if (runs.Count == 0)
        {
            return null;
        }

        return around is { } at
            ? runs.OrderBy(r => at < r.Start ? r.Start - at : at > r.End ? at - r.End : 0).First()
            : runs[^1];
    }

    private string Ffmpeg() =>
        _serviceProvider.GetService<IMediaEncoder>()?.EncoderPath is { Length: > 0 } p ? p : throw new InvalidOperationException("Jellyfin's ffmpeg path is unknown");

    private static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Round(seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
