using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ComskipSegments.ProgramStart;

/// <summary>
/// Recordings that JellyDVR Schedule lengthened for a sport that ran late ("Sports overruns"). Such
/// a recording starts at its normal time, so its opening holds the end of the game coverage, and
/// the program itself starts somewhere in the extra minutes: the card search looks that far in.
/// </summary>
/// <remarks>
/// Read from Schedule's own file (<c>data/jellydvr-schedule/overruns.json</c>), so neither plugin
/// needs the other; without Schedule there is no file and nothing changes. A change is matched to
/// a recording by title and by when the recording began (the .nfo's dateadded, which Jellyfin
/// keeps as the item's DateCreated, even after an archive move).
/// </remarks>
public static class OverrunLink
{
    /// <summary>How the recording was lengthened: minutes of padding before the listed start, and the extra minutes.</summary>
    public sealed record Overrun(int PrePaddingMinutes, int ExtraMinutes, string Reason);

    private sealed class Data
    {
        public Change[] Changes { get; set; } = Array.Empty<Change>();
    }

    private sealed class Change
    {
        public DateTime StartUtc { get; set; }

        public string Title { get; set; } = string.Empty;

        public int OriginalPrePadding { get; set; }

        public int ExtraMinutes { get; set; }

        public int LaterStartMinutes { get; set; }

        public int UserExtraMinutes { get; set; }

        public bool UserKept { get; set; }

        public string Reason { get; set; } = string.Empty;
    }

    public static Overrun? For(string dataPath, BaseItem item)
    {
        var file = Path.Combine(dataPath, "jellydvr-schedule", "overruns.json");
        if (item.DateCreated == default || !File.Exists(file))
        {
            return null;
        }

        Data? data;
        try
        {
            data = JsonSerializer.Deserialize<Data>(File.ReadAllText(file));
        }
        catch (Exception)
        {
            return null;
        }

        var began = DateTime.SpecifyKind(item.DateCreated, DateTimeKind.Utc);
        var titles = new[] { (item as Episode)?.SeriesName, item.Name }.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var c = data?.Changes?.FirstOrDefault(c =>
            !c.UserKept
            && c.ExtraMinutes + c.UserExtraMinutes > 0
            && titles.Any(t => string.Equals(t, c.Title, StringComparison.OrdinalIgnoreCase))
            // The recording began at the listed start less its padding (and any later start).
            && Math.Abs((began - c.StartUtc.AddSeconds(-c.OriginalPrePadding).AddMinutes(c.LaterStartMinutes)).TotalMinutes) <= 3);
        return c is null ? null : new Overrun((int)Math.Ceiling(c.OriginalPrePadding / 60.0), c.ExtraMinutes + c.UserExtraMinutes, c.Reason);
    }
}
