using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ComskipSegments.Comskip;

namespace Jellyfin.Plugin.ComskipSegments.RepeatAds;

/// <summary>
/// Folds repeated ads into a recording's breaks. Breaks only grow, so applying the same repeats
/// again changes nothing:
/// - repeats up to <see cref="RepeatGapSeconds"/> apart count as one stretch: the ads between them,
///   heard only once so far, belong to the same break (a show's segments run minutes, not seconds);
/// - a stretch overlapping or within <see cref="BreakGapSeconds"/> of a break extends it (Comskip's
///   edges were often a little short of the repeat); one between two breaks joins them;
/// - a stretch next to no break becomes a break of its own if it's at least <see cref="MinNewSeconds"/>.
/// Two of Comskip's own breaks are never joined unless a repeat lies between them.
/// </summary>
public static class BreakMerge
{
    public const double RepeatGapSeconds = 60;
    public const double BreakGapSeconds = 30;
    public const double MinNewSeconds = 10;
    private const long TicksPerSecond = 10_000_000L;

    /// <param name="added">Seconds that weren't in a break before.</param>
    public static List<CommercialBreak> Merge(
        IReadOnlyList<CommercialBreak> breaks, IEnumerable<(double Start, double End)> repeats, out double added)
    {
        var spans = breaks.Select(b => (Start: b.StartTicks / (double)TicksPerSecond, End: b.EndTicks / (double)TicksPerSecond, Comskip: true))
            .OrderBy(s => s.Start).ToList();
        var before = Covered(spans.Select(s => (s.Start, s.End)));

        // Repeats close together are one stretch.
        var stretches = new List<(double Start, double End)>();
        foreach (var r in repeats.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            if (stretches.Count > 0 && r.Start - stretches[^1].End <= RepeatGapSeconds)
            {
                stretches[^1] = (stretches[^1].Start, Math.Max(stretches[^1].End, r.End));
            }
            else
            {
                stretches.Add(r);
            }
        }

        foreach (var r in stretches)
        {
            var near = spans.Where(s => r.Start - s.End <= BreakGapSeconds && s.Start - r.End <= BreakGapSeconds).ToList();
            if (near.Count == 0)
            {
                if (r.End - r.Start >= MinNewSeconds)
                {
                    spans.Add((r.Start, r.End, false));
                }

                continue;
            }

            foreach (var s in near)
            {
                spans.Remove(s);
            }

            spans.Add((Math.Min(r.Start, near.Min(s => s.Start)), Math.Max(r.End, near.Max(s => s.End)), false));
        }

        var merged = spans.OrderBy(s => s.Start).ToList();
        added = Covered(merged.Select(s => (s.Start, s.End))) - before;
        return merged
            .Select(s => new CommercialBreak((long)(Math.Max(0, s.Start) * TicksPerSecond), (long)(s.End * TicksPerSecond)))
            .ToList();
    }

    private static double Covered(IEnumerable<(double Start, double End)> spans)
    {
        double total = 0, reach = double.MinValue;
        foreach (var (s, e) in spans.OrderBy(x => x.Start))
        {
            if (e <= reach)
            {
                continue;
            }

            total += e - Math.Max(s, reach);
            reach = e;
        }

        return total;
    }
}
