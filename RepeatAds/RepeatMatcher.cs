using System;
using System.Collections.Generic;
using System.Numerics;

namespace Jellyfin.Plugin.ComskipSegments.RepeatAds;

/// <summary>
/// Finds stretches of sound two recordings share, from their Chromaprint fingerprints (one 32-bit
/// item every <see cref="Step"/> seconds). Commercials air over and over, in many shows; a stretch
/// heard in a recording of ANOTHER series is an ad. Tested on 50 recordings (2026-11-01): every
/// such repeat Comskip had called show was a real ad (61 of 61 that could be checked); repeats
/// within the same airing also caught anime openings, endings and eyecatches, so they aren't used.
/// </summary>
public static class RepeatMatcher
{
    /// <summary>Seconds per fingerprint item (Chromaprint at 11025 Hz, 1365-sample step).</summary>
    public const double Step = 1365.0 / 11025.0;

    private const int MaxHamming = 10;          // of 32 bits: the same sound
    private const double MaxMeanHamming = 3.0;  // a real repeat averages 0.3-3; one item off averages 4-7
    private const double MinSelfChange = 2.0;   // silence and steady tones barely change item to item
    private const double MinRunSeconds = 8.0;   // ads run 15/30 s; their cards and bumpers 5-10 s
    private const int MaxGroup = 40;            // values seen this often in one recording carry no information
    private const int MinVotes = 6;             // exactly equal items on one alignment before checking it
    private const double PaddingSeconds = 150;  // back-to-back recordings of a channel overlap: not a repeat
    private const int Window = 9;               // about a second

    /// <summary>A shared stretch: seconds in the first recording, and where it is in the other.</summary>
    public readonly record struct Match(double Start, double End, double OtherStart, double OtherEnd);

    /// <summary>Where each value occurs in a fingerprint (values occurring too often left out).</summary>
    public static Dictionary<uint, int[]> Index(uint[] fp)
    {
        var lists = new Dictionary<uint, List<int>>();
        for (var i = 0; i < fp.Length; i++)
        {
            if (!lists.TryGetValue(fp[i], out var l))
            {
                lists[fp[i]] = l = new List<int>(1);
            }

            l.Add(i);
        }

        var index = new Dictionary<uint, int[]>(lists.Count);
        foreach (var (v, l) in lists)
        {
            if (l.Count <= MaxGroup)
            {
                index[v] = l.ToArray();
            }
        }

        return index;
    }

    /// <summary>The stretches <paramref name="a"/> (indexed as <paramref name="aIndex"/>) shares with <paramref name="b"/>.</summary>
    public static List<Match> Find(uint[] a, Dictionary<uint, int[]> aIndex, uint[] b)
    {
        // Votes per alignment: b[i + d] == a[i].
        var votes = new Dictionary<int, int>();
        for (var j = 0; j < b.Length; j++)
        {
            if (aIndex.TryGetValue(b[j], out var positions))
            {
                foreach (var i in positions)
                {
                    var d = j - i;
                    votes[d] = votes.GetValueOrDefault(d) + 1;
                }
            }
        }

        var found = new List<Match>();
        var la = a.Length * Step;
        var lb = b.Length * Step;
        foreach (var (d, v) in votes)
        {
            var before = votes.GetValueOrDefault(d - 1);
            var after = votes.GetValueOrDefault(d + 1);
            if (v + before + after < MinVotes || v < before || v <= after)
            {
                continue; // too few, or a neighbour is the better alignment
            }

            foreach (var (s, t) in Runs(a, b, d))
            {
                var m = new Match(s * Step, t * Step, (s + d) * Step, (t + d) * Step);
                if ((m.End > la - PaddingSeconds && m.OtherStart < PaddingSeconds)
                    || (m.OtherEnd > lb - PaddingSeconds && m.Start < PaddingSeconds))
                {
                    continue; // the end of one recording heard at the start of the next: one broadcast
                }

                found.Add(m);
            }
        }

        return found;
    }

    // Item ranges [s, t) of a where a[i] sounds like b[i + d].
    private static IEnumerable<(int Start, int End)> Runs(uint[] a, uint[] b, int d)
    {
        var lo = Math.Max(0, -d);
        var hi = Math.Min(a.Length, b.Length - d);
        var n = hi - lo;
        if (n < Window)
        {
            yield break;
        }

        var ham = new int[n];
        for (var k = 0; k < n; k++)
        {
            ham[k] = BitOperations.PopCount(a[lo + k] ^ b[lo + k + d]);
        }

        // Centred one-second window: "matching" where at least half of it matches.
        var good = new bool[n];
        var inWindow = 0;
        const int half = Window / 2;
        for (var k = 0; k < Math.Min(half, n); k++)
        {
            inWindow += ham[k] <= MaxHamming ? 1 : 0;
        }

        for (var k = 0; k < n; k++)
        {
            if (k + half < n)
            {
                inWindow += ham[k + half] <= MaxHamming ? 1 : 0;
            }

            if (k - half - 1 >= 0)
            {
                inWindow -= ham[k - half - 1] <= MaxHamming ? 1 : 0;
            }

            good[k] = inWindow * 2 >= Window;
        }

        var minItems = (int)(MinRunSeconds / Step);
        var start = -1;
        for (var k = 0; k <= n; k++)
        {
            if (k < n && good[k])
            {
                if (start < 0)
                {
                    start = k;
                }

                continue;
            }

            if (start >= 0 && k - start >= minItems && Acceptable(a, ham, lo, start, k))
            {
                yield return (lo + start, lo + k);
            }

            start = -1;
        }
    }

    private static bool Acceptable(uint[] a, int[] ham, int lo, int start, int end)
    {
        long sum = 0, change = 0;
        for (var k = start; k < end; k++)
        {
            sum += ham[k];
            if (k > start)
            {
                change += BitOperations.PopCount(a[lo + k] ^ a[lo + k - 1]);
            }
        }

        var len = end - start;
        return sum <= MaxMeanHamming * len && change >= MinSelfChange * (len - 1);
    }
}
