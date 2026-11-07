using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;

namespace Jellyfin.Plugin.ComskipSegments.Comskip;

/// <summary>
/// Puts Comskip's times on the recording's clock. Comskip counts from the first picture it can
/// decode (it takes that picture's time off every frame: <c>initial_pts</c> in its
/// <c>mpeg2dec.c</c>), but Jellyfin, the players and everything else here count from the start
/// of the file. A recording that starts partway through a group of pictures has nothing to
/// decode until the next keyframe, so every break Comskip reports is early by that gap: 1.95 s
/// on an FBI recording, 0.64 s on an Elsbeth one (measured 2026-11-06: the black frame at each
/// junction sat exactly that far after Comskip's time, every break).
/// </summary>
public static class ComskipClock
{
    /// <summary>A gap longer than this isn't a missing first group of pictures; it's left alone.</summary>
    private const double MaxOffsetSeconds = 15;

    /// <summary>How far into the file the first picture is looked for.</summary>
    private const double LookSeconds = 20;

    /// <summary>Recordings still being written: their offset, worked out once (the live overlay asks every few seconds).</summary>
    private static readonly ConcurrentDictionary<string, double> LiveOffsets = new(StringComparer.Ordinal);

    /// <summary>
    /// Seconds to add to Comskip's times for <paramref name="path"/>: how long after the start of
    /// the file its first decodable picture comes. 0 when there's no video or it can't be read.
    /// </summary>
    public static async Task<double> OffsetAsync(string ffprobePath, string path, CancellationToken ct)
    {
        var (start, _) = await AudioChannelScanner.ProbeFormatTimes(ffprobePath, path, ct).ConfigureAwait(false);
        var first = await FirstPictureAsync(ffprobePath, path, ct).ConfigureAwait(false);
        if (first is not { } t)
        {
            return 0;
        }

        var offset = Math.Round(t - start, 3);
        return offset is > 0 and <= MaxOffsetSeconds ? offset : 0;
    }

    /// <summary><see cref="OffsetAsync"/>, remembered per path, for a recording still being written.</summary>
    public static async Task<double> LiveOffsetAsync(string ffprobePath, string path, CancellationToken ct)
    {
        if (LiveOffsets.TryGetValue(path, out var known))
        {
            return known;
        }

        var offset = await OffsetAsync(ffprobePath, path, ct).ConfigureAwait(false);
        if (LiveOffsets.Count > 50)
        {
            LiveOffsets.Clear(); // only a handful are ever recording at once
        }

        LiveOffsets[path] = offset;
        return offset;
    }

    /// <summary>The breaks moved later by <paramref name="offset"/> seconds.</summary>
    public static IReadOnlyList<CommercialBreak> Shift(IReadOnlyList<CommercialBreak> breaks, double offset)
    {
        if (offset == 0)
        {
            return breaks;
        }

        var ticks = (long)Math.Round(offset * TimeSpan.TicksPerSecond);
        return breaks.Select(b => new CommercialBreak(b.StartTicks + ticks, b.EndTicks + ticks)).ToList();
    }

    /// <summary>Comskip's pieces moved later by <paramref name="offset"/> seconds.</summary>
    public static IReadOnlyList<ComskipPiece>? Shift(IReadOnlyList<ComskipPiece>? pieces, double offset) =>
        pieces is null || offset == 0
            ? pieces
            : pieces.Select(p => new ComskipPiece(p.IsCommercial, p.Start + offset, p.End + offset)).ToList();

    /// <summary>The time (on the stream's own clock) of the first video picture that decodes; null when there's none.</summary>
    private static async Task<double?> FirstPictureAsync(string ffprobePath, string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-read_intervals", string.Create(CultureInfo.InvariantCulture, $"%+{LookSeconds}"),
            "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", path
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        _ = process.StandardError.ReadToEndAsync(ct);
        double? first = null;
        try
        {
            // The first line is all that's wanted: stop ffprobe there rather than decode the rest.
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (double.TryParse(line.Trim().TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                {
                    first = t;
                    break;
                }
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return first;
    }
}
