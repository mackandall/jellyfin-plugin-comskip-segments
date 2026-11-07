using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ComskipSegments.Comskip;

namespace Jellyfin.Plugin.ComskipSegments;

/// <summary>
/// A check on break edges that needs nothing marked by hand: ads are sold in 15-second units, so a
/// break whose two edges are both on the junction comes out a whole number of them. Detection never
/// uses this, which is what makes it a fair ruler (the idea and the method are SmartCut's). On
/// 29.97/59.94 fps video a unit is 15.015 s: 450 frames at 1000/1001 speed. FBI's breaks came out
/// 180.21 s (12 units and a black frame) once the edges were right.
/// </summary>
/// <remarks>
/// It is a measure over many breaks, not a verdict on one: a break with a network promo of some
/// other length in it is right and still off the grid. What counts is how far above chance the
/// share on the grid is (chance: 2 × <see cref="ToleranceSeconds"/> / unit), and whether a change
/// moves it.
/// </remarks>
public static class BreakRuler
{
    /// <summary>How close to a whole number of units a break has to be.</summary>
    public const double ToleranceSeconds = 0.3;

    /// <summary>Breaks touching the first or last second of the file are cut off by the recording, not by a junction.</summary>
    private const double FileEdgeSeconds = 1.0;

    /// <summary>Shorter than this isn't an ad break (a stray few seconds).</summary>
    private const double MinBreakSeconds = 10;

    public sealed record Tally(int Recordings, int Breaks, int On15, int On5)
    {
        public static Tally Empty { get; } = new(0, 0, 0, 0);

        public Tally Add(Tally other) => new(Recordings + other.Recordings, Breaks + other.Breaks, On15 + other.On15, On5 + other.On5);
    }

    /// <summary>The 15-second unit for a video at <paramref name="fps"/> (NTSC rates run 1000/1001 slow).</summary>
    public static double Unit(double? fps) =>
        fps is { } f && (Math.Abs(f - 29.97) < 0.02 || Math.Abs(f - 59.94) < 0.04 || Math.Abs(f - 23.976) < 0.02) ? 15.015 : 15.0;

    /// <summary>Chance of a break of any length landing on the grid of <paramref name="unit"/>.</summary>
    public static double Chance(double unit) => 2 * ToleranceSeconds / unit;

    /// <summary>
    /// One recording's breaks against the grid. <paramref name="runtimeSeconds"/>: the file's length
    /// (0 if not known: the last break is then left out); <paramref name="padding"/>: seconds each
    /// edge was moved in by, added back. <paramref name="adSpans"/>: where the ads alone run (see
    /// <see cref="EdgeSnap"/>); a break with one inside is measured by it, without its black,
    /// which is up to two seconds and would put any break off the grid.
    /// </summary>
    public static Tally Measure(
        IReadOnlyList<CommercialBreak> breaks, double runtimeSeconds, double? fps, double padding = 0, IReadOnlyList<CommercialBreak>? adSpans = null)
    {
        var slack = (long)((padding + 0.05) * 1e7);
        var unit = Unit(fps);
        int n = 0, on15 = 0, on5 = 0;
        var sorted = breaks.OrderBy(b => b.StartTicks).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var s = sorted[i].StartTicks / 1e7 - padding;
            var e = sorted[i].EndTicks / 1e7 + padding;
            var atEnd = runtimeSeconds > 0 ? e >= runtimeSeconds - FileEdgeSeconds : i == sorted.Count - 1;
            if (s <= FileEdgeSeconds || atEnd || e - s < MinBreakSeconds)
            {
                continue;
            }

            var b = sorted[i];
            var length = adSpans?.FirstOrDefault(a => a.StartTicks >= b.StartTicks - slack && a.EndTicks <= b.EndTicks + slack) is { EndTicks: > 0 } ad
                ? (ad.EndTicks - ad.StartTicks) / 1e7
                : e - s;
            n++;
            if (OnGrid(length, unit))
            {
                on15++;
            }

            if (OnGrid(length, unit / 3))
            {
                on5++;
            }
        }

        return new Tally(n > 0 ? 1 : 0, n, on15, on5);
    }

    private static bool OnGrid(double length, double unit) =>
        Math.Abs(length - Math.Round(length / unit) * unit) <= ToleranceSeconds;
}
