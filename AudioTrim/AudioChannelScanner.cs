using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.AudioTrim;

/// <summary>A contiguous run of frames sharing the same audio channel count.</summary>
public readonly record struct ChannelRun(int Channels, double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>
/// The result of scanning one recording's primary audio stream for channel-format
/// boundaries, and the trim (if any) that follows from it.
/// </summary>
public sealed record TrimPlan(
    bool NeedsTrim,
    double LeadTrimSeconds,
    double TrailTrimSeconds,
    double TotalDurationSeconds,
    double CoreDurationSeconds,
    int CoreChannels,
    string Reason,
    double? CoreStartSeconds = null)
{
    public static TrimPlan NotNeeded(double totalDuration, string reason) =>
        new(false, 0, 0, totalDuration, totalDuration, 0, reason);
}

/// <summary>
/// Detects whether a recording opens and/or closes with a different audio channel
/// layout than the movie itself (see <see cref="Configuration.PluginConfiguration.EnableAudioFormatTrim"/>
/// for why this happens on broadcast DVR captures).
///
/// Method: decode the primary audio stream's per-frame channel count across the WHOLE
/// file via ffprobe (cheap — a couple of seconds of AC3/E-AC3 decode per hour of
/// recording, confirmed against a real 112-minute capture) and group it into contiguous
/// runs. The run that contains the recording's temporal MIDPOINT — not the longest run,
/// and not simply "whatever starts at t=0" — is treated as the movie. Anchoring on the
/// midpoint is what correctly survives DVR pre-roll padding: a recording that starts a
/// minute early can begin mid-way through the tail of a DIFFERENTLY-formatted previous
/// program, so the very first run in the file is not reliably "junk" vs. "movie", but
/// whatever is playing at the recording's own midpoint always is the movie for any
/// recording where the padding is a small fraction of the runtime.
/// </summary>
public sealed class AudioChannelScanner
{
    private readonly ILogger<AudioChannelScanner> _logger;

    public AudioChannelScanner(ILogger<AudioChannelScanner> logger) => _logger = logger;

    public async Task<TrimPlan> ScanAsync(
        string ffprobePath,
        string mediaPath,
        double minCoreFraction,
        double minJunkSeconds,
        double paddingSeconds,
        CancellationToken cancellationToken)
    {
        var (startTime, totalDuration) = await ProbeFormatTimes(ffprobePath, mediaPath, cancellationToken).ConfigureAwait(false);
        if (totalDuration <= 0)
        {
            return TrimPlan.NotNeeded(0, "could not determine recording duration");
        }

        var runs = await ScanChannelRuns(ffprobePath, mediaPath, startTime, totalDuration, cancellationToken).ConfigureAwait(false);
        if (runs.Count == 0)
        {
            return TrimPlan.NotNeeded(totalDuration, "no audio frames decoded (no audio stream, or ffprobe failed)");
        }

        if (runs.Count == 1)
        {
            return TrimPlan.NotNeeded(totalDuration, "audio channel format is uniform across the whole recording");
        }

        var midpoint = totalDuration / 2.0;
        var coreIndex = runs.FindIndex(r => r.StartSeconds <= midpoint && midpoint < r.EndSeconds);
        var core = coreIndex >= 0 ? runs[coreIndex] : runs[^1];

        var coreFraction = core.DurationSeconds / totalDuration;
        if (coreFraction < minCoreFraction)
        {
            return TrimPlan.NotNeeded(
                totalDuration,
                $"ambiguous: the run at the recording's midpoint only covers {coreFraction:P0} of its " +
                $"duration (need >= {minCoreFraction:P0}) — leaving the file untouched rather than guessing");
        }

        var rawLead = core.StartSeconds;
        var rawTrail = totalDuration - core.EndSeconds;

        var lead = Math.Max(0, rawLead - paddingSeconds);
        var trail = Math.Max(0, rawTrail - paddingSeconds);

        if (lead < minJunkSeconds)
        {
            lead = 0;
        }

        if (trail < minJunkSeconds)
        {
            trail = 0;
        }

        if (lead <= 0 && trail <= 0)
        {
            return TrimPlan.NotNeeded(
                totalDuration,
                rawLead > 0 || rawTrail > 0
                    ? $"leading/trailing non-movie audio ({rawLead:F1}s / {rawTrail:F1}s) is too short to bother trimming"
                    : "no channel-format change detected");
        }

        return new TrimPlan(
            true,
            lead,
            trail,
            totalDuration,
            core.DurationSeconds,
            core.Channels,
            $"core {core.Channels}ch run covers {coreFraction:P0} of the recording; trimming {lead:F1}s lead / {trail:F1}s trail",
            core.StartSeconds);
    }

    /// <summary>
    /// Streams <c>frame=pts_time,channels</c> for the primary audio stream and folds it
    /// into contiguous runs without ever buffering the whole (potentially 200k-line)
    /// output at once.
    ///
    /// <paramref name="startTime"/> is subtracted from every frame's <c>pts_time</c>
    /// before it's used. A raw MPEG-TS capture (as opposed to a remuxed .mkv/.mp4) keeps
    /// the broadcast's own PCR-derived timestamps, which do NOT start at zero — confirmed
    /// against a real recording, where every frame's <c>pts_time</c> was offset by the
    /// container's <c>format.start_time</c> (~53595s on a file whose own duration was
    /// ~6708s). Without this correction every run's Start/End lands far outside
    /// [0, totalDuration], the midpoint search in <see cref="ScanAsync"/> never matches
    /// any run, and it silently falls back to the last run in the list every time.
    /// </summary>
    private async Task<List<ChannelRun>> ScanChannelRuns(
        string ffprobePath, string mediaPath, double startTime, double totalDuration, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("a:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("frame=pts_time,channels");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add(mediaPath);

        var runs = new List<ChannelRun>();
        int? runChannels = null;
        double runStart = 0;
        double lastTime = 0;

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain stderr concurrently with stdout so a chatty ffprobe run can never fill
        // that pipe and deadlock this loop.
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            var parts = line.Split(',', StringSplitOptions.None);
            if (parts.Length < 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rawTime)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channels))
            {
                continue; // e.g. a frame with no pts_time
            }

            var time = rawTime - startTime;

            if (runChannels is null)
            {
                runChannels = channels;
                runStart = time;
            }
            else if (channels != runChannels)
            {
                runs.Add(new ChannelRun(runChannels.Value, runStart, time));
                runChannels = channels;
                runStart = time;
            }

            lastTime = time;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (runChannels is not null)
        {
            // Close the final run at the true end of the recording, not the last frame's
            // start time, so it isn't shorted by one frame.
            runs.Add(new ChannelRun(runChannels.Value, runStart, Math.Max(lastTime, totalDuration)));
        }

        if (runs.Count == 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            _logger.LogDebug("ffprobe audio channel scan produced no frames for {Path}: {Stderr}", mediaPath, stderr.Trim());
        }

        return runs;
    }

    /// <summary>
    /// Returns (start_time, duration) in seconds. <c>start_time</c> is 0 for an ordinary
    /// file but non-zero for a raw MPEG-TS DVR capture, whose frame timestamps carry the
    /// broadcast's own PCR-derived clock rather than resetting at the start of the
    /// recording — see the remarks on <see cref="ScanChannelRuns"/>.
    /// </summary>
    internal static async Task<(double StartTime, double Duration)> ProbeFormatTimes(
        string ffprobePath, string mediaPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=start_time,duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add(mediaPath);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var fields = stdout.Trim().Split(',', StringSplitOptions.None);
        var startTime = fields.Length > 0 && double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var duration = fields.Length > 1 && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        return (startTime, duration);
    }

    /// <summary>
    /// The <c>-map</c> arguments for a stream copy: the streams the recording declares, by their
    /// MPEG-TS PID (<c>-map 0:#0x100?</c>), so a stray stream that turns up only partway through is
    /// left out. Some recordings carry an undeclared ID3 PID that ffmpeg takes for an empty MP3 track
    /// ("mp3, 0 channels"): with <c>-map 0</c>, a part that starts where it appears fails with
    /// "sample rate not set". Files without PIDs (not MPEG-TS), or a failed probe, copy everything.
    /// </summary>
    internal static async Task<string[]> CopyMaps(string ffprobePath, string mediaPath, CancellationToken cancellationToken)
    {
        var all = new[] { "-map", "0" };
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "stream=index,id", "-of", "csv=p=0", mediaPath })
            {
                psi.ArgumentList.Add(a);
            }

            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // index,id lines; ffprobe lists them once per program and once on their own.
            var pids = new SortedDictionary<int, string>();
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var f = line.Split(',');
                if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    continue;
                }

                if (f.Length < 2 || !f[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return all;   // a stream without a PID: not MPEG-TS
                }

                pids[index] = f[1];
            }

            if (process.ExitCode != 0 || pids.Count == 0)
            {
                return all;
            }

            // The trailing ? lets a part that lacks one of them still copy.
            return pids.Values.SelectMany(pid => new[] { "-map", $"0:#{pid}?" }).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return all;   // probing is only a refinement: copy everything, as before
        }
    }
}
