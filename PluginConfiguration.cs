using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ComskipSegments.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Path fields are trimmed on set: the config page tends to leave a stray leading
    // space on pasted values, and " /abs/path" is not treated as rooted on Linux, so
    // it silently becomes a relative path and Comskip writes its output nowhere useful.
    private string _comskipPath = string.Empty;
    private string _comskipIniPath = string.Empty;
    private string _workDirectory = string.Empty;
    private string _recordingsPath = "/var/lib/jellyfin/data/livetv/recordings";

    /// <summary>Absolute path to the comskip binary (executable by the Jellyfin service account).</summary>
    public string ComskipPath
    {
        get => _comskipPath;
        set => _comskipPath = value?.Trim() ?? string.Empty;
    }

    /// <summary>Absolute path to comskip.ini (readable by the Jellyfin service account).</summary>
    public string ComskipIniPath
    {
        get => _comskipIniPath;
        set => _comskipIniPath = value?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Work directory for Comskip output (.edl/.txt/.log). MUST be writable by the
    /// Jellyfin service account. This is why detection can read the read-only
    /// recordings folder but never tries to write there.
    /// </summary>
    public string WorkDirectory
    {
        get => _workDirectory;
        set => _workDirectory = value?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Absolute path to the DVR recordings root. New items whose path is under this
    /// folder are auto-detected as recordings when ProcessRecordingsAutomatically is on.
    /// </summary>
    public string RecordingsPath
    {
        get => _recordingsPath;
        set => _recordingsPath = value?.Trim() ?? string.Empty;
    }

    /// <summary>Automatically run detection when a new recording appears.</summary>
    public bool ProcessRecordingsAutomatically { get; set; } = true;

    /// <summary>
    /// How often, in hours, the "Scan library for commercials (Comskip)" task runs on
    /// its own as a safety net for recordings the live-event watcher misses (Jellyfin
    /// raises ItemUpdated rather than ItemAdded when a recording finishes over an
    /// episode that already existed). 0 disables the automatic run — you can still
    /// start the task by hand from Dashboard -> Scheduled Tasks.
    ///
    /// NOTE: Jellyfin only reads this default when the task is first registered. If you
    /// change it later, also hit "reset to default triggers" (or edit the trigger) for
    /// that task in the dashboard, or restart the server, for the new interval to take.
    /// </summary>
    public double AutoScanIntervalHours { get; set; } = 6.0;

    /// <summary>
    /// Master switch for the periodic backstop sweep (the automatic interval run of the
    /// "Scan library for commercials (Comskip)" task). Off = no automatic sweeps; you
    /// can still run that task by hand from Dashboard -> Scheduled Tasks.
    /// </summary>
    public bool EnableBackstopScan { get; set; } = true;

    /// <summary>
    /// Whether the media-segment provider auto-queues an unprocessed item when
    /// Jellyfin's own Media Segment Scan asks it for segments. Off = detection only
    /// ever starts from the recording watcher, the backstop scan, or a manual run.
    /// </summary>
    public bool EnableMediaSegmentScanEnqueue { get; set; } = true;

    /// <summary>
    /// Optional show/folder allow-list, one entry per line. When non-empty, automatic
    /// detection runs ONLY on items whose series name — or any folder in their path, or
    /// the file name — matches an entry. "*" and "?" wildcards are supported; entries
    /// without a wildcard match case-insensitively and in full. Manual/forced runs
    /// ignore this list.
    /// </summary>
    public string SeriesAllowList { get; set; } = string.Empty;

    /// <summary>
    /// Optional show/folder block-list, one entry per line. Automatic detection never
    /// runs on a matching item. Applied after the allow-list. Same matching rules as
    /// <see cref="SeriesAllowList"/>.
    /// </summary>
    public string SeriesBlockList { get; set; } = string.Empty;

    /// <summary>
    /// Optional DVR channel allow-list, one entry per line. When non-empty, automatic
    /// detection runs ONLY on shows whose channel name or channel number matches an
    /// entry. Same wildcard/matching rules as <see cref="SeriesAllowList"/>.
    ///
    /// Channel resolution has two layers, because neither the finished recording nor
    /// Live TV's own recording list carries a channel once the recording is done
    /// (confirmed against a live server). Primarily,
    /// <see cref="Jellyfin.Plugin.ComskipSegments.Detection.LiveRecordingChannelPoller"/>
    /// polls Jellyfin every 20s for whatever is currently recording and caches its
    /// channel by path — the only way to actually catch it, since Jellyfin doesn't add a
    /// DVR recording to the library (and so never fires the events
    /// <see cref="Jellyfin.Plugin.ComskipSegments.Events.RecordingWatcher"/> listens for)
    /// until AFTER it finishes, by which point the channel is already gone; reacting to
    /// those events instead of polling was tried and confirmed too late. For anything
    /// the poller never caught while it was recording (this feature was off, or the
    /// plugin/server was down at the time), it falls back to a by-name lookup against
    /// Jellyfin's recently-aired program guide (see
    /// <see cref="Jellyfin.Plugin.ComskipSegments.Detection.ChannelResolver"/>), which
    /// works in a normal broadcast lineup where a given show always airs on the same
    /// channel but returns nothing for a show with no recent guide coverage (treated as
    /// "channel unknown", which fails an allow-list closed). Skipped entirely when both
    /// channel lists are empty, the default. Manual/forced runs ignore this list.
    /// </summary>
    public string ChannelAllowList { get; set; } = string.Empty;

    /// <summary>
    /// Optional DVR channel block-list, one entry per line. Automatic detection never
    /// runs on a recording from a matching channel. Applied after the channel
    /// allow-list. Same matching rules as <see cref="ChannelAllowList"/>.
    /// </summary>
    public string ChannelBlockList { get; set; } = string.Empty;

    /// <summary>
    /// Extra absolute folder paths to include in the on-demand "Scan library for
    /// commercials" scheduled task, one per line. Leave empty to scan recordings only.
    /// Keep this narrow — only point it at content that actually came off broadcast.
    /// </summary>
    public string ScanPaths { get; set; } = string.Empty;

    /// <summary>Max Comskip jobs running at once. Start at 1; raise only if you have CPU/GPU headroom.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>Kill any Comskip job that runs longer than this (guards against wedged/corrupt files).</summary>
    public int PerFileTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Seconds trimmed from each end of every detected break so the skip doesn't
    /// clip the last frame of the show. 0 = use Comskip's boundaries exactly.
    /// </summary>
    public double PaddingSeconds { get; set; } = 0.0;

    /// <summary>Delete Comskip's .txt/.log clutter after a successful parse, keeping only nothing behind.</summary>
    public bool CleanupWorkFiles { get; set; } = true;

    /// <summary>
    /// Also copy the parsed .edl file next to the recording itself (same folder, same
    /// base filename), for other software pointed at the recordings folder that expects
    /// a sibling .edl (comchap/comcut-style tooling, some players/frontends). Independent
    /// of <see cref="CleanupWorkFiles"/> — that setting only controls whether the
    /// original copy in <see cref="WorkDirectory"/> is kept or deleted; this is a second,
    /// separate copy placed in the recordings folder. Off by default since it writes
    /// into a folder this plugin doesn't otherwise touch.
    /// </summary>
    public bool PlaceEdlBesideRecording { get; set; }
}
