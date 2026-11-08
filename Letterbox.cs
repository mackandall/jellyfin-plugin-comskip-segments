using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Comskip;

namespace Jellyfin.Plugin.ComskipSegments;

/// <summary>
/// Some shows are letterboxed (black bars above and below the picture) and their ads aren't.
/// Comskip sees the shape change but still calls a short act an ad: it only scores a stretch as
/// show when it runs longer than comskip.ini's min_show_segment_length (250 s), so a 230-second
/// cold open between the recording's start and the first ads went into the break (Bureau of
/// Paranormal Investigation on Crunchyroll, 2026-11-08), and its live scans merged whole acts.
/// This measures the top bar of every keyframe and gives a long letterboxed stretch inside a
/// break back to the show, when the show is letterboxed and its breaks are not.
/// </summary>
public static partial class Letterbox
{
    /// <summary>The top strip looked at: 1/16 of the height, so pictures 2.05:1 and wider count.</summary>
    private const int StripDivisor = 16;

    /// <summary>A strip no brighter than this (8-bit, black is 16) is a black bar; the bars measured 16–30.</summary>
    private const int BarMaxLuma = 40;

    /// <summary>Of the keyframes outside the breaks, at least this share must have the bar: the show is letterboxed.</summary>
    private const double ShowBarShare = 0.8;

    /// <summary>Of the keyframes inside the breaks, at least this share must be without the bar: the ads aren't.</summary>
    private const double BreakOpenShare = 0.25;

    /// <summary>Keyframes needed outside the breaks before the show's shape is trusted.</summary>
    private const int MinShowSamples = 60;

    /// <summary>A break piece left shorter than this beside a given-back stretch goes to the show too.</summary>
    private const double MinPieceSeconds = 5;

    [GeneratedRegex(@"pts_time:(-?[0-9.]+)\s.*?lavfi\.signalstats\.YMAX=([0-9]+)", RegexOptions.Singleline)]
    private static partial Regex SampleRegex();

    /// <summary>A keyframe: its time and whether its top strip is a black bar.</summary>
    public readonly record struct Sample(double Seconds, bool Bar);

    /// <summary>The top-strip brightness of every keyframe (about 30 s for a 35-minute 1080p recording); null when ffmpeg fails.</summary>
    public static async Task<IReadOnlyList<Sample>?> MeasureAsync(string ffmpegPath, string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in new[]
        {
            "-nostdin", "-hide_banner", "-nostats", "-skip_frame", "nokey", "-i", path, "-an", "-sn", "-dn",
            "-vf", $"crop=iw:ih/{StripDivisor}:0:0,signalstats,metadata=print:key=lavfi.signalstats.YMAX",
            "-f", "null", "-"
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It may already have finished.
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
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0 ? Parse(stderr) : null;
    }

    /// <summary>The samples in ffmpeg's metadata=print output (kept apart for testing).</summary>
    public static IReadOnlyList<Sample> Parse(string ffmpegOutput) =>
        SampleRegex().Matches(ffmpegOutput)
            .Select(m => new Sample(
                double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) <= BarMaxLuma))
            .OrderBy(s => s.Seconds)
            .ToList();

    /// <summary>
    /// The breaks with every letterboxed stretch of <paramref name="minSeconds"/> or more inside
    /// them given back to the show; the same breaks when the show isn't letterboxed or its
    /// breaks are too (a letterboxed movie channel's ads). <paramref name="givenBack"/> is the
    /// seconds given back.
    /// </summary>
    public static IReadOnlyList<CommercialBreak> Apply(
        IReadOnlyList<CommercialBreak> breaks, IReadOnlyList<Sample> samples, double minSeconds, out double givenBack)
    {
        givenBack = 0;
        if (breaks.Count == 0 || samples.Count < 2)
        {
            return breaks;
        }

        bool InBreak(double t) => breaks.Any(b => t >= b.StartTicks / 1e7 && t < b.EndTicks / 1e7);
        var show = samples.Where(s => !InBreak(s.Seconds)).ToList();
        var inBreaks = samples.Where(s => InBreak(s.Seconds)).ToList();
        if (show.Count < MinShowSamples || inBreaks.Count == 0
            || show.Count(s => s.Bar) < ShowBarShare * show.Count
            || inBreaks.Count(s => !s.Bar) < BreakOpenShare * inBreaks.Count)
        {
            return breaks;
        }

        // A dark ad frame or a flash in the show must not split a stretch: bar keyframes up to a
        // few keyframe gaps apart are one stretch.
        var gaps = samples.Zip(samples.Skip(1), (a, b) => b.Seconds - a.Seconds).Where(d => d > 0).OrderBy(d => d).ToList();
        var join = Math.Max(3, 3 * (gaps.Count > 0 ? gaps[gaps.Count / 2] : 1));

        var result = new List<CommercialBreak>();
        foreach (var b in breaks.OrderBy(b => b.StartTicks))
        {
            double start = b.StartTicks / 1e7, end = b.EndTicks / 1e7;
            var bars = samples.Where(s => s.Bar && s.Seconds >= start && s.Seconds < end).Select(s => s.Seconds).ToList();
            var keep = new List<(double From, double To)>();
            var at = start;
            for (var i = 0; i < bars.Count;)
            {
                var j = i;
                while (j + 1 < bars.Count && bars[j + 1] - bars[j] <= join)
                {
                    j++;
                }

                // The stretch reaches the break's edge when its first or last bar keyframe is
                // within a join of it (the show carries on outside the break).
                var from = bars[i] - start <= join ? start : bars[i];
                var to = end - bars[j] <= join ? end : bars[j];
                if (to - from >= minSeconds)
                {
                    keep.Add((at, from));
                    givenBack += to - from;
                    at = to;
                }

                i = j + 1;
            }

            keep.Add((at, end));
            foreach (var (from, to) in keep)
            {
                if (to - from >= MinPieceSeconds)
                {
                    result.Add(new CommercialBreak((long)(from * TimeSpan.TicksPerSecond), (long)(to * TimeSpan.TicksPerSecond)));
                }
                else if (to > from && keep.Count > 1)
                {
                    givenBack += to - from;
                }
                else if (to > from)
                {
                    result.Add(new CommercialBreak((long)(from * TimeSpan.TicksPerSecond), (long)(to * TimeSpan.TicksPerSecond)));
                }
            }
        }

        return givenBack > 0 ? result : breaks;
    }
}
