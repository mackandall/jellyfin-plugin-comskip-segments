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
    private readonly LessonStore _lessons;
    private readonly SemaphoreSlim _matching = new(1, 1);

    public RepeatAdService(
        ILogger<RepeatAdService> logger,
        ILibraryManager libraryManager,
        IServiceProvider serviceProvider,
        DetectionStore detections,
        FingerprintStore store,
        CommercialCutStore cuts,
        LessonStore lessons)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _serviceProvider = serviceProvider;
        _detections = detections;
        _store = store;
        _cuts = cuts;
        _lessons = lessons;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public FingerprintStore Store => _store;

    public LessonStore Lessons => _lessons;

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
            var pruned = _store.Prune(cfg.RepeatAdKeepDays, id => _lessons.Has(id) || IsEdited(id));
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

        var steps = new List<string>();
        var combined = BreakLearning.Combine(breaks, pieces, mine.Select(m => new Span(m.Item1, m.Item2)).ToList(), evidence, length, out var added, out var removed, steps);
        _logger.LogInformation("Repeat-ad steps for {Path}: {Steps}", item.Path, string.Join(" | ", steps));
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

        // The sound that ends your breaks, heard just after one: the break runs on to it.
        if (edited.Count >= 2 && combined.Count > 0)
        {
            var countdown = edited.Values
                .Select(e => _store.Load(e.Id) is { } theirFp
                    ? EdgeAnchors.Clips(theirFp, e.Breaks).Take(e.Breaks.Count - 1).Select(c => c.BreakLast)
                    : Enumerable.Empty<uint[]?>())
                .SelectMany(c => c)
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();
            var spans = combined.Select(b => new Span(b.StartTicks / 1e7, b.EndTicks / 1e7)).ToList();
            var longer = EdgeAnchors.ExtendToCountdown(fp, spans, countdown, out var extended);
            if (extended > 0)
            {
                var more = BreakLearning.Covered(longer) - BreakLearning.Covered(spans);
                added += more;
                _logger.LogInformation(
                    "Break-ending countdown in {Path}: {Count} break(s) run on to it ({More:0.0} s more), from {Clips} clips of your edited breaks' last seconds",
                    item.Path, extended, more, countdown.Count);
                combined = longer.Select(x => new CommercialBreak((long)Math.Round(x.Start * 1e7), (long)Math.Round(x.End * 1e7))).ToList();
            }
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
    /// The show's teaching lessons (other than this recording's own), each with the fingerprint of
    /// the file its breaks were saved on: kept since the lesson was saved, or made now from that file
    /// (or the kept original of a cut one) when it's still there. A lesson whose file is gone and was
    /// never fingerprinted can't teach.
    /// </summary>
    private async Task<Dictionary<Guid, EditedEpisode>> EditedEpisodesAsync(BaseItem item, string key, CancellationToken ct)
    {
        MigrateLessons();
        var result = new Dictionary<Guid, EditedEpisode>();
        foreach (var (id, lesson) in _lessons.OfShow(key))
        {
            if (id == item.Id || await LessonFingerprintAsync(id, lesson, ct).ConfigureAwait(false) is not { } fp)
            {
                continue;
            }

            var seconds = fp.Length * RepeatMatcher.Step;
            if (seconds > 0)
            {
                result[id] = new EditedEpisode(
                    id,
                    BreakLearning.Normalize(lesson.Breaks.Select(b => new Span(b.StartTicks / 1e7, b.EndTicks / 1e7))),
                    seconds);
            }
        }

        return result;
    }

    /// <summary>The lesson's file: the recording, or the kept original of a cut one, while it's still the file the breaks were saved on.</summary>
    public string? LessonFile(Guid id, Lesson lesson)
    {
        if (_libraryManager.GetItemById(id) is not BaseItem other || other.Path is null)
        {
            return null;
        }

        foreach (var path in new[] { other.Path, TrimBackups.Find(other.Path) })
        {
            // Without a known size and time, only the recording itself, and never once it's cut (the
            // breaks are on the original's clock).
            if (path is not null && FileStamp(path) is { } stamp
                && (lesson.FileLength is null
                    ? path == other.Path && !(_detections.TryGet(id, out var r) && r.Source == DetectionRecord.SourceCut)
                    : stamp.Length == lesson.FileLength && stamp.TimeUtc == lesson.FileTimeUtc))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>The fingerprint of the lesson's file: the kept one, or made now (and kept) while the file is there.</summary>
    private async Task<uint[]?> LessonFingerprintAsync(Guid id, Lesson lesson, CancellationToken ct)
    {
        if (_store.TryGetEntry(id, out var entry) && _store.Load(id) is { } kept)
        {
            // A lesson knows its file's size and time; one made of an older edit doesn't: its fingerprint
            // is good while it was made of the file as it is now (or of the original, kept before a cut).
            var same = lesson.FileLength is not null
                ? entry.Length == lesson.FileLength && entry.FileTimeUtc == lesson.FileTimeUtc
                : entry.PreCut || FileStamp(_libraryManager.GetItemById(id)?.Path ?? string.Empty) is not { } now
                    || (now.Length == entry.Length && now.TimeUtc == entry.FileTimeUtc);
            if (same)
            {
                return kept;
            }
        }

        if (LessonFile(id, lesson) is not { } source || FileStamp(source) is not { } stamp)
        {
            return null;
        }

        var preCut = _libraryManager.GetItemById(id)?.Path != source;
        _logger.LogInformation("Repeat-ad check: fingerprinting {Path} to learn from its lesson", source);
        if (await FingerprintAsync(source, ct).ConfigureAwait(false) is not { } made)
        {
            return null;
        }

        await _matching.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _store.Save(id, made, new FingerprintEntry(lesson.SeriesKey, lesson.Name, stamp.Length, stamp.TimeUtc, DateTime.UtcNow, preCut));
        }
        finally
        {
            _matching.Release();
        }

        return made;
    }

    /// <summary>
    /// Keeps a hand edit just saved as its show's lesson (replacing an earlier lesson of the same
    /// recording, keeping whether it teaches and what the scan had), and its sound in the background
    /// so cutting or deleting the recording doesn't take it away.
    /// </summary>
    /// <param name="before">The recording's breaks before this save.</param>
    public Lesson RememberEdit(BaseItem item, IReadOnlyList<CommercialBreak> breaks, DetectionRecord? before)
    {
        _lessons.TryGet(item.Id, out var earlier);
        var stamp = item.Path is null ? null : FileStamp(item.Path);
        var scanBefore = earlier?.ScanBefore
            ?? (before is { Source: not DetectionRecord.SourceEdited and not DetectionRecord.SourceCut and not DetectionRecord.SourceCleared } b
                && b.Status == DetectionStatus.Detected ? b.Breaks : null);
        var lesson = new Lesson(
            SeriesKey(item),
            ShowName(item),
            LessonName(item),
            breaks.OrderBy(b => b.StartTicks).ToList(),
            (item.RunTimeTicks ?? 0) / 1e7,
            DateTime.UtcNow,
            earlier?.Teaching ?? true,
            stamp?.Length,
            stamp?.TimeUtc,
            scanBefore);
        _lessons.Save(item.Id, lesson);

        if (Config.RepeatAdLearnFromEdits)
        {
            var id = item.Id;
            _ = Task.Run(async () =>
            {
                try
                {
                    await LessonFingerprintAsync(id, lesson, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not keep the sound of {Name}'s lesson", lesson.Name);
                }
            });
        }

        return lesson;
    }

    public static string ShowName(BaseItem item) => item is Episode e && !string.IsNullOrWhiteSpace(e.SeriesName) ? e.SeriesName : item.Name;

    public static string LessonName(BaseItem item) =>
        item is Episode e && e.ParentIndexNumber is { } s && e.IndexNumber is { } n ? $"S{s:00}E{n:00} · {item.Name}" : item.Name;

    /// <summary>
    /// Once: the hand edits from before lessons existed become lessons (edited recordings, and
    /// cut ones edited before the cut, with the breaks they had then).
    /// </summary>
    public void MigrateLessons()
    {
        if (_lessons.Migrated)
        {
            return;
        }

        var found = new List<(Guid, Lesson)>();
        foreach (var (id, record) in _detections.All())
        {
            if (_libraryManager.GetItemById(id) is not BaseItem other || other.Path is null)
            {
                continue;
            }

            IReadOnlyList<CommercialBreak> breaks;
            (long Length, DateTime TimeUtc)? stamp;
            if (record.Source == DetectionRecord.SourceEdited)
            {
                breaks = record.Breaks;
                stamp = FileStamp(other.Path);
            }
            else if (record.Source == DetectionRecord.SourceCut && EditedBeforeCut(id) is { } cut)
            {
                breaks = cut.BreaksBefore;
                stamp = TrimBackups.Find(other.Path) is { } original ? FileStamp(original)
                    : _store.TryGetEntry(id, out var e) && e.PreCut ? (e.Length, e.FileTimeUtc) : null;
            }
            else
            {
                continue;
            }

            found.Add((id, new Lesson(
                SeriesKey(other), ShowName(other), LessonName(other), breaks.OrderBy(b => b.StartTicks).ToList(),
                (other.RunTimeTicks ?? 0) / 1e7, record.UpdatedUtc, true, stamp?.Length, stamp?.TimeUtc, null, "migrated")));
        }

        var added = _lessons.AddMigrated(found);
        _logger.LogInformation("Lessons: made {Count} lesson(s) of the hand edits from before lessons existed", added);
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

    public static (long Length, DateTime TimeUtc)? FileStamp(string path)
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
