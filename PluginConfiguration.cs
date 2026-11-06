using System;
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
    /// Start a live-tuned Comskip process the moment a recording begins, instead of
    /// waiting until it finishes, so the post-recording scan just reads whatever that
    /// process already produced instead of scanning the whole file from scratch. This
    /// needs <c>live_tv=1</c> and <c>output_edl=1</c> set in the configured comskip.ini
    /// (the latter is already required for normal detection to work at all) — without
    /// <c>live_tv=1</c>, Comskip may exit early or misbehave against a file that's still
    /// growing. Trade-off: because the live process is killed (not gracefully stopped)
    /// once the recording settles, its analysis may trail the true end of the recording
    /// by a few seconds, so a break right at the very end could be missed; this is the
    /// same reason live TV playback itself can never show marks in real time (see
    /// <see cref="Jellyfin.Plugin.ComskipSegments.Detection.LiveScanStarter"/>). Doubles
    /// CPU load while a recording is in progress — leave off on modest hardware unless
    /// instant post-recording marks matter enough to pay for it.
    /// </summary>
    public bool EnableLiveScanStart { get; set; }

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
    /// A forced rescan of one recording (dashboard, item menu, settings page) runs Comskip even
    /// when another program's .edl is beside it: that .edl is renamed to "&lt;name&gt;.edl.old", not
    /// deleted. Off: such a rescan uses that .edl, as automatic scans and folder rescans always do.
    /// </summary>
    public bool ManualScanIgnoresOtherEdl { get; set; } = true;

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
    /// "channel unknown", which fails BOTH this allow-list and <see cref="ChannelBlockList"/>
    /// closed — an unresolved channel is never treated as passing either filter, even
    /// though only an empty allow-list literally requires a match; see the block-list's
    /// own remarks for why that matters just as much there). Skipped entirely when both
    /// channel lists are empty, the default. Manual/forced runs ignore this list.
    /// </summary>
    public string ChannelAllowList { get; set; } = string.Empty;

    /// <summary>
    /// Optional DVR channel block-list, one entry per line. Automatic detection never
    /// runs on a recording from a matching channel. Applied after the channel
    /// allow-list. Same matching rules as <see cref="ChannelAllowList"/>.
    ///
    /// Fails CLOSED (blocked), not open, when this list is non-empty but the channel
    /// can't be resolved at all — confirmed live: an HBO recording correctly skipped
    /// right after it finished recording (channel caught by the live poller) later got
    /// auto-scanned anyway by the periodic backstop scan, days after the channel-cache
    /// entry had expired and the by-name guide fallback had gone cold, because an
    /// earlier version treated "channel unknown" as "allow it through" for a block-list
    /// specifically. An unresolved channel is now never treated as passing this filter.
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

    /// <summary>
    /// Repeat-ad check: after Comskip, compare the recording's sound with the other recent
    /// recordings' and add the stretches also heard in another show (ads air over and over, the
    /// show doesn't). Catches ads Comskip misses where breaks have no black frames or logo change
    /// (FAST channels like Crunchyroll's) and short break edges. Off by default.
    /// </summary>
    public bool EnableRepeatAdCheck { get; set; }

    /// <summary>Days a recording's fingerprint is kept for comparing with newer ones (about 115 KB an hour of recording).</summary>
    public int RepeatAdKeepDays { get; set; } = 30;

    /// <summary>Also add the ads a new recording shares with an older one to the older one (only when its breaks are Comskip's).</summary>
    public bool RepeatAdUpdateOlder { get; set; } = true;

    /// <summary>
    /// Learn from hand-edited episodes (needs the repeat-ad check): once a show has two or more
    /// episodes whose breaks were set by hand, its new recordings get the ads you marked there,
    /// lose the stretches you kept as show (openings, endings Comskip calls ads), have their show
    /// time checked against yours, and get the padding you always mark. Off by default.
    /// </summary>
    public bool RepeatAdLearnFromEdits { get; set; }

    /// <summary>
    /// With <see cref="RepeatAdLearnFromEdits"/>: when three or more of a show's hand-edited
    /// episodes of about the same length have the same number of breaks, new scans of it get
    /// that many breaks, each where and as long as in those episodes, its edges on Comskip's cut
    /// points (on by default).
    /// </summary>
    public bool RepeatAdUseBreakPattern { get; set; } = true;

    /// <summary>
    /// When a recording opens with 20 s or more of silence that ends in a fade to black (Pluto's
    /// lead-in), mark from the start to that black as a break (on by default). See <see cref="SilentOpening"/>.
    /// </summary>
    public bool MarkSilentOpening { get; set; } = true;

    /// <summary>
    /// Learn the picture a show's edited episodes end on before their final break runs to the
    /// end of the file, and mark from there to the end in new scans (on by default). See <see cref="RepeatAds.EndCard"/>.
    /// </summary>
    public bool LearnEndCard { get; set; } = true;

    /// <summary>
    /// Master switch for the audio-format trim feature. Some broadcast recordings open
    /// (and/or close) with a different audio channel layout than the movie itself — e.g. a
    /// network preview/bumper block in 2.0 stereo before a movie broadcast in 5.1, or a few
    /// minutes of the tail end of the PREVIOUS program in 5.1 caught by DVR pre-roll padding
    /// before the actual 2.0 bumper starts. Because ffprobe (and so Jellyfin) determines a
    /// file's audio channel count by probing from the start, that leading junk makes the
    /// whole recording get read as 2.0 even though the movie itself is 5.1. When on, this
    /// scans the primary audio stream's channel layout across the whole file and can cut the
    /// non-movie audio format off each end. Off by default: unlike commercial-skip segments
    /// (which only mark playback ranges), this can rewrite the actual recording file.
    /// </summary>
    public bool EnableAudioFormatTrim { get; set; }

    /// <summary>
    /// When <see cref="EnableAudioFormatTrim"/> is also on, apply the trim automatically as
    /// part of the normal post-recording pipeline (same trigger as Comskip). When off (the
    /// default), detection/trimming only ever happens from an explicit per-item action —
    /// either the "Fix opening/closing audio format" item-details menu entry, or the config
    /// page's manual trim button — so you can build confidence in the detector against your
    /// own channels before letting it touch files unattended.
    /// </summary>
    public bool AudioTrimAutomaticApply { get; set; }

    /// <summary>
    /// True: keep the untouched original for good, hidden beside the corrected file
    /// (<c>.&lt;name&gt;.original&lt;ext&gt;</c>), so a trim or cut can always be undone.
    /// False (default since 1.3.1.0; a cut would otherwise take more space than it saves):
    /// keep it for <see cref="TrimUndoDays"/>, then delete it.
    /// </summary>
    public bool AudioTrimKeepOriginal { get; set; }

    /// <summary>
    /// When <see cref="AudioTrimKeepOriginal"/> is off: keep the original this many days anyway,
    /// so "Undo trim" can put it back after a mistaken or curious press, then delete it. 0 deletes
    /// it as soon as the trimmed file is in place. Applies to every trim (program start, audio).
    /// </summary>
    public int TrimUndoDays { get; set; } = 7;

    /// <summary>
    /// True (default): a kept original whose recording is gone (deleted, or moved without it)
    /// can't be undone to, so it's deleted a day after the daily look finds it that way.
    /// </summary>
    public bool DeleteOrphanedOriginals { get; set; } = true;

    /// <summary>
    /// "Cut commercials out": a cut copies the video as it is, so each kept part has to start on a
    /// keyframe. <see cref="CutKeepAd"/> (default) starts it on the keyframe at or before the end of
    /// the break, keeping a second or so of the ad; <see cref="CutKeepShow"/> on the one after,
    /// losing a second or so of the show instead.
    /// </summary>
    public string CutRounding { get; set; } = CutKeepAd;

    /// <summary>
    /// Users (Jellyfin user ids) besides administrators who may edit recordings: program start,
    /// remembered cards, trims and their undo, the audio fix, and scanning for or removing
    /// commercials. Administrators always may; everyone else sees no edit buttons.
    /// </summary>
    public string[] RecordingEditorUserIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The contiguous audio-format run that contains the temporal midpoint of the recording
    /// is treated as "the movie" (this correctly survives DVR pre-roll padding starting
    /// mid-way through the tail of a previous, differently-formatted program — see README).
    /// Everything outside that run is a trim candidate, but only if that core run covers at
    /// least this fraction of the total recording; otherwise the file is treated as
    /// ambiguous and left untouched rather than guessing.
    /// </summary>
    public double AudioTrimMinCoreFraction { get; set; } = 0.7;

    /// <summary>
    /// Leading/trailing non-movie audio shorter than this is left alone — not worth the risk
    /// of a re-mux for a couple of seconds of junk, and a cheap guard against a spurious
    /// one-frame blip being mistaken for a real format boundary.
    /// </summary>
    public double AudioTrimMinJunkSeconds { get; set; } = 5.0;

    /// <summary>
    /// Safety margin subtracted from each measured cut point (i.e. less is trimmed than was
    /// actually measured), so a slightly imprecise boundary — or ffmpeg's keyframe-rounded
    /// seek — never has a chance to clip real movie content. The trade-off is a few extra
    /// seconds of junk audio left in place.
    /// </summary>
    public double AudioTrimPaddingSeconds { get; set; } = 1.5;

    /// <summary>
    /// Badge on cards and list rows (web script, Wholphin): "icon" = scissors with the total
    /// minutes of commercials, "letters" = a colored CS tag.
    /// </summary>
    public string BadgeCardStyle { get; set; } = "icon";

    /// <summary>Badge in the Live TV guide: "letters" (a colored CS tag, compact next to the
    /// Episode History marks) or "icon".</summary>
    public string BadgeGuideStyle { get; set; } = "letters";

    /// <summary>How a "scanning" badge moves: "pulse" (slow fade), "spin" or "none" (color only).</summary>
    public string BadgeScanningMotion { get; set; } = "pulse";

    /// <summary>Show a done/total summary badge on show and season cards.</summary>
    public bool BadgeShowSummary { get; set; } = true;

    /// <summary>
    /// Put the web overlay (seek-bar marks, item menu) back into jellyfin-web's index.html on
    /// startup when it's missing, e.g. after a jellyfin-web upgrade; only where Jellyfin may write the file.
    /// </summary>
    public bool AutoInstallWebOverlay { get; set; } = true;

    public const string CutKeepAd = "keep-ad";
    public const string CutKeepShow = "keep-show";

    public const string ProgramStartMarker = "marker";
    public const string ProgramStartTrim = "trim";

    /// <summary>
    /// What happens to the promos before a program's start once it's known (marked in a
    /// player, or found by a remembered card): "marker" (default) adds a Preview segment from
    /// 0 to the start, so players offer Skip and the file is untouched; "trim" cuts them off
    /// the file with the same safe trim as the audio-format fix.
    /// </summary>
    public string ProgramStartAction { get; set; } = ProgramStartMarker;

    /// <summary>
    /// After each recording, look for a remembered program-start card (a channel's rating
    /// card, say) in its opening minutes. Does nothing until a card has been remembered.
    /// </summary>
    public bool ProgramStartAutomatic { get; set; } = true;

    /// <summary>How far into a recording to look for a program-start card (minutes).</summary>
    public int ProgramStartScanMinutes { get; set; } = 10;

    /// <summary>
    /// How alike a frame must be to a remembered card to count (0–1, normalized correlation
    /// of 96×54 greyscale frames). On Starz recordings the cards scored 0.83–0.97 against
    /// cards from other films and the closest other frame 0.53, so 0.70 sits in the gap.
    /// </summary>
    public double ProgramStartMatchThreshold { get; set; } = 0.70;
}
