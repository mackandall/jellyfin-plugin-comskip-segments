using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ComskipSegments.Comskip;

/// <summary>A stretch between two of Comskip's cut points, and whether Comskip called it commercial.</summary>
public readonly record struct ComskipPiece(bool IsCommercial, double Start, double End);

/// <summary>
/// Reads Comskip's pieces from its log (written when <c>verbose</c> is on): the "Block list after
/// weighing" table, one row per piece, <c>--</c> for commercial and <c>++</c> for show, e.g.
/// <code>  3:--   32  23   9  14956  15876   249.50s   264.84s    15.35s ...</code>
/// </summary>
public static class ComskipPieces
{
    private const string Heading = "Block list after weighing";

    private static readonly Regex Row = new(
        @"^\s*\d+:(?<kind>\+\+|--)\s+\d+\s+\d+\s+\d+\s+\d+\s+\d+\s+(?<start>[\d.]+)s\s+(?<end>[\d.]+)s",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ComskipPiece> Parse(string logText)
    {
        var result = new List<ComskipPiece>();
        var at = logText.LastIndexOf(Heading, StringComparison.Ordinal);
        if (at < 0)
        {
            return result;
        }

        foreach (Match m in Row.Matches(logText, at))
        {
            if (double.TryParse(m.Groups["start"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                && double.TryParse(m.Groups["end"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var end)
                && end > start)
            {
                result.Add(new ComskipPiece(m.Groups["kind"].Value == "--", start, end));
            }
        }

        return result;
    }

    public static IReadOnlyList<ComskipPiece>? TryRead(string logPath)
    {
        try
        {
            return File.Exists(logPath) ? Parse(File.ReadAllText(logPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
