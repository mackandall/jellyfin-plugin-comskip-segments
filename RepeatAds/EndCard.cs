using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Plugin.ComskipSegments.Editing;
using Jellyfin.Plugin.ComskipSegments.ProgramStart;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.RepeatAds;

/// <summary>A frame seen just before a hand-edited final break, and how long before the cut it was.</summary>
public sealed record CardFrame(byte[] Frame, double BeforeCut);

/// <summary>One edited episode's frames either side of its final break's start.</summary>
public sealed record EditedEnding(IReadOnlyList<CardFrame> Before, IReadOnlyList<byte[]> After);

/// <summary>
/// The end-of-show card, learned from your edits. Some channels run filler, promos or the next
/// episode after a show ends, and you end every edited episode's last break at the same card
/// (Into the Badlands on Pluto: "Millar Gough Ink", then "AMC Studios", then ten minutes of
/// Pluto's logo screen and the next episode's opening). The frames in the seconds before your
/// final break that turn up in at least one other edited episode of the show are the card; a
/// new recording where they're found gets a break from there to the end of the file. Where
/// they aren't found (another channel, another ending) nothing changes.
/// </summary>
public static class EndCard
{
    /// <summary>How many seconds before your final break are looked at.</summary>
    public const double LearnSeconds = 12;

    /// <summary>"The same picture" (CardFrames: the same card on different recordings scores 0.95+).</summary>
    internal static double MinSimilarity = 0.9;

    /// <summary>A frame this flat (black, a fade) matches too easily: not learned.</summary>
    private const double MinContrast = 12;

    /// <summary>How many matching frames (at CardFrames.FramesPerSecond) must agree on the cut.</summary>
    private const int MinHits = 3;

    /// <summary>How closely matching frames must agree on where the cut is.</summary>
    private const double AgreeSeconds = 2;

    /// <summary>Your final break must reach to within this of the end of the file to teach.</summary>
    public const double ToEndSeconds = 10;

    /// <summary>
    /// The card frames: from each edited episode's last seconds before its final break, those
    /// with some picture in them that also appear in another edited episode, and in no
    /// episode's first seconds of that break (the channel's filler screen can come just
    /// before the card in one episode and just after the cut in the next).
    /// </summary>
    public static List<CardFrame> Learn(IReadOnlyList<EditedEnding> episodes)
    {
        var inBreak = episodes.SelectMany(e => e.After).Where(f => Contrast(f) >= MinContrast).ToList();
        var result = new List<CardFrame>();
        for (var e = 0; e < episodes.Count; e++)
        {
            foreach (var f in episodes[e].Before)
            {
                if (Contrast(f.Frame) < MinContrast || inBreak.Any(g => CardFrames.Similarity(f.Frame, g) >= MinSimilarity))
                {
                    continue;
                }

                var shared = false;
                for (var o = 0; o < episodes.Count && !shared; o++)
                {
                    shared = o != e && episodes[o].Before.Any(g => CardFrames.Similarity(f.Frame, g.Frame) >= MinSimilarity);
                }

                if (shared)
                {
                    result.Add(f);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Where the show ends in <paramref name="frames"/> (sampled from <paramref name="from"/>
    /// seconds at CardFrames.FramesPerSecond): the latest spot the card frames agree on, or null.
    /// </summary>
    public static double? Find(IReadOnlyList<byte[]> frames, double from, IReadOnlyList<CardFrame> card)
    {
        if (card.Count == 0)
        {
            return null;
        }

        var hits = new List<(double Cut, double At)>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (Contrast(frames[i]) < MinContrast)
            {
                continue;
            }

            CardFrame? best = null;
            var bestScore = MinSimilarity;
            foreach (var c in card)
            {
                var s = CardFrames.Similarity(frames[i], c.Frame);
                if (s >= bestScore)
                {
                    (best, bestScore) = (c, s);
                }
            }

            if (best is not null)
            {
                var at = from + ((double)i / CardFrames.FramesPerSecond);
                hits.Add((at + best.BeforeCut, at));
            }
        }

        // The latest group of hits that agree on the cut (a card seen earlier, in a promo, is
        // passed over), and its middle: where you cut after that card, on the median. (Snapping
        // to the group's last frame instead was up to 8 s early: the card fades out unmatched.)
        hits.Sort((x, y) => x.Cut.CompareTo(y.Cut));
        for (var end = hits.Count - 1; end >= 0;)
        {
            var start = end;
            while (start > 0 && hits[end].Cut - hits[start - 1].Cut <= AgreeSeconds)
            {
                start--;
            }

            if (end - start + 1 >= MinHits)
            {
                return hits[(start + end) / 2].Cut;
            }

            end = start - 1;
        }

        return null;
    }

    /// <summary>A gap of show before the final break longer than this is the show, not a credits pocket.</summary>
    public const double MaxPocketSeconds = 120;

    /// <summary>How closely the edited episodes' pockets must agree.</summary>
    private const double PocketAgreeSeconds = 10;

    /// <summary>How far the pocket's start may move to the black frames before the credits.</summary>
    internal static double PocketSnapSeconds = 6;

    /// <summary>
    /// The credits pocket: how much show you keep between the second-to-last break and the
    /// final one (Into the Badlands: the credits, then the two logo cards, 35–40 s). Null when
    /// fewer than 2 episodes, or fewer than half of <paramref name="episodes"/>, agree on one.
    /// </summary>
    public static double? LearnPocket(IEnumerable<double?> pockets, int episodes)
    {
        var v = pockets.Where(p => p is > 0 and <= MaxPocketSeconds).Select(p => p!.Value).OrderBy(p => p).ToList();
        if (v.Count < 2)
        {
            return null;
        }

        var median = v[v.Count / 2];
        var agree = v.Where(p => Math.Abs(p - median) <= PocketAgreeSeconds).ToList();
        return agree.Count >= 2 && agree.Count * 2 >= episodes ? agree[agree.Count / 2] : null;
    }

    /// <summary>
    /// Where the pocket starts in <paramref name="frames"/> (sampled from <paramref name="from"/>):
    /// <paramref name="pocket"/> before the cut, moved to the nearest picture after black (the
    /// credits coming in after the promos) within <see cref="PocketSnapSeconds"/>.
    /// </summary>
    public static double PocketStart(IReadOnlyList<byte[]> frames, double from, double cut, double pocket)
    {
        var target = cut - pocket;
        double? best = null;
        for (var i = 1; i < frames.Count; i++)
        {
            var at = from + ((double)i / CardFrames.FramesPerSecond);
            if (Math.Abs(at - target) <= PocketSnapSeconds && CardFrames.IsBlack(frames[i - 1]) && !CardFrames.IsBlack(frames[i])
                && (best is null || Math.Abs(at - target) < Math.Abs(best.Value - target)))
            {
                best = at;
            }
        }

        return best ?? target;
    }

    /// <summary>
    /// The breaks with [<paramref name="cut"/>, <paramref name="end"/>] marked: breaks starting
    /// after the cut are joined to it, and one is added when there's none. With a pocket
    /// (<paramref name="pocketStart"/> to the cut), that stays show: breaks running into it
    /// stop where it starts, and breaks inside it go. Without one, a break reaching the cut is
    /// joined to the final break.
    /// </summary>
    public static IReadOnlyList<CommercialBreak> Apply(IReadOnlyList<CommercialBreak> breaks, double cut, double end, double? pocketStart = null)
    {
        static long T(double s) => (long)(s * TimeSpan.TicksPerSecond);
        var last = new CommercialBreak(T(cut), T(end));
        var keepFrom = pocketStart is { } ps && ps < cut ? T(ps) : (long?)null;
        var rest = new List<CommercialBreak>();
        foreach (var b in breaks.OrderBy(b => b.StartTicks))
        {
            if (b.StartTicks >= last.StartTicks || (keepFrom is null && b.EndTicks >= last.StartTicks))
            {
                last = new CommercialBreak(Math.Min(last.StartTicks, b.StartTicks), Math.Max(last.EndTicks, b.EndTicks));
            }
            else if (keepFrom is not { } k || b.EndTicks <= k)
            {
                rest.Add(b);
            }
            else if (b.StartTicks < k)
            {
                rest.Add(new CommercialBreak(b.StartTicks, k));
            }
        }

        rest.Add(last);
        return rest;
    }

    /// <summary>The frames either side of <paramref name="cut"/> in one edited episode, for <see cref="Learn"/>.</summary>
    public static async Task<EditedEnding> SampleEndingAsync(string ffmpeg, string path, double cut, CancellationToken ct)
    {
        var from = Math.Max(0, cut - LearnSeconds);
        var frames = await CardFrames.SampleAsync(ffmpeg, path, from, cut - from + LearnSeconds, ct).ConfigureAwait(false);
        var before = new List<CardFrame>();
        var after = new List<byte[]>();
        for (var i = 0; i < frames.Count; i++)
        {
            var at = from + ((double)i / CardFrames.FramesPerSecond);
            if (at < cut - 0.25)
            {
                before.Add(new CardFrame(frames[i], cut - at));
            }
            else if (at > cut + 0.75)
            {
                after.Add(frames[i]);
            }
        }

        return new EditedEnding(before, after);
    }

    /// <summary>One edited episode's pocket: the show between its last two breaks, when short enough to be one.</summary>
    public static double? Pocket(IReadOnlyList<CommercialBreak> breaks)
    {
        var sorted = breaks.OrderBy(b => b.StartTicks).ToList();
        if (sorted.Count < 2)
        {
            return null;
        }

        var gap = (sorted[^1].StartTicks - sorted[^2].EndTicks) / 1e7;
        return gap is > 0 and <= MaxPocketSeconds ? gap : null;
    }

    private static double Contrast(byte[] f)
    {
        double sum = 0, sq = 0;
        foreach (var v in f)
        {
            sum += v;
            sq += v * v;
        }

        var mean = sum / f.Length;
        return Math.Sqrt(Math.Max(0, (sq / f.Length) - (mean * mean)));
    }
}

/// <summary>Learns a show's end card from its edited episodes and looks for it in a new recording.</summary>
public sealed class EndCardService
{
    /// <summary>At most this many edited episodes are learned from (the latest edits).</summary>
    private const int MaxEpisodes = 12;

    /// <summary>How far before the earliest learned cut a new recording is searched.</summary>
    private const double SearchSlack = 300;

    private readonly ILogger<EndCardService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly DetectionStore _detections;
    private readonly CommercialCutStore _cuts;

    public EndCardService(ILogger<EndCardService> logger, ILibraryManager libraryManager, DetectionStore detections, CommercialCutStore cuts)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _detections = detections;
        _cuts = cuts;
    }

    /// <summary>The result of a check: the breaks, and where the show was found to end (null: not found).</summary>
    public sealed record CheckResult(IReadOnlyList<CommercialBreak> Breaks, double? Cut, int EditedEpisodes, double? PocketStart = null);

    /// <summary>
    /// <paramref name="breaks"/> with a break from the learned end card to the end of the file,
    /// when the show has at least 2 edited episodes ending that way and the card is found.
    /// </summary>
    public async Task<CheckResult> CheckAsync(string ffmpeg, BaseItem item, IReadOnlyList<CommercialBreak> breaks, CancellationToken ct)
    {
        var duration = item.RunTimeTicks is { } t ? t / 1e7 : 0;
        var edited = Edited(item).OrderByDescending(e => e.EditedUtc).Take(MaxEpisodes).ToList();
        if (duration <= 0 || edited.Count < 2)
        {
            return new CheckResult(breaks, null, edited.Count);
        }

        var episodes = new List<EditedEnding>();
        foreach (var e in edited)
        {
            episodes.Add(await EndCard.SampleEndingAsync(ffmpeg, e.Path, e.Cut, ct).ConfigureAwait(false));
        }

        var card = EndCard.Learn(episodes);
        if (card.Count == 0)
        {
            return new CheckResult(breaks, null, edited.Count);
        }

        var searchFrom = Math.Max(0, Math.Min(edited.Min(e => e.Cut), duration) - SearchSlack);
        var sample = await CardFrames.SampleAsync(ffmpeg, item.Path, searchFrom, duration - searchFrom, ct).ConfigureAwait(false);
        if (EndCard.Find(sample, searchFrom, card) is not { } cut || cut >= duration - 1)
        {
            return new CheckResult(breaks, null, edited.Count);
        }

        double? pocketStart = EndCard.LearnPocket(edited.Select(e => e.Pocket), edited.Count) is { } pocket
            ? EndCard.PocketStart(sample, searchFrom, cut, pocket)
            : null;
        return new CheckResult(EndCard.Apply(breaks, cut, duration, pocketStart), cut, edited.Count, pocketStart);
    }

    private sealed record EditedFile(string Path, double Cut, double? Pocket, DateTime EditedUtc);

    /// <summary>
    /// The show's edited episodes whose final break runs to the end of the file: the file to
    /// look at (the kept original for one cut after editing) and where that break starts.
    /// </summary>
    private IEnumerable<EditedFile> Edited(BaseItem item)
    {
        var key = RepeatAdService.SeriesKey(item);
        foreach (var (id, record) in _detections.All())
        {
            if (id == item.Id || _libraryManager.GetItemById(id) is not BaseItem other || other.Path is null || RepeatAdService.SeriesKey(other) != key)
            {
                continue;
            }

            IReadOnlyList<CommercialBreak> breaks;
            string? path;
            double seconds;
            if (record.Source == DetectionRecord.SourceEdited)
            {
                (breaks, path, seconds) = (record.Breaks, other.Path, other.RunTimeTicks is { } t ? t / 1e7 : 0);
            }
            else if (record.Source == DetectionRecord.SourceCut && _cuts.TryGet(id, out var c)
                && c.SourceBefore == DetectionRecord.SourceEdited && c.BreaksBefore.Count > 0)
            {
                (breaks, path, seconds) = (c.BreaksBefore, TrimBackups.Find(other.Path), c.SecondsBefore);
            }
            else
            {
                continue;
            }

            if (path is null || !File.Exists(path) || seconds <= 0 || breaks.Count == 0)
            {
                continue;
            }

            var last = breaks.MaxBy(b => b.EndTicks);
            if (last.EndTicks / 1e7 < seconds - EndCard.ToEndSeconds || last.StartTicks <= 0)
            {
                continue;
            }

            yield return new EditedFile(path, last.StartTicks / 1e7, EndCard.Pocket(breaks), record.UpdatedUtc);
        }
    }
}
