using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.ComskipSegments.Comskip;

public readonly record struct CommercialBreak(long StartTicks, long EndTicks);

/// <summary>
/// Parses a Comskip .edl file.
///
/// Format observed in testing (tab-separated, seconds as floats):
///     308.07\t428.86\t0
///     620.32\t837.34\t0
/// The trailing integer is the EDL action code. Comskip writes 0 (cut); other DVRs'
/// commercial detectors (Channels DVR, Emby, Comskip set to Kodi-style output) write 3
/// ("commercial break"), e.g. <c>0\t145.79\t3</c>. Both mean "skip this"; the other
/// actions (1 mute, 2 scene marker) aren't commercials and are ignored.
/// </summary>
public static class EdlParser
{
    private const long TicksPerSecond = 10_000_000L;
    private const int CutAction = 0;
    private const int CommercialBreakAction = 3;

    public static IReadOnlyList<CommercialBreak> Parse(string edlPath, double paddingSeconds)
    {
        var breaks = new List<CommercialBreak>();

        foreach (var raw in File.ReadLines(edlPath))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var action)
                || (action != CutAction && action != CommercialBreakAction))
            {
                continue;
            }

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
            {
                continue;
            }

            // Optional inward padding so the skip doesn't clip real content.
            start += paddingSeconds;
            end -= paddingSeconds;
            if (end <= start)
            {
                continue;
            }

            breaks.Add(new CommercialBreak(
                (long)(start * TicksPerSecond),
                (long)(end * TicksPerSecond)));
        }

        return breaks;
    }

    /// <summary>Each break trimmed by <paramref name="paddingSeconds"/> at both ends (as <see cref="Parse"/> does); ones that close up are dropped.</summary>
    public static IReadOnlyList<CommercialBreak> Pad(IReadOnlyList<CommercialBreak> breaks, double paddingSeconds)
    {
        var pad = (long)(paddingSeconds * TicksPerSecond);
        var result = new List<CommercialBreak>(breaks.Count);
        foreach (var b in breaks)
        {
            if (b.EndTicks - pad > b.StartTicks + pad)
            {
                result.Add(new CommercialBreak(b.StartTicks + pad, b.EndTicks - pad));
            }
        }

        return result;
    }
}

/// <summary>Writes commercial breaks as an .edl, the way Comskip does (seconds, tab-separated, action 0).</summary>
public static class EdlWriter
{
    public static void Write(string edlPath, IEnumerable<CommercialBreak> breaks)
    {
        var lines = new List<string>();
        foreach (var b in breaks)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{b.StartTicks / 1e7:F2}\t{b.EndTicks / 1e7:F2}\t0"));
        }

        var tmp = edlPath + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, edlPath, overwrite: true);
    }
}
