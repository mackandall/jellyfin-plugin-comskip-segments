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
/// The trailing integer is Comskip's action code; 0 == commercial. We deliberately
/// filter on that code rather than assuming every row is a commercial, because other
/// .ini settings can emit different actions.
/// </summary>
public static class EdlParser
{
    private const long TicksPerSecond = 10_000_000L;
    private const int CommercialAction = 0;

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
                || action != CommercialAction)
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
}
