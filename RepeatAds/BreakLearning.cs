using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ComskipSegments.Comskip;

namespace Jellyfin.Plugin.ComskipSegments.RepeatAds;

/// <summary>A stretch of a recording, in seconds.</summary>
public readonly record struct Span(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// What a show's hand-edited episodes say about a new recording of it (all optional):
/// <paramref name="LearnedAds"/>, sound heard inside a break you marked in another episode;
/// <paramref name="LearnedShow"/>, sound heard in what you kept as show; <paramref name="ShowSeconds"/>,
/// how long the show part is (median, for recordings of about this length);
/// <paramref name="StartPadding"/>/<paramref name="EndPadding"/>, the shortest padding you marked
/// at each end (of recordings of about this length); <paramref name="BreakSlots"/>, where your
/// breaks are in those episodes (all of them together).
/// </summary>
public sealed record EditedEvidence(
    IReadOnlyList<Span> LearnedAds,
    IReadOnlyList<Span> LearnedShow,
    double? ShowSeconds,
    double? StartPadding,
    double? EndPadding,
    int Episodes,
    IReadOnlyList<Span>? BreakSlots = null,
    IReadOnlyList<Junction>? Pattern = null,
    IReadOnlyList<EdgeAnchor>? Anchors = null);

/// <summary>
/// One break of a show's pattern, from its hand-edited episodes of about the same length: where it
/// starts (earliest, latest) and how long it is (shortest, longest, median). <paramref name="AtStart"/>:
/// it's the padding at the start (from 0); <paramref name="AtEnd"/>: the padding at the end (to the end).
/// </summary>
public sealed record Junction(double StartMin, double StartMax, double LengthMin, double LengthMax, double LengthMedian, bool AtStart, bool AtEnd);

/// <summary>
/// Puts a recording's breaks together from Comskip's and what the repeat-ad check found
/// (tested on 44 Crunchyroll-channel recordings, 2026-11-01):
/// 1. snapping: a Comskip piece (the stretch between two of its cut points) that's mostly
///    repeated ads becomes ad from edge to edge, and so does a short piece between two such;
/// 2. the repeats themselves are merged in (<see cref="BreakMerge"/>);
/// with a show's hand-edited episodes (<see cref="EditedEvidence"/>):
/// 3. sound you kept as show elsewhere is taken back out of the breaks (openings, endings);
/// 4. show-length check: while the show part is well short of the edited episodes', Comskip's
///    own ad pieces that nothing backs up are given back to the show;
/// 5. the padding at both ends is a break (you always marked it).
/// </summary>
public static class BreakLearning
{
    /// <summary>A piece counts as ad when this much of it was heard as an ad elsewhere.</summary>
    public const double SnapCover = 0.4;

    /// <summary>A show piece this short between two ad pieces is ad too (station cards, bumpers).</summary>
    public const double SandwichSeconds = 90;

    /// <summary>A Comskip-only ad piece is given back only when less than this much of it was heard as an ad.</summary>
    public const double UnsupportedCover = 0.2;

    /// <summary>The show-length check acts only when the show part is this much short.</summary>
    public const double LengthSlackSeconds = 60;

    private const double MinSpanSeconds = 10;
    private const long TicksPerSecond = 10_000_000L;

    public static List<CommercialBreak> Combine(
        IReadOnlyList<CommercialBreak> breaks,
        IReadOnlyList<ComskipPiece>? pieces,
        IReadOnlyList<Span> crossRepeats,
        EditedEvidence? edited,
        double recordingSeconds,
        out double addedSeconds,
        out double removedSeconds)
    {
        var original = Normalize(breaks.Select(b => new Span(b.StartTicks / (double)TicksPerSecond, b.EndTicks / (double)TicksPerSecond)));
        var evidence = Normalize(crossRepeats.Concat(edited?.LearnedAds ?? Array.Empty<Span>()));

        // 1-2. Snap, then merge the repeats in.
        var spans = Normalize(original.Concat(Snap(pieces, evidence)));
        var merged = BreakMerge.Merge(ToBreaks(spans), evidence.Select(s => (s.Start, s.End)), out _);
        spans = Normalize(merged.Select(b => new Span(b.StartTicks / (double)TicksPerSecond, b.EndTicks / (double)TicksPerSecond)));

        if (edited is not null)
        {
            var pre = edited.StartPadding ?? 0;
            var post = edited.EndPadding ?? 0;
            var padding = new List<Span>();
            if (pre > 0)
            {
                padding.Add(new Span(0, Math.Min(pre, recordingSeconds)));
            }

            if (post > 0)
            {
                padding.Add(new Span(Math.Max(0, recordingSeconds - post), recordingSeconds));
            }

            // 3. Show you kept elsewhere, except in the padding (the next episode's opening plays there)
            //    and where it was also heard as an ad (a match running from an ad's end into the show).
            var show = Subtract(Subtract(Normalize(edited.LearnedShow), padding), evidence);
            spans = DropShort(Subtract(spans, show));

            // 4. Show-length check.
            if (edited.ShowSeconds is { } expected)
            {
                var candidates = (pieces is { Count: > 0 }
                        ? pieces.Where(p => p.IsCommercial).Select(p => new Span(p.Start, p.End))
                        : original)
                    .Where(c => c.Length >= MinSpanSeconds && Cover(c, evidence) < UnsupportedCover)
                    .ToList();
                while (true)
                {
                    var deficit = expected - (recordingSeconds - Covered(spans));
                    if (deficit <= LengthSlackSeconds)
                    {
                        break;
                    }

                    Span? best = null;
                    var bestAfter = double.MaxValue;
                    foreach (var c in candidates)
                    {
                        var inside = Covered(Intersect(spans, c));
                        if (inside < MinSpanSeconds || !MayGiveBack(spans, c, edited.BreakSlots))
                        {
                            continue;
                        }

                        var after = Math.Abs(deficit - inside);
                        if (after < Math.Abs(deficit) && after < bestAfter)
                        {
                            best = c;
                            bestAfter = after;
                        }
                    }

                    if (best is null)
                    {
                        break;
                    }

                    spans = DropShort(Subtract(spans, new[] { best.Value }));
                    candidates.Remove(best.Value);
                }
            }

            // 5. Padding.
            spans = Normalize(spans.Concat(padding));

            // 6. The show's break pattern.
            if (edited.Pattern is { Count: > 0 } pattern && pieces is { Count: > 0 })
            {
                spans = FitPattern(spans, pieces, evidence, pattern, recordingSeconds, edited.Anchors);
            }
        }

        addedSeconds = Covered(Subtract(spans, original));
        removedSeconds = Covered(Subtract(original, spans));
        return ToBreaks(spans);
    }

    /// <summary>How far past the edited episodes' earliest and latest start a break is looked for, widening when nothing fits.</summary>
    private static readonly double[] PatternSlack = { 30, 60, 120 };

    /// <summary>How much longer or shorter than the edited episodes' breaks a break may be.</summary>
    public const double PatternLengthSlack = 15;

    /// <summary>How much ad only Comskip saw counts, against ad heard elsewhere (1).</summary>
    internal static double PatternComskipWeight = 0.5;

    /// <summary>Score lost per second a break is longer or shorter than the edited episodes' usual.</summary>
    internal static double PatternLengthPull = 1;

    /// <summary>At the widest search, a break must be at least this much ad already (Comskip's, heard elsewhere, or found above).</summary>
    private const double PatternWideCover = 0.5;

    /// <summary>
    /// Fits the show's break pattern (see <see cref="Junction"/>): for each of its breaks, the start
    /// and end among Comskip's cut points (where it found black frames or silence) near where your
    /// edited episodes have it, as long as yours are, and most covered by ad (Comskip's, heard
    /// elsewhere, or found above). Breaks that fit replace what's there; where one doesn't fit, what
    /// was found reaching into where it was looked for stays, whole (the episode is laid out differently);
    /// when they all fit, anything else (where your edited episodes never have a break) goes.
    /// </summary>
    internal static List<Span> FitPattern(
        List<Span> spans, IReadOnlyList<ComskipPiece> pieces, IReadOnlyList<Span> evidence, IReadOnlyList<Junction> pattern, double recordingSeconds,
        IReadOnlyList<EdgeAnchor>? anchors = null)
    {
        var ads = Normalize(pieces.Where(p => p.IsCommercial).Select(p => new Span(p.Start, p.End)).Concat(evidence).Concat(spans));
        var heard = Normalize(evidence);
        // The fingerprint stops a few seconds short of the end; Comskip's last piece doesn't.
        recordingSeconds = Math.Max(recordingSeconds, pieces.Max(p => p.End));
        var cuts = pieces.SelectMany(p => new[] { p.Start, p.End }).Append(0).Append(recordingSeconds)
            .Where(t => t >= 0 && t <= recordingSeconds + 0.5).Distinct().OrderBy(t => t).ToArray();
        var result = new List<Span>();
        var unplaced = new List<Span>();
        for (var k = 0; k < pattern.Count; k++)
        {
            var j = pattern[k];
            var anchor = anchors is not null && k < anchors.Count ? anchors[k] : default;

            // An edge pinned by the sound at it (see EdgeAnchors) is the edge; the other is searched for.
            var starts = anchor.Start is { } a0 ? new[] { a0 } : cuts;
            var ends = anchor.End is { } a1 ? new[] { a1 } : cuts;
            Span? best = null;
            var bestScore = double.MinValue;
            foreach (var slack in PatternSlack)
            {
                foreach (var start in starts)
                {
                    if (j.AtStart ? start > 0.5 : start < j.StartMin - slack || start > j.StartMax + slack)
                    {
                        continue;
                    }

                    foreach (var end in ends)
                    {
                        var len = (j.AtEnd ? recordingSeconds : end) - start;
                        if ((j.AtEnd && end < recordingSeconds - 0.5) || len < j.LengthMin - PatternLengthSlack || len > j.LengthMax + PatternLengthSlack)
                        {
                            continue;
                        }

                        var span = new Span(start, j.AtEnd ? recordingSeconds : end);
                        var cover = Cover(span, ads);
                        if (slack == PatternSlack[^1] && cover < PatternWideCover)
                        {
                            continue;
                        }

                        // Ad taken in, less show taken in (seconds), and a pull to the usual length. Ad only
                        // Comskip saw counts for less than ad heard elsewhere: Comskip often runs a break
                        // into the show (or the show into a break) by up to half a minute.
                        var heardInside = Cover(span, heard) * len;
                        var adInside = heardInside + PatternComskipWeight * (cover * len - heardInside);
                        var score = (2 * adInside - len) - Math.Abs(len - j.LengthMedian) * PatternLengthPull;
                        if (score > bestScore)
                        {
                            best = span;
                            bestScore = score;
                        }
                    }
                }

                if (best is not null)
                {
                    break;
                }
            }

            if (best is { } b)
            {
                result.Add(b);
            }
            else
            {
                unplaced.Add(new Span(
                    j.AtStart ? 0 : j.StartMin - PatternSlack[^1],
                    j.AtEnd ? recordingSeconds : j.StartMax + j.LengthMax + PatternSlack[^1]));
            }
        }

        // A break of the pattern that couldn't be placed: this episode is laid out differently there
        // (a finale, a special), so what was found reaching into where it was looked for stays, whole
        // (not just the part inside), unless it runs into a break that was placed.
        var placed = Normalize(result);
        result.AddRange(spans.Where(s =>
            unplaced.Any(w => w.End > s.Start && w.Start < s.End) && !placed.Any(p => p.End > s.Start && p.Start < s.End)));

        return DropShort(result);
    }

    /// <summary>
    /// The show-length check may trim a break (an opening or a stretch of story Comskip joined to
    /// ads: a break stays beside it), but it never deletes a whole break where your edited
    /// episodes have breaks: Comskip found ads there that just weren't heard elsewhere yet.
    /// </summary>
    private static bool MayGiveBack(List<Span> spans, Span c, IReadOnlyList<Span>? slots)
    {
        var rest = Subtract(spans, new[] { c });
        if (rest.Any(s => Math.Abs(s.End - c.Start) < 2 || Math.Abs(s.Start - c.End) < 2))
        {
            return true;
        }

        return slots is not { Count: > 0 } || Cover(c, slots) < 0.5;
    }

    /// <summary>The pieces that become ad: mostly heard as ads elsewhere, or a short one between two such.</summary>
    internal static List<Span> Snap(IReadOnlyList<ComskipPiece>? pieces, IReadOnlyList<Span> evidence)
    {
        var result = new List<Span>();
        if (pieces is not { Count: > 0 } || evidence.Count == 0)
        {
            return result;
        }

        var byEvidence = pieces.Select(p => !p.IsCommercial && Cover(new Span(p.Start, p.End), evidence) >= SnapCover).ToArray();
        for (var k = 0; k < pieces.Count; k++)
        {
            var p = pieces[k];
            var isAd = byEvidence[k];
            if (!isAd && !p.IsCommercial && k > 0 && k < pieces.Count - 1 && p.End - p.Start <= SandwichSeconds)
            {
                var before = pieces[k - 1].IsCommercial || byEvidence[k - 1];
                var after = pieces[k + 1].IsCommercial || byEvidence[k + 1];
                isAd = before && after && (byEvidence[k - 1] || byEvidence[k + 1]); // never joins two of Comskip's own
            }

            if (isAd)
            {
                result.Add(new Span(p.Start, p.End));
            }
        }

        return result;
    }

    public static double Cover(Span s, IReadOnlyList<Span> by) =>
        s.Length <= 0 ? 0 : Covered(Intersect(Normalize(by), s)) / s.Length;

    public static List<Span> Normalize(IEnumerable<Span> spans)
    {
        var result = new List<Span>();
        foreach (var s in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (result.Count > 0 && s.Start <= result[^1].End + 0.05)
            {
                result[^1] = new Span(result[^1].Start, Math.Max(result[^1].End, s.End));
            }
            else
            {
                result.Add(s);
            }
        }

        return result;
    }

    public static double Covered(IEnumerable<Span> spans) => Normalize(spans).Sum(s => s.Length);

    public static List<Span> Subtract(IEnumerable<Span> from, IEnumerable<Span> remove)
    {
        var cut = Normalize(remove);
        var result = new List<Span>();
        foreach (var s in Normalize(from))
        {
            var start = s.Start;
            foreach (var c in cut)
            {
                if (c.End <= start || c.Start >= s.End)
                {
                    continue;
                }

                if (c.Start > start)
                {
                    result.Add(new Span(start, c.Start));
                }

                start = Math.Max(start, c.End);
            }

            if (start < s.End)
            {
                result.Add(new Span(start, s.End));
            }
        }

        return result;
    }

    private static List<Span> Intersect(IEnumerable<Span> spans, Span window) =>
        spans.Where(s => s.End > window.Start && s.Start < window.End)
            .Select(s => new Span(Math.Max(s.Start, window.Start), Math.Min(s.End, window.End)))
            .ToList();

    private static List<Span> DropShort(IEnumerable<Span> spans) => Normalize(spans).Where(s => s.Length >= MinSpanSeconds).ToList();

    private static List<CommercialBreak> ToBreaks(IEnumerable<Span> spans) =>
        Normalize(spans)
            .Select(s => new CommercialBreak((long)(Math.Max(0, s.Start) * TicksPerSecond), (long)(s.End * TicksPerSecond)))
            .ToList();
}
