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
/// Some streaming channels (Pluto) open every recording with about a minute of total silence that
/// ends in a few seconds of black, then the show. Comskip often stops partway into that silence,
/// and the repeat-ad learning can't place the edge either (a silent clip matches anything). This
/// finds where such an opening ends: at the start of the black that ends the silence, which is
/// where you end the opening when you edit by hand.
/// </summary>
public static partial class SilentOpening
{
    /// <summary>How much of the recording is looked at.</summary>
    private const double LookSeconds = 150;

    /// <summary>The opening must be silent for at least this long to count.</summary>
    private const double MinSilentSeconds = 20;

    /// <summary>The opening it finds must be at least this long.</summary>
    private const double MinOpeningSeconds = 10;

    /// <summary>The black must last until at most this much before the sound comes back (it may go on after it: some shows open on black).</summary>
    private const double BlackToSoundSeconds = 1.5;

    [GeneratedRegex(@"silence_(start|end): (-?[0-9.]+)")]
    private static partial Regex SilenceRegex();

    [GeneratedRegex(@"black_start:([0-9.]+) black_end:([0-9.]+)")]
    private static partial Regex BlackRegex();

    /// <summary>Seconds from the start where the silent opening ends, or null when the recording doesn't open that way.</summary>
    public static async Task<double?> FindEndAsync(string ffmpegPath, string path, CancellationToken ct)
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
            "-nostdin", "-hide_banner", "-t", LookSeconds.ToString(CultureInfo.InvariantCulture), "-i", path,
            "-vf", "blackdetect=d=0.03:pix_th=0.1", "-af", "silencedetect=n=-50dB:d=0.25", "-f", "null", "-"
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

    /// <summary>The rule, from ffmpeg's blackdetect/silencedetect output (kept apart for testing).</summary>
    public static double? Parse(string ffmpegOutput)
    {
        // The first silence must start at the very beginning and end (sound comes back) after MinSilentSeconds.
        double? silenceStart = null, soundBack = null;
        foreach (Match m in SilenceRegex().Matches(ffmpegOutput))
        {
            var t = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Value == "start")
            {
                silenceStart ??= t;
            }
            else if (silenceStart is not null)
            {
                soundBack = t;
                break;
            }
        }

        if (silenceStart is not { } s0 || s0 > 0.5 || soundBack is not { } back || back < MinSilentSeconds)
        {
            return null;
        }

        // ...and a stretch of black has to be there as the sound comes back: the lead-in's fade
        // into the show. The opening ends where the last such black starts.
        var black = BlackRegex().Matches(ffmpegOutput)
            .Select(m => (Start: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), End: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .Where(b => b.Start < back && b.End >= back - BlackToSoundSeconds)
            .OrderByDescending(b => b.Start)
            .FirstOrDefault();
        // A recording that opens on silent black from the very start is a quiet opening, not a lead-in.
        return black == default || black.Start < MinOpeningSeconds ? null : black.Start;
    }

    /// <summary>
    /// The breaks with the opening marked to <paramref name="end"/>: a break that starts in the
    /// opening is stretched to cover it (never shortened), and one is added when there's none.
    /// </summary>
    public static IReadOnlyList<CommercialBreak> Apply(IReadOnlyList<CommercialBreak> breaks, double end)
    {
        var endTicks = (long)(end * TimeSpan.TicksPerSecond);
        var start = new CommercialBreak(0, endTicks);
        var rest = new List<CommercialBreak>();
        foreach (var b in breaks.OrderBy(b => b.StartTicks))
        {
            if (b.StartTicks <= start.EndTicks)
            {
                start = new CommercialBreak(0, Math.Max(start.EndTicks, b.EndTicks));
            }
            else
            {
                rest.Add(b);
            }
        }

        rest.Insert(0, start);
        return rest;
    }
}
