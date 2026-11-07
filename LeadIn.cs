using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Comskip;

namespace Jellyfin.Plugin.ComskipSegments;

/// <summary>
/// A DVR starts recording a minute before the program (Jellyfin's and Channels DVR's pre-padding),
/// so every such recording opens with the end of whatever aired before it. To Comskip that is just
/// more show: on 2026-11-06, 270 of 349 Comskip results marked nothing there, or only the first few
/// seconds, where every hand edit starts with a break from 0 to the program's first picture.
/// This marks that opening: from the start of the file to the end of the first black at or after
/// the scheduled start (the junction into the program), and joins a Comskip break that begins
/// there to it.
/// </summary>
/// <remarks>
/// <para>Only for recordings known to have started early. While Jellyfin records, its timer says
/// when the program starts, and the file says when the recording did: the difference is noted
/// (<see cref="Detection.LiveRecordingChannelPoller"/>). For older files: a Channels DVR file name
/// ends in the start time, and a recording whose length is a whole number of half hours plus the
/// two paddings has them (FAST channels' programs of other lengths can't be told this way). A
/// recording joined in progress, a file with its start trimmed, or one with its program start
/// already known (see <see cref="ProgramStart"/>) is left as it is.</para>
/// <para>The first black after the scheduled start is where the program begins on most channels.
/// Some put a promo or two between (Into the Badlands, Get Jiro! on 15 hand edits: 7 ended on
/// that black, 7 on a later one): there the opening stops short of the program, never past it,
/// and the editor shows the rest.</para>
/// </remarks>
public static partial class LeadIn
{
    /// <summary>The black may come this long before the scheduled start (channels run a little early).</summary>
    private const double EarlySeconds = 10;

    /// <summary>...and at most this long after it.</summary>
    private const double LateSeconds = 90;

    /// <summary>A Comskip break starting this soon after the opening joins it.</summary>
    private const double JoinSeconds = 20;

    private const double HalfHour = 1800;

    // "...2024-06-09-0659.ts": Channels DVR names a recording after when it started.
    [GeneratedRegex(@"(\d{4})-(\d\d)-(\d\d)-(\d\d)(\d\d)(?:-\d+)?$")]
    private static partial Regex NameStamp();

    /// <summary>
    /// For a recording whose timer wasn't seen while it ran (an older one, or another DVR's): how
    /// many seconds before the program it started, or null when that can't be told.
    /// <paramref name="prePadding"/>/<paramref name="postPadding"/>: Jellyfin's Live TV padding.
    /// </summary>
    public static double? Seconds(string path, double runtimeSeconds, double prePadding, double postPadding)
    {
        if (prePadding <= 0)
        {
            return null;
        }

        if (NameStamp().Match(Path.GetFileNameWithoutExtension(path)) is { Success: true } s)
        {
            var minute = int.Parse(s.Groups[5].Value, CultureInfo.InvariantCulture);
            var early = (30 - (minute % 30)) % 30 * 60.0;
            return early > 0 && early <= Math.Max(prePadding, 60) + 60 ? early : null;
        }

        // A whole number of half hours plus both paddings: started early by the pre-padding.
        if (runtimeSeconds > HalfHour && postPadding >= 0)
        {
            var program = runtimeSeconds - prePadding - postPadding;
            var off = Math.Abs(program - (Math.Round(program / HalfHour) * HalfHour));
            if (off <= 3)
            {
                return prePadding;
            }
        }

        return null;
    }

    /// <summary>Where the opening ends: the end of the first black around the scheduled start, else the scheduled start itself.</summary>
    public static double End(IReadOnlyList<(double Start, double End)> blackRuns, double leadIn) =>
        blackRuns.Where(r => r.End >= leadIn - EarlySeconds && r.Start <= leadIn + LateSeconds)
            .OrderBy(r => r.End)
            .Select(r => (double?)r.End)
            .FirstOrDefault() ?? leadIn;

    /// <summary>The stretch the black is looked for in.</summary>
    public static (double From, double To) Window(double leadIn) => (Math.Max(0, leadIn - EarlySeconds - 3), leadIn + LateSeconds + 3);

    /// <summary>
    /// The breaks with 0–<paramref name="end"/> marked, joined with any break that starts within
    /// <see cref="JoinSeconds"/> of it; the same list when the first break already covers it.
    /// </summary>
    public static IReadOnlyList<CommercialBreak> Apply(IReadOnlyList<CommercialBreak> breaks, double end, out bool changed)
    {
        var sorted = breaks.OrderBy(b => b.StartTicks).ToList();
        var endTicks = (long)(end * TimeSpan.TicksPerSecond);
        if (sorted.Count > 0 && sorted[0].StartTicks < TimeSpan.TicksPerSecond && sorted[0].EndTicks >= endTicks)
        {
            changed = false;
            return breaks;
        }

        var opening = new CommercialBreak(0, endTicks);
        var rest = new List<CommercialBreak>();
        var join = endTicks + (long)(JoinSeconds * TimeSpan.TicksPerSecond);
        foreach (var b in sorted)
        {
            if (b.StartTicks <= join && rest.Count == 0)
            {
                opening = new CommercialBreak(0, Math.Max(opening.EndTicks, b.EndTicks));
                join = opening.EndTicks;
            }
            else
            {
                rest.Add(b);
            }
        }

        rest.Insert(0, opening);
        changed = true;
        return rest;
    }

    private static double ToHalfHour(DateTime t)
    {
        var intoHalf = (t.Minute % 30 * 60) + t.Second + (t.Millisecond / 1000.0);
        return intoHalf == 0 ? 0 : HalfHour - intoHalf;
    }
}
