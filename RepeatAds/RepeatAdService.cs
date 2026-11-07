using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Plugin.ComskipSegments.Editing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.RepeatAds;

/// <summary>
/// The repeat-ad check (a setting, off by default): after Comskip, a recording's sound is
/// compared with the other recent recordings' (see <see cref="FingerprintStore"/>). Stretches also
/// heard in a recording of another show are ads, and are added to its breaks (see
/// <see cref="BreakLearning"/>). An older recording the new one shares an ad with gets that ad too,
/// when its breaks are still Comskip's (never ones edited by hand, cut, or another program's).
/// With <see cref="PluginConfiguration.RepeatAdLearnFromEdits"/>, a show's hand-edited episodes
/// also teach its new recordings (their fingerprints are kept for as long as the edits).
/// </summary>
public sealed class RepeatAdService
{
    private readonly ILogger<RepeatAdService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly DetectionStore _detections;
    private readonly FingerprintStore _store;
    private readonly CommercialCutStore _cuts;
    private readonly SemaphoreSlim _matching = new(1, 1);

    public RepeatAdService(
        ILogger<RepeatAdService> logger,
        ILibraryManager libraryManager,
        IServiceProvider serviceProvider,
        DetectionStore detections,
        FingerprintStore store,
        CommercialCutStore cuts)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _serviceProvider = serviceProvider;
        _detections = detections;
        _store = store;
        _cuts = cuts;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public FingerprintStore Store => _store;

    /// <summary>
    /// Which show a recording belongs to, by name (the same show can be in two libraries with
    /// different ids): episodes by series name, anything else on its own.
    /// </summary>
    public static string SeriesKey(BaseItem item)
    {
        static string Norm(string? s) => new string((s ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return item switch
        {
            Episode e when !string.IsNullOrWhiteSpace(e.SeriesName) => "series:" + Norm(e.SeriesName),
            Movie m => "movie:" + Norm(m.Name) + m.ProductionYear,
            _ => "item:" + item.Id.ToString("N")
        };
    }

    /// <summary>
    /// The audio fingerprint of a file, made with Jellyfin's ffmpeg (Chromaprint, the same as
    /// fpcalc); null if it can't be made. Runs at low priority, alongside Comskip, which is reading
    /// the same file (so it's read from disk once).
    /// </summary>
    public async Task<uint[]?> FingerprintAsync(string path, CancellationToken ct)
    {
        // Scans resumed at startup can get here before Jellyfin has set up its ffmpeg (a few seconds).
        var encoder = _serviceProvider.GetService<IMediaEncoder>()?.EncoderPath;
        for (var waited = 0; string.IsNullOrEmpty(encoder) && waited < 120; waited += 2)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            encoder = _serviceProvider.GetService<IMediaEncoder>()?.EncoderPath;
        }

        if (string.IsNullOrEmpty(encoder))
        {
            _logger.LogWarning("Repeat-ad check: Jellyfin's ffmpeg wasn't found");
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = encoder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-nostdin", "-v", "error", "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-ac", "1", "-ar", "11025", "-f", "chromaprint", "-fp_format", "raw", "-algorithm", "1", "pipe:1"
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
                // Only a courtesy.
            }

            var errors = new StringBuilder();
            var errTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    if (errors.Length < 2000)
                    {
                        errors.AppendLine(line);
                    }
                }
            }, ct);
            using var ms = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await errTask.ConfigureAwait(false);

            var bytes = ms.ToArray();
            if (process.ExitCode != 0 || bytes.Length < 4 * 100)
            {
                _logger.LogWarning("Repeat-ad check: no fingerprint for {Path} (exit code {Code}): {Errors}", path, process.ExitCode, errors.ToString().Trim());
                return null;
            }

            var fp = new uint[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, fp, 0, fp.Length * 4);
            return fp;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            _logger.LogWarning(ex, "Repeat-ad check: could not fingerprint {Path}", path);
            return null;
        }
    }

    /// <summary>The breaks after the check, and what changed.</summary>
    /// <param name="AddedSeconds">More ad than Comskip found.</param>
    /// <param name="RemovedSeconds">Comskip's ad given back to the show (learned from hand-edited episodes).</param>
    /// <param name="EditedEpisodes">How many of the show's hand-edited episodes were learned from (0: none).</param>
    public sealed record CheckResult(IReadOnlyList<CommercialBreak> Breaks, double AddedSeconds, double RemovedSeconds, int EditedEpisodes);

    /// <summary>A same-show pair sharing one stretch this long is the same episode aired twice: not learned from.</summary>
    private const double SameEpisodeSeconds = 300;

    /// <summary>Hand-edited episodes only teach recordings within this many seconds of their length (padding, show time).</summary>
    private const double SameLengthSeconds = 60;

    /// <summary>
    /// Keeps the recording's fingerprint, finds the ads it shares with recordings of other shows,
    /// and (with <see cref="PluginConfiguration.RepeatAdLearnFromEdits"/>, once the show has two
    /// hand-edited episodes) what those episodes say about it; returns its breaks put together
    /// by <see cref="BreakLearning"/>. Older recordings that share ads with it are updated in the
    /// background (<paramref name="isBusy"/>: being scanned or cut now, left alone).
    /// </summary>
    public async Task<CheckResult> CheckAsync(
        BaseItem item, uint[] fp, IReadOnlyList<CommercialBreak> breaks, IReadOnlyList<ComskipPiece>? pieces,
        Func<Guid, bool> isBusy, CancellationToken ct)
    {
        var cfg = Config;
        var key = SeriesKey(item);
        var mine = new List<(double, double)>();
        var theirs = new Dictionary<Guid, List<(double, double)>>();
        var edited = cfg.RepeatAdLearnFromEdits ? await EditedEpisodesAsync(item, key, ct).ConfigureAwait(false) : new();
        var learnedAds = new List<Span>();
        var learnedShow = new List<Span>();
        var length = fp.Length * RepeatMatcher.Step;

        await _matching.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pruned = _store.Prune(cfg.RepeatAdKeepDays, IsEdited);
            if (pruned > 0)
            {
                _logger.LogInformation("Repeat-ad check: forgot {Count} fingerprint(s) older than {Days} days", pruned, cfg.RepeatAdKeepDays);
            }

            var index = RepeatMatcher.Index(fp);
            foreach (var (id, entry) in _store.Entries())
            {
                ct.ThrowIfCancellationRequested();
                var sameShow = entry.SeriesKey == key;
                if (id == item.Id || (sameShow && !edited.ContainsKey(id)))
                {
                    continue;
                }

                if (_store.Load(id) is not { } other)
                {
                    _store.Remove(id);
                    continue;
                }

                var matches = RepeatMatcher.Find(fp, index, other);
                if (sameShow)
                {
                    if (matches.Any(m => m.End - m.Start >= SameEpisodeSeconds))
                    {
                        continue; // the same episode recorded twice
                    }

                    var theirBreaks = edited[id].Breaks;
                    foreach (var m in matches)
                    {
                        var inBreaks = BreakLearning.Cover(new Span(m.OtherStart, m.OtherEnd), theirBreaks);
                        if (inBreaks >= 0.8)
                        {
                            learnedAds.Add(new Span(m.Start, m.End));
                        }
                        else if (inBreaks <= 0.2)
                        {
                            learnedShow.Add(new Span(m.Start, m.End));
                        }
                    }

                    continue;
                }

                foreach (var m in matches)
                {
                    mine.Add((m.Start, m.End));
                    if (!theirs.TryGetValue(id, out var list))
                    {
                        theirs[id] = list = new List<(double, double)>();
                    }

                    list.Add((m.OtherStart, m.OtherEnd));
                }
            }

            if (item.Path is not null && FileStamp(item.Path) is { } stamp)
            {
                _store.Save(item.Id, fp, new FingerprintEntry(key, item.Name, stamp.Length, stamp.TimeUtc, DateTime.UtcNow));
            }
        }
        finally
        {
            _matching.Release();
        }

        EditedEvidence? evidence = null;
        if (edited.Count >= 2)
        {
            var alike = edited.Values.Where(e => Math.Abs(e.Seconds - length) <= SameLengthSeconds).ToList();
            var pattern = cfg.RepeatAdUseBreakPattern ? Pattern(alike) : null;
            evidence = new EditedEvidence(
                learnedAds,
                learnedShow,
                alike.Count >= 2 ? Median(alike.Select(e => e.Seconds - BreakLearning.Covered(e.Breaks))) : null,
                alike.Count >= 2 && alike.All(e => e.Breaks.Count > 0 && e.Breaks[0].Start < 1) ? alike.Min(e => e.Breaks[0].End) : null,
                alike.Count >= 2 && alike.All(e => e.Breaks.Count > 0 && e.Breaks[^1].End > e.Seconds - 5) ? alike.Min(e => e.Seconds - e.Breaks[^1].Start) : null,
                edited.Count,
                BreakLearning.Normalize(alike.SelectMany(e => e.Breaks)),
                pattern?.Junctions,
                pattern is null ? null : Anchors(fp, pattern.Value));
        }

        var combined = BreakLearning.Combine(breaks, pieces, mine.Select(m => new Span(m.Item1, m.Item2)).ToList(), evidence, length, out var added, out var removed);
        if (added < 1 && removed < 1)
        {
            combined = breaks.ToList();
            added = removed = 0;
        }
        else
        {
            _logger.LogInformation(
                "Repeat-ad check on {Path}: {Added:0} s of ads added, {Removed:0} s given back to the show ({Before} → {After} break(s); {Edited} hand-edited episode(s) of the show learned from)",
                item.Path, added, removed, breaks.Count, combined.Count, evidence?.Episodes ?? 0);
        }

        if (cfg.RepeatAdUpdateOlder && theirs.Count > 0)
        {
            _ = Task.Run(() => UpdateOlderAsync(theirs, isBusy));
        }

        return new CheckResult(combined, added, removed, evidence?.Episodes ?? 0);
    }

    /// <summary>A hand-edited episode: its breaks (in seconds) and its length.</summary>
    private sealed record EditedEpisode(Guid Id, List<Span> Breaks, double Seconds);

    private bool IsEdited(Guid id) =>
        _detections.TryGet(id, out var r)
        && (r.Source == DetectionRecord.SourceEdited || (r.Source == DetectionRecord.SourceCut && EditedBeforeCut(id) is not null));

    /// <summary>The breaks and length a recording had before its commercials were cut, when those were edited by hand.</summary>
    private CommercialCutRecord? EditedBeforeCut(Guid id) =>
        _cuts.TryGet(id, out var cut) && cut.SourceBefore == DetectionRecord.SourceEdited && cut.BreaksBefore.Count > 0 ? cut : null;

    /// <summary>
    /// The show's hand-edited episodes (other than this one), each with a fingerprint: made now
    /// for any that has none or whose file changed since (about 15 s each, once). An episode
    /// whose commercials you cut out after editing them still teaches: with the breaks from
    /// before the cut and a fingerprint of the kept original (made while it's kept, then kept).
    /// </summary>
    private async Task<Dictionary<Guid, EditedEpisode>> EditedEpisodesAsync(BaseItem item, string key, CancellationToken ct)
    {
        var result = new Dictionary<Guid, EditedEpisode>();
        foreach (var (id, record) in _detections.All())
        {
            if (id == item.Id || _libraryManager.GetItemById(id) is not BaseItem other || other.Path is null || SeriesKey(other) != key)
            {
                continue;
            }

            IEnumerable<CommercialBreak> breaks;
            var hasEntry = _store.TryGetEntry(id, out var entry);
            string? source;
            (long Length, DateTime TimeUtc)? stamp;
            var preCut = false;
            if (record.Source == DetectionRecord.SourceEdited)
            {
                breaks = record.Breaks;
                source = other.Path;
                stamp = FileStamp(source);
                if (stamp is null)
                {
                    continue;
                }

                if (hasEntry && (entry.PreCut || entry.Length != stamp.Value.Length || entry.FileTimeUtc != stamp.Value.TimeUtc))
                {
                    hasEntry = false; // made of something else (before an Undo of a cut, or the file changed since)
                }
            }
            else if (record.Source == DetectionRecord.SourceCut && EditedBeforeCut(id) is { } cut)
            {
                breaks = cut.BreaksBefore;
                preCut = true;
                source = TrimBackups.Find(other.Path);
                stamp = source is null ? null : FileStamp(source);
                if (hasEntry && !entry.PreCut
                    && (stamp is null || entry.Length != stamp.Value.Length || entry.FileTimeUtc != stamp.Value.TimeUtc))
                {
                    hasEntry = false; // made of the cut file, or of a different original
                }

                if (!hasEntry && stamp is null)
                {
                    continue; // the original is gone, and wasn't fingerprinted while it was kept
                }
            }
            else
            {
                continue;
            }

            if (!hasEntry)
            {
                _logger.LogInformation(
                    preCut ? "Repeat-ad check: fingerprinting the kept original of cut, hand-edited {Path} to learn from it"
                        : "Repeat-ad check: fingerprinting hand-edited {Path} to learn from it",
                    other.Path);
                if (await FingerprintAsync(source!, ct).ConfigureAwait(false) is not { } made)
                {
                    continue;
                }

                await _matching.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    _store.Save(id, made, new FingerprintEntry(key, other.Name, stamp!.Value.Length, stamp.Value.TimeUtc, DateTime.UtcNow, preCut));
                }
                finally
                {
                    _matching.Release();
                }
            }
            else if (preCut && !entry.PreCut)
            {
                // Made before the cut, of what is now the kept original: keep it when that goes.
                _store.MarkPreCut(id);
            }

            var seconds = _store.Load(id) is { } f ? f.Length * RepeatMatcher.Step : 0;
            if (seconds > 0)
            {
                result[id] = new EditedEpisode(
                    id,
                    BreakLearning.Normalize(breaks.Select(b => new Span(b.StartTicks / 1e7, b.EndTicks / 1e7))),
                    seconds);
            }
        }

        return result;
    }

    /// <summary>Least edited episodes of about this length, with the same number of breaks, for a pattern.</summary>
    private const int PatternEpisodes = 3;

    /// <summary>
    /// The show's break pattern from its edited episodes of about this length: when at least
    /// <see cref="PatternEpisodes"/> of them, and two in three, have the same number of breaks,
    /// each of those breaks' earliest and latest start and shortest, longest and median length.
    /// </summary>
    private (List<Junction> Junctions, List<EditedEpisode> Episodes)? Pattern(List<EditedEpisode> alike)
    {
        var group = alike.GroupBy(e => e.Breaks.Count).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (group is null || group.Key == 0 || group.Count() < PatternEpisodes || group.Count() * 3 < alike.Count * 2)
        {
            return null;
        }

        var eps = group.ToList();
        var atStart = eps.All(e => e.Breaks[0].Start < 1);
        var atEnd = eps.All(e => e.Breaks[^1].End > e.Seconds - 5);
        var result = new List<Junction>();
        for (var k = 0; k < group.Key; k++)
        {
            var starts = eps.Select(e => e.Breaks[k].Start).ToList();
            var lengths = eps.Select(e => e.Breaks[k].Length).ToList();
            result.Add(new Junction(starts.Min(), starts.Max(), lengths.Min(), lengths.Max(), Median(lengths),
                k == 0 && atStart, k == group.Key - 1 && atEnd));
        }

        return (result, eps);
    }

    /// <summary>The pattern's break edges pinned by the sound at them in its episodes (see <see cref="EdgeAnchors"/>).</summary>
    private List<EdgeAnchor>? Anchors(uint[] fp, (List<Junction> Junctions, List<EditedEpisode> Episodes) pattern)
    {
        var clips = pattern.Episodes
            .Select(e => _store.Load(e.Id) is { } theirs ? EdgeAnchors.Clips(theirs, e.Breaks) : null)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
        return clips.Count >= 2 ? EdgeAnchors.Find(fp, pattern.Junctions, clips) : null;
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private async Task UpdateOlderAsync(Dictionary<Guid, List<(double, double)>> theirs, Func<Guid, bool> isBusy)
    {
        foreach (var (id, repeats) in theirs)
        {
            try
            {
                if (isBusy(id) || _libraryManager.GetItemById(id) is not BaseItem item || item.Path is null
                    || !_detections.TryGet(id, out var record)
                    || (record.Source ?? DetectionRecord.SourceComskip) != DetectionRecord.SourceComskip
                    || record.Status == DetectionStatus.Failed)
                {
                    continue; // edited by hand, cut, another program's .edl, cleared, or gone
                }

                if (!_store.TryGetEntry(id, out var entry) || FileStamp(item.Path) is not { } stamp
                    || stamp.Length != entry.Length || stamp.TimeUtc != entry.FileTimeUtc)
                {
                    _store.Remove(id); // the file changed since: its fingerprint no longer lines up
                    continue;
                }

                var merged = BreakMerge.Merge(record.Breaks, repeats, out var added);
                if (added < 1)
                {
                    continue;
                }

                _detections.Save(id, record with
                {
                    Status = DetectionStatus.Detected, Breaks = merged, UpdatedUtc = DateTime.UtcNow,
                    Source = DetectionRecord.SourceComskip, RepeatSeconds = record.RepeatSeconds + added
                });
                WriteEdlBeside(item.Path, merged);
                var segments = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
                await segments.RunSegmentPluginProviders(item, _libraryManager.GetLibraryOptions(item), true, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation(
                    "Repeat-ad check: {Seconds:0} s of ads heard in a newer recording added to {Path} ({Before} → {After} break(s))",
                    added, item.Path, record.Breaks.Count, merged.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Repeat-ad check: could not update the breaks of {ItemId}", id);
            }
        }
    }

    /// <summary>Rewrites this plugin's .edl beside the recording (never another program's), when that setting is on.</summary>
    public void WriteEdlBeside(string path, IReadOnlyList<CommercialBreak> breaks)
    {
        if (!Config.PlaceEdlBesideRecording || DetectionManager.SiblingEdlPath(path) is not { } edl)
        {
            return;
        }

        try
        {
            if (File.Exists(edl) && !_detections.IsOwnedEdl(edl))
            {
                return;
            }

            EdlWriter.Write(edl, breaks);
            _detections.MarkOwnedEdl(edl);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write {Edl}", edl);
        }
    }

    private static (long Length, DateTime TimeUtc)? FileStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stop ffmpeg");
        }
    }
}
