using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.ComskipSegments;

/// <summary>
/// JellyDVR Live's live TV buffers: a recording made from tune-in so live TV can be paused and
/// rewound, deleted a few hours after the last viewer unless someone keeps it. An unkept one
/// is no recording to scan or edit (often a clip of a couple of minutes), so automatic scans
/// pass it by and the Recordings list leaves it out. Read from JellyDVR Live's own list
/// (&lt;data&gt;/jellydvr-live/buffers.json); without that plugin nothing is a buffer.
/// </summary>
public static class LiveBuffers
{
    private static readonly object Gate = new();
    private static DateTime _readStamp;
    private static HashSet<string> _unkept = new(StringComparer.Ordinal);
    private static HashSet<string> _unkeptTimers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The server's data folder (set by <see cref="Plugin"/>).</summary>
    public static string? DataPath { get; set; }

    /// <summary>True when <paramref name="path"/> is a buffer JellyDVR Live owns and nobody has kept.</summary>
    public static bool IsUnkept(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        Refresh();
        lock (Gate)
        {
            return _unkept.Contains(path);
        }
    }

    /// <summary>
    /// True when one of <paramref name="timerIds"/> is an unkept buffer's timer. A buffer's file
    /// only lands in JellyDVR Live's list on its next pass (up to 20 s after the recording
    /// starts); its timer is there from tune-in on.
    /// </summary>
    public static bool IsUnkeptTimer(IEnumerable<string>? timerIds)
    {
        if (timerIds is null)
        {
            return false;
        }

        Refresh();
        lock (Gate)
        {
            return timerIds.Any(id => _unkeptTimers.Contains(id.Replace("-", string.Empty, StringComparison.Ordinal)));
        }
    }

    private static void Refresh()
    {
        if (DataPath is null)
        {
            return;
        }

        var file = Path.Combine(DataPath, "jellydvr-live", "buffers.json");
        lock (Gate)
        {
            try
            {
                var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
                if (stamp != _readStamp)
                {
                    (_unkept, _unkeptTimers) = stamp == DateTime.MinValue
                        ? (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                        : Read(file);
                    _readStamp = stamp;
                }
            }
            catch (Exception)
            {
                // Being rewritten as we read: the last list read stands until the next look.
            }
        }
    }

    private static (HashSet<string> Paths, HashSet<string> Timers) Read(string file)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var timers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        IEnumerable<JsonElement> buffers = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.EnumerateArray(),
            JsonValueKind.Object when doc.RootElement.TryGetProperty("Buffers", out var b) && b.ValueKind == JsonValueKind.Array => b.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>()
        };
        foreach (var b in buffers)
        {
            var owned = b.TryGetProperty("Owned", out var o) && o.ValueKind == JsonValueKind.True;
            var kept = b.TryGetProperty("Kept", out var k) && k.ValueKind == JsonValueKind.True;
            if (!owned || kept)
            {
                continue;
            }

            if (b.TryGetProperty("TimerId", out var t) && t.GetString() is { Length: > 0 } timer)
            {
                timers.Add(timer.Replace("-", string.Empty, StringComparison.Ordinal));
            }

            if (b.TryGetProperty("Path", out var p) && p.GetString() is { Length: > 0 } path)
            {
                set.Add(path);
            }

            if (b.TryGetProperty("ExtraPaths", out var extra) && extra.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in extra.EnumerateArray())
                {
                    if (x.GetString() is { Length: > 0 } more)
                    {
                        set.Add(more);
                    }
                }
            }
        }

        return (set, timers);
    }
}
