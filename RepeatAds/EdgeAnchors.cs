using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Jellyfin.Plugin.ComskipSegments.RepeatAds;

/// <summary>The sound either side of one edited break's edges (null: none, or silence).</summary>
public sealed record BreakClips(uint[]? ShowBefore, uint[]? BreakFirst, uint[]? BreakLast, uint[]? ShowAfter);

/// <summary>A junction's edges pinned by sound heard at them in the edited episodes (null: not pinned).</summary>
public readonly record struct EdgeAnchor(double? Start, double? End);

/// <summary>
/// Pins break edges to the sound at them, the same in every episode: just inside the show (an
/// eyecatch, a sting, the first notes of the ending) or, failing that, just inside the break (the
/// channel's "we'll be back" countdown ending). From each of the show's edited episodes, the few
/// seconds either side of each edge you marked are looked for near there in a new recording; when
/// most of them are heard, and agree on the spot to within a second, that's the edge. Tested on 8
/// edited Witchblade episodes 2026-11-01: the show's return after its third break was found in 56
/// of 56 pairs, 54 within a second of your edge.
/// </summary>
public static class EdgeAnchors
{
    /// <summary>How much show is looked for at each edge.</summary>
    public const double ClipSeconds = 3;

    /// <summary>Mean differing bits (of 32) for "the same sound": the same eyecatch scores 2-6, unrelated sound 11-12.</summary>
    internal static double MaxMeanBits = 6.5;

    /// <summary>The same, for the clips inside the break (a countdown's last seconds under a different line of text).</summary>
    internal static double MaxMeanBitsInside = 8;

    /// <summary>Clips of silence or a steady tone (black frames) match anything quiet: skipped.</summary>
    private const double MinSelfChange = 2.0;

    /// <summary>How closely the clips must agree on where the edge is.</summary>
    private const double AgreeSeconds = 1.0;

    /// <summary>How far past the edited episodes' earliest and latest edge to look.</summary>
    private const double SearchSlack = 120;

    private static int ClipItems => (int)Math.Round(ClipSeconds / RepeatMatcher.Step);

    /// <summary>
    /// The clips at one edited episode's breaks, by break: the show before its start and the
    /// break's first seconds (none for the first break, at 0), the break's last seconds and the
    /// show after its end (none for the last, at the end of the recording).
    /// </summary>
    public static List<BreakClips> Clips(uint[] fp, IReadOnlyList<Span> breaks)
    {
        var n = ClipItems;
        uint[]? Take(int from) => from >= 0 && from + n <= fp.Length ? Usable(fp[from..(from + n)]) : null;
        var result = new List<BreakClips>();
        foreach (var b in breaks)
        {
            var s = (int)Math.Round(b.Start / RepeatMatcher.Step);
            var e = (int)Math.Round(b.End / RepeatMatcher.Step);
            result.Add(new BreakClips(Take(s - n), Take(s), Take(e - n), Take(e)));
        }

        return result;
    }

    /// <summary>
    /// Where each of the pattern's breaks starts and ends in <paramref name="fp"/>, where the
    /// edited episodes' clips (<paramref name="clips"/>: per episode, per break) agree.
    /// </summary>
    public static List<EdgeAnchor> Find(uint[] fp, IReadOnlyList<Junction> pattern, IReadOnlyList<List<BreakClips>> clips)
    {
        var result = new List<EdgeAnchor>();
        for (var k = 0; k < pattern.Count; k++)
        {
            var j = pattern[k];
            double? start = null, end = null;
            var item = ClipItems * RepeatMatcher.Step;
            if (!j.AtStart)
            {
                var from = j.StartMin - SearchSlack;
                var to = j.StartMax + SearchSlack;
                start = Agree(clips.Select(c => k < c.Count ? c[k].ShowBefore : null), fp, from - item, to, item, MaxMeanBits)
                    ?? Agree(clips.Select(c => k < c.Count ? c[k].BreakFirst : null), fp, from, to, 0, MaxMeanBitsInside);
            }

            if (!j.AtEnd)
            {
                var from = j.StartMin + j.LengthMin - SearchSlack;
                var to = j.StartMax + j.LengthMax + SearchSlack;
                end = Agree(clips.Select(c => k < c.Count ? c[k].ShowAfter : null), fp, from, to, 0, MaxMeanBits)
                    ?? Agree(clips.Select(c => k < c.Count ? c[k].BreakLast : null), fp, from - item, to, item, MaxMeanBitsInside);
            }

            result.Add(new EdgeAnchor(start, end));
        }

        return result;
    }

    /// <summary>Each clip's best spot in [from, to] (plus <paramref name="offset"/>); their agreed edge, if most were heard and agree.</summary>
    private static double? Agree(IEnumerable<uint[]?> clips, uint[] fp, double from, double to, double offset, double maxMeanBits)
    {
        var tried = 0;
        var found = new List<double>();
        foreach (var clip in clips)
        {
            if (clip is null)
            {
                continue;
            }

            tried++;
            if (Best(clip, fp, from, to, maxMeanBits) is { } at)
            {
                found.Add(at + offset);
            }
        }

        if (tried < 2 || found.Count * 2 < tried)
        {
            return null;
        }

        found.Sort();
        var median = found[found.Count / 2];
        var agreeing = found.Where(f => Math.Abs(f - median) <= AgreeSeconds).ToList();
        return agreeing.Count * 3 >= found.Count * 2 && agreeing.Count >= 2 ? agreeing.Average() : null;
    }

    /// <summary>Where <paramref name="clip"/> sounds most alike in <paramref name="fp"/> between the two times, if alike enough.</summary>
    private static double? Best(uint[] clip, uint[] fp, double from, double to, double maxMeanBits)
    {
        var lo = Math.Max(0, (int)(from / RepeatMatcher.Step));
        var hi = Math.Min(fp.Length - clip.Length, (int)(to / RepeatMatcher.Step));
        var bestBits = int.MaxValue;
        var bestAt = -1;
        for (var i = lo; i <= hi; i++)
        {
            var bits = 0;
            for (var c = 0; c < clip.Length && bits < bestBits; c++)
            {
                bits += BitOperations.PopCount(clip[c] ^ fp[i + c]);
            }

            if (bits < bestBits)
            {
                bestBits = bits;
                bestAt = i;
            }
        }

        return bestAt >= 0 && bestBits <= maxMeanBits * clip.Length ? bestAt * RepeatMatcher.Step : null;
    }

    private static uint[]? Usable(uint[]? clip)
    {
        if (clip is not { Length: > 1 })
        {
            return null;
        }

        var change = 0;
        for (var i = 1; i < clip.Length; i++)
        {
            change += BitOperations.PopCount(clip[i] ^ clip[i - 1]);
        }

        return change / (double)(clip.Length - 1) >= MinSelfChange ? clip : null;
    }
}
