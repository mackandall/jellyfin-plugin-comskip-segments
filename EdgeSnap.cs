using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;
using Jellyfin.Plugin.ComskipSegments.Comskip;

namespace Jellyfin.Plugin.ComskipSegments;

/// <summary>
/// Puts each break edge on the picture where the junction really is. Comskip's edges, and the
/// repeat-ad check's, are estimates from frame scores and sound; the junction itself is a run of
/// black frames (most US broadcast) or a hard cut. A short stretch around each edge is decoded and:
/// a break's start goes to the first black frame of the black run it's in or the nearest one (the
/// show has ended), its end to the first picture after it (the show is back), so the black is
/// part of the break and no show is lost; with no black near, both go to the nearest hard cut.
/// With neither within <c>window</c> seconds the edge stays where it was.
/// </summary>
/// <remarks>
/// The nearest cut, not the strongest: cuts inside an ad are more dramatic than the one at its
/// edge, and picking the strongest drags the edge into the ad (SmartCut's measurements, which
/// match what this found on FBI/Elsbeth). Edges at the very start or end of the file aren't
/// junctions and aren't touched.
/// </remarks>
public static partial class EdgeSnap
{
    /// <summary>Average luma (8-bit limited range: black is 16, white 235) under which a picture counts as black.</summary>
    private const double BlackLuma = 24;

    /// <summary>
    /// ...and the brightest tenth of its pixels (YHIGH) under this: junction black measures 16 all
    /// over (a station bug in a corner is far less than a tenth), while a dark night scene that
    /// averages near black still has lights and faces above it.
    /// </summary>
    private const double BlackHigh = 32;

    /// <summary>ffmpeg scene score from which a change of picture counts as a cut.</summary>
    private const double CutScore = 0.3;

    /// <summary>Extra decoded either side of the window, so a black run at its edge is seen whole.</summary>
    private const double Margin = 0.4;

    /// <summary>How far a black run the edge is in may go on (decoded on that side): 1.1 s measured on Elsbeth.</summary>
    private const double BlackReach = 2.5;

    /// <summary>Edges this close to the start or end of the file aren't junctions.</summary>
    private const double FileEdgeSeconds = 1.0;

    /// <summary>At most this many edges are looked at per recording (each is a seek and a second or two of decoding).</summary>
    private const int MaxEdges = 60;

    public enum Kind
    {
        Unchanged,
        Black,
        Cut
    }

    /// <summary>
    /// The breaks with their edges moved, and how many went where. <paramref name="AdSpans"/>: for
    /// each break whose two edges are both on a junction, the stretch between its black (the ads
    /// alone, which <see cref="BreakRuler"/> measures).
    /// </summary>
    public sealed record Result(IReadOnlyList<CommercialBreak> Breaks, IReadOnlyList<CommercialBreak> AdSpans, int Black, int Cut, int Unchanged, double MaxMoveSeconds);

    [GeneratedRegex(@"^frame:\s*\d+\s+pts:\s*-?\d+\s+pts_time:(-?[0-9.]+)", RegexOptions.Multiline)]
    private static partial Regex FrameLine();

    public static async Task<Result> SnapAsync(
        string ffmpegPath, string ffprobePath, string path, IReadOnlyList<CommercialBreak> breaks, double window, CancellationToken ct)
    {
        var (start, duration) = await AudioChannelScanner.ProbeFormatTimes(ffprobePath, path, ct).ConfigureAwait(false);
        var result = new List<CommercialBreak>(breaks.Count);
        var ads = new List<CommercialBreak?>(breaks.Count);
        int black = 0, cut = 0, unchanged = 0, looked = 0;
        double maxMove = 0;
        foreach (var b in breaks.OrderBy(b => b.StartTicks))
        {
            var s = b.StartTicks / 1e7;
            var e = b.EndTicks / 1e7;
            var newS = s;
            var newE = e;
            double? innerS = null, innerE = null;
            if (s > FileEdgeSeconds && looked++ < MaxEdges)
            {
                var (t, kind, inner) = await EdgeAsync(ffmpegPath, ffprobePath, path, start, s, window, isStart: true, ct).ConfigureAwait(false);
                Count(kind);
                (newS, innerS) = (t, inner);
            }

            if ((duration <= 0 || e < duration - FileEdgeSeconds) && looked++ < MaxEdges)
            {
                var (t, kind, inner) = await EdgeAsync(ffmpegPath, ffprobePath, path, start, e, window, isStart: false, ct).ConfigureAwait(false);
                Count(kind);
                (newE, innerE) = (t, inner);
            }

            if (newE - newS < 1)
            {
                (newS, newE) = (s, e); // snapping would have all but closed it: keep it as it was
                (innerS, innerE) = (null, null);
            }

            ads.Add(innerS is { } a && innerE is { } z && z > a ? new CommercialBreak((long)Math.Round(a * 1e7), (long)Math.Round(z * 1e7)) : null);

            maxMove = Math.Max(maxMove, Math.Max(Math.Abs(newS - s), Math.Abs(newE - e)));
            result.Add(new CommercialBreak((long)Math.Round(newS * 1e7), (long)Math.Round(newE * 1e7)));
        }

        // Two breaks a junction apart can snap onto the same black run: never let them overlap.
        for (var i = 1; i < result.Count; i++)
        {
            if (result[i].StartTicks < result[i - 1].EndTicks)
            {
                result[i - 1] = new CommercialBreak(result[i - 1].StartTicks, Math.Max(result[i - 1].EndTicks, result[i].EndTicks));
                result.RemoveAt(i);
                ads[i - 1] = null;
                ads.RemoveAt(i--);
            }
        }

        return new Result(result, ads.Where(a => a is not null).Select(a => a!.Value).ToList(), black, cut, unchanged, maxMove);

        void Count(Kind k)
        {
            switch (k)
            {
                case Kind.Black: black++; break;
                case Kind.Cut: cut++; break;
                default: unchanged++; break;
            }
        }
    }

    /// <summary>Where one edge goes, from the pictures around it (kept apart from the decoding for testing).</summary>
    /// <returns>The edge; how it was placed; where the ads begin or end at it (the far side of its black run), null when not on a junction.</returns>
    internal static (double T, Kind Kind, double? Inner) Place(IReadOnlyList<(double T, double Luma, double Scene)> frames, double t, double window, bool isStart)
    {
        // Black runs: [first black frame, first frame after it). A run cut off by either end of
        // what was decoded has an unknown end on that side and is only used for the other one.
        var runs = new List<(double Start, double End, bool StartKnown, bool EndKnown)>();
        int? runFrom = null;
        for (var i = 0; i < frames.Count; i++)
        {
            var isBlack = frames[i].Luma < BlackLuma;
            if (isBlack && runFrom is null)
            {
                runFrom = i;
            }
            else if (!isBlack && runFrom is { } from)
            {
                runs.Add((frames[from].T, frames[i].T, from > 0, true));
                runFrom = null;
            }
        }

        if (runFrom is { } open)
        {
            runs.Add((frames[open].T, frames[^1].T, open > 0, false));
        }

        // The edge is already in a black run (Comskip puts it there): the run's own end, however
        // long the black lasts. Otherwise the nearest run whose start (for a break's start) or
        // end (for its end) is within the window.
        const double OneFrame = 0.034;
        foreach (var r in runs)
        {
            if (r.Start - OneFrame <= t && t <= r.End + OneFrame && (isStart ? r.StartKnown : r.EndKnown))
            {
                return OnRun(r);
            }
        }

        var near = runs
            .Where(r => isStart ? r.StartKnown : r.EndKnown)
            .Where(r => Math.Abs((isStart ? r.Start : r.End) - t) <= window)
            .OrderBy(r => Math.Abs((isStart ? r.Start : r.End) - t))
            .ToList();
        if (near.Count > 0)
        {
            return OnRun(near[0]);
        }

        // A cut is one picture unlike the one before it, between pictures like their neighbours. In
        // a fade every picture differs from the last (scores of 0.3-0.6 frame after frame), and
        // that's no cut to put an edge on.
        var cuts = Enumerable.Range(1, Math.Max(0, frames.Count - 1))
            .Where(i => frames[i].Scene >= CutScore && Math.Abs(frames[i].T - t) <= window
                && frames[i - 1].Scene < CutScore / 2 && (i + 1 >= frames.Count || frames[i + 1].Scene < CutScore / 2))
            .Select(i => frames[i])
            .OrderBy(f => Math.Abs(f.T - t))
            .ToList();
        return cuts.Count > 0 ? (cuts[0].T, Kind.Cut, cuts[0].T) : (t, Kind.Unchanged, null);

        (double, Kind, double?) OnRun((double Start, double End, bool StartKnown, bool EndKnown) r) =>
            isStart
                ? (r.Start, Kind.Black, r.EndKnown ? r.End : null)
                : (r.End, Kind.Black, r.StartKnown ? r.Start : null);
    }

    /// <summary>The black runs (first black picture, first picture after it) from <paramref name="from"/> to <paramref name="to"/>, by the same test as the edges.</summary>
    public static async Task<IReadOnlyList<(double Start, double End)>> BlackRunsAsync(
        string ffmpegPath, string ffprobePath, string path, double from, double to, CancellationToken ct)
    {
        var (start, _) = await AudioChannelScanner.ProbeFormatTimes(ffprobePath, path, ct).ConfigureAwait(false);
        var frames = await DecodeAsync(ffmpegPath, ffprobePath, path, start, from, to, ct).ConfigureAwait(false);
        var runs = new List<(double Start, double End)>();
        int? runFrom = null;
        for (var i = 0; i < frames.Count; i++)
        {
            var isBlack = frames[i].Luma < BlackLuma;
            if (isBlack && runFrom is null)
            {
                runFrom = i;
            }
            else if (!isBlack && runFrom is { } f)
            {
                // A run already going when decoding began has no known start; its end still counts.
                runs.Add((frames[f].T, frames[i].T));
                runFrom = null;
            }
        }

        return runs;
    }

    private static async Task<(double T, Kind Kind, double? Inner)> EdgeAsync(
        string ffmpegPath, string ffprobePath, string path, double fileStart, double t, double window, bool isStart, CancellationToken ct)
    {
        try
        {
            // Far enough both ways to see the whole of the black run the edge is in: its far side
            // is where the ads begin or end (for the ruler), and either side may be the edge.
            var reach = Math.Max(BlackReach, window + Margin);
            var (from, to) = (t - reach, t + reach);
            var frames = await DecodeAsync(ffmpegPath, ffprobePath, path, fileStart, from, to, ct).ConfigureAwait(false);
            return frames.Count < 3 ? (t, Kind.Unchanged, null) : Place(frames, t, window, isStart);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (t, Kind.Unchanged, null);
        }
    }

    /// <summary>Every picture from <paramref name="from"/> to <paramref name="to"/> (seconds from the start of the file): its time, average luma, scene score.</summary>
    private static async Task<IReadOnlyList<(double T, double Luma, double Scene)>> DecodeAsync(
        string ffmpegPath, string ffprobePath, string path, double fileStart, double from, double to, CancellationToken ct)
    {
        from = Math.Max(0, from);
        // Start decoding a little before the keyframe ahead of the stretch, as FrameStrip does,
        // so every picture in it decodes whole; keep the file's own timestamps.
        var keys = await Keyframes.AroundAsync(ffprobePath, path, from, 12, ct).ConfigureAwait(false);
        var keyBefore = keys.Where(k => k <= from).Cast<double?>().LastOrDefault();
        var seek = Math.Max(0, (keyBefore ?? from - 8) - 0.5);
        var a = (fileStart + from).ToString("F4", CultureInfo.InvariantCulture);
        var b = (fileStart + to).ToString("F4", CultureInfo.InvariantCulture);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-nostdin", "-v", "error", "-threads", "2", "-copyts",
            "-ss", seek.ToString("F3", CultureInfo.InvariantCulture),
            "-t", (to - seek + 0.5).ToString("F3", CultureInfo.InvariantCulture),
            "-i", path, "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"scale=320:-2,format=yuv420p,select='gte(scene\\,0)*between(t\\,{a}\\,{b})',signalstats,metadata=print:file=-",
            "-fps_mode", "passthrough", "-f", "null", "-"
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        using var stop = ct.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        });
        _ = process.StandardError.ReadToEndAsync(ct);
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return Parse(output, fileStart);
    }

    /// <summary>
    /// ffmpeg's <c>metadata=print</c> output as (time from the start of the file, luma, scene score)
    /// per picture. A picture whose bright pixels rule out black (see <see cref="BlackHigh"/>) gets
    /// a luma no lower than <see cref="BlackLuma"/>, so only real black reads as black.
    /// </summary>
    internal static IReadOnlyList<(double T, double Luma, double Scene)> Parse(string output, double fileStart)
    {
        var frames = new List<(double T, double Luma, double Scene)>();
        double? t = null;
        double luma = double.NaN, high = double.NaN, scene = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var m = FrameLine().Match(line);
            if (m.Success)
            {
                Flush();
                t = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - fileStart;
                luma = double.NaN;
                high = double.NaN;
                scene = 0;
            }
            else if (line.StartsWith("lavfi.scene_score=", StringComparison.Ordinal))
            {
                double.TryParse(line.AsSpan(18), NumberStyles.Float, CultureInfo.InvariantCulture, out scene);
            }
            else if (line.StartsWith("lavfi.signalstats.YAVG=", StringComparison.Ordinal)
                && double.TryParse(line.AsSpan(23), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                luma = y;
            }
            else if (line.StartsWith("lavfi.signalstats.YHIGH=", StringComparison.Ordinal)
                && double.TryParse(line.AsSpan(24), NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            {
                high = h;
            }
        }

        Flush();
        return frames;

        void Flush()
        {
            if (t is { } time && !double.IsNaN(luma))
            {
                frames.Add((Math.Round(time, 4), high >= BlackHigh ? Math.Max(luma, BlackLuma) : luma, scene));
            }
        }
    }
}
