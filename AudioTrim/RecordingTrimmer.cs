using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Editing;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.AudioTrim;

public sealed record TrimOutcome(bool Success, string? BackupPath, string Message);

/// <summary>
/// Runs the actual ffmpeg cut for a <see cref="TrimPlan"/> and swaps the result into
/// place at the recording's original path, so the library item's path never changes.
///
/// Writes ffmpeg's output to a hidden file beside the recording first (so a cut needs room on
/// the recording's own drive, never on the server's system drive, and ends with a rename), and
/// only replaces the recording once that output is verified to exist and be a sane size, so a
/// crash or a full disk mid-run can never leave the original recording missing. Where the
/// recording's folder can't be written, falls back to <paramref name="workDir"/>.
/// </summary>
public sealed class RecordingTrimmer
{
    private readonly ILogger<RecordingTrimmer> _logger;
    private readonly TrimBackups _backups;
    private readonly IMediaEncoder _encoder;

    public RecordingTrimmer(ILogger<RecordingTrimmer> logger, TrimBackups backups, IMediaEncoder encoder)
    {
        _logger = logger;
        _backups = backups;
        _encoder = encoder;
    }

    public async Task<TrimOutcome> ApplyAsync(
        string ffmpegPath,
        string workDir,
        string recordingPath,
        TrimPlan plan,
        bool keepOriginal,
        CancellationToken cancellationToken,
        CutJob? job = null)
    {
        if (!plan.NeedsTrim)
        {
            return new TrimOutcome(false, null, "no trim needed");
        }

        plan = await OnKeyframeAsync(recordingPath, plan, cancellationToken).ConfigureAwait(false);
        var ext = Path.GetExtension(recordingPath);
        var keptBytes = (long)(SafeLength(recordingPath) * (plan.TotalDurationSeconds - plan.LeadTrimSeconds - plan.TrailTrimSeconds) / plan.TotalDurationSeconds);
        var tempOutput = TempBeside(recordingPath, ext) ?? Path.Combine(workDir, $"audiotrim-{Guid.NewGuid():N}{ext}");
        Directory.CreateDirectory(Path.GetDirectoryName(tempOutput)!);

        // Refuse up front rather than fill the drive: a full system drive breaks Jellyfin itself.
        var free = FreeBytes(tempOutput);
        if (free >= 0 && free < keptBytes + MinFreeAfterCut)
        {
            _logger.LogWarning(
                "Not trimming {Path}: the cut needs about {Need:N0} MB and {Folder} has {Free:N0} MB free",
                recordingPath, (keptBytes + MinFreeAfterCut) / 1048576, Path.GetDirectoryName(tempOutput), free / 1048576);
            return new TrimOutcome(false, null, $"not enough free space for the cut ({free / 1073741824.0:F1} GB free, needs {(keptBytes + MinFreeAfterCut) / 1073741824.0:F1} GB)");
        }

        try
        {
            if (job is not null)
            {
                job.TempPath = tempOutput;
                job.Bytes = keptBytes;
                job.Phase = CutJob.PhaseCutting;
            }

            var runResult = await RunFfmpeg(ffmpegPath, recordingPath, tempOutput, plan, cancellationToken).ConfigureAwait(false);
            if (!runResult.Success)
            {
                return new TrimOutcome(false, null, runResult.Message);
            }

            var originalLength = SafeLength(recordingPath);
            var trimmedLength = SafeLength(tempOutput);
            var expectedFraction = (plan.TotalDurationSeconds - plan.LeadTrimSeconds - plan.TrailTrimSeconds) / plan.TotalDurationSeconds;

            // Stream copy, so the trimmed file's size should track the fraction of the
            // recording that was kept, within a generous margin (variable bitrate,
            // container overhead). Well outside that margin means something went wrong
            // (e.g. ffmpeg silently produced a near-empty file) — refuse to swap it in.
            if (trimmedLength <= 0 || trimmedLength < originalLength * expectedFraction * 0.5)
            {
                TryDelete(tempOutput);
                return new TrimOutcome(
                    false, null,
                    $"trimmed output failed a sanity check (expected ~{expectedFraction:P0} of {originalLength} bytes, got {trimmedLength}); left the original untouched");
            }

            // From here the original is replaced: not stoppable any more (a half-done swap
            // could leave no playable file).
            if (job is not null)
            {
                job.Bytes = trimmedLength;
                job.FinalPath = recordingPath;
                job.Phase = CutJob.PhaseFinishing;
            }

            return SwapIntoPlace(recordingPath, tempOutput, keepOriginal);
        }
        finally
        {
            TryDelete(tempOutput);
        }
    }

    /// <summary>
    /// Cuts the commercials out: copies each kept part (each starting on a keyframe, see
    /// <see cref="Keyframes"/>) to a hidden file beside the recording, joins them, checks the
    /// joined file is as long as the parts together, and swaps it in like a trim (the original kept
    /// for Undo the same way). The original is untouched until that swap.
    /// A copied part's sound starts a little before its keyframe (ffmpeg keeps the audio from where
    /// it lands in the file, a fraction of a second earlier) and its end runs a few frames on, so
    /// what was really kept is measured from each part and returned: resume positions move by that.
    /// </summary>
    /// <summary>How a commercial cut ended, and what it really kept (see <see cref="ApplyPartsAsync"/>).</summary>
    public sealed record PartsOutcome(TrimOutcome Outcome, IReadOnlyList<KeptPart> Kept);

    public async Task<PartsOutcome> ApplyPartsAsync(
        string ffmpegPath,
        string workDir,
        string recordingPath,
        IReadOnlyList<KeptPart> parts,
        double totalSeconds,
        bool keepOriginal,
        CancellationToken cancellationToken,
        CutJob? job = null)
    {
        if (parts.Count == 0 || totalSeconds <= 0)
        {
            return Failed("nothing would be left of the recording");
        }

        var ext = Path.GetExtension(recordingPath);
        var keptSeconds = parts.Sum(p => p.Length);
        var keptBytes = (long)(SafeLength(recordingPath) * Math.Min(1, keptSeconds / totalSeconds));
        var output = TempBeside(recordingPath, ext) ?? Path.Combine(workDir, $"cut-{Guid.NewGuid():N}{ext}");
        var folder = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(folder);

        // The parts and the joined copy are on the drive at once.
        var free = FreeBytes(output);
        if (free >= 0 && free < (2 * keptBytes) + MinFreeAfterCut)
        {
            return Failed($"not enough free space for the cut ({free / 1073741824.0:F1} GB free, needs {((2 * keptBytes) + MinFreeAfterCut) / 1073741824.0:F1} GB)");
        }

        var stem = Path.GetFileNameWithoutExtension(output);
        var partPaths = parts.Select((_, i) => Path.Combine(folder, $"{stem}-part{i + 1}{ext}")).ToList();
        var listPath = Path.Combine(folder, $"{stem}-parts.txt");
        var kept = new List<KeptPart>();
        var maps = await AudioChannelScanner.CopyMaps(_encoder.ProbePath, recordingPath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (job is not null)
            {
                job.Bytes = 2 * keptBytes;
                job.Phase = CutJob.PhaseCutting;
            }

            for (var i = 0; i < parts.Count; i++)
            {
                if (job is not null)
                {
                    job.Detail = $"Copying part {i + 1} of {parts.Count}";
                    job.BytesBefore = partPaths.Take(i).Sum(SafeLength);
                    job.TempPath = partPaths[i];
                }

                var part = parts[i];
                var args = new List<string> { "-y", "-nostdin" };
                if (part.Start > 0)
                {
                    // Just before the part's keyframe: ffmpeg starts the copy on the first keyframe from here.
                    args.AddRange(new[] { "-ss", Math.Max(0, part.Start - Keyframes.Margin).ToString("F3", CultureInfo.InvariantCulture) });
                }

                args.AddRange(new[] { "-i", recordingPath });
                if (part.End < totalSeconds - 0.5)
                {
                    args.AddRange(new[] { "-t", (part.End - Math.Max(0, part.Start - Keyframes.Margin)).ToString("F3", CultureInfo.InvariantCulture) });
                }

                // -f mpegts: see RunFfmpeg (a .mpg name would otherwise give MPEG-PS).
                args.AddRange(maps);
                args.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero", "-f", "mpegts", partPaths[i] });
                var copied = await RunProcess(Psi(ffmpegPath, args), recordingPath, partPaths[i], cancellationToken).ConfigureAwait(false);
                if (!copied.Success)
                {
                    return Failed(copied.Message);
                }

                // What the part really holds: its sound may start a little before its keyframe, and
                // the copy runs to the end of the frame or audio packet at its end. The join lays the
                // parts end to end by these lengths.
                var start = part.Start > 0 ? Math.Max(0, part.Start - await AudioLeadAsync(partPaths[i], cancellationToken).ConfigureAwait(false)) : 0;
                var (_, length) = await AudioChannelScanner.ProbeFormatTimes(_encoder.ProbePath, partPaths[i], cancellationToken).ConfigureAwait(false);
                kept.Add(new KeptPart(start, length > 0 ? start + length : part.End));
            }

            if (job is not null)
            {
                job.Detail = "Joining the parts";
                job.BytesBefore = partPaths.Sum(SafeLength);
                job.TempPath = output;
            }

            await File.WriteAllLinesAsync(listPath, partPaths.Select(p => "file '" + p.Replace("'", "'\\''", StringComparison.Ordinal) + "'"), cancellationToken).ConfigureAwait(false);
            var joined = await RunProcess(
                Psi(ffmpegPath, new[] { "-y", "-nostdin", "-f", "concat", "-safe", "0", "-i", listPath, "-map", "0", "-c", "copy", "-f", "mpegts", output }),
                recordingPath, output, cancellationToken).ConfigureAwait(false);
            if (!joined.Success)
            {
                return Failed(joined.Message);
            }

            foreach (var p in partPaths)
            {
                TryDelete(p);
            }

            if (job is not null)
            {
                job.Detail = "Checking the new file";
            }

            // The whole of it has to be there: as long as the parts together, give or take a frame or two a join.
            var (_, joinedSeconds) = await AudioChannelScanner.ProbeFormatTimes(_encoder.ProbePath, output, cancellationToken).ConfigureAwait(false);
            var keptReally = kept.Sum(p => p.Length);
            if (joinedSeconds <= 0 || Math.Abs(joinedSeconds - keptReally) > Math.Max(2, parts.Count * 0.5))
            {
                _logger.LogWarning("Commercial cut of {Path}: the joined file is {Joined:F1}s, the parts {Kept:F1}s; leaving the recording as it was", recordingPath, joinedSeconds, keptReally);
                return Failed($"the cut file came out {Clock(joinedSeconds)} long instead of {Clock(keptReally)}; the recording was left as it was");
            }

            if (job is not null)
            {
                job.Detail = "Putting it in place";
                job.Bytes = SafeLength(output);
                job.BytesBefore = 0;
                job.FinalPath = recordingPath;
                job.Phase = CutJob.PhaseFinishing;
            }

            return new PartsOutcome(SwapIntoPlace(recordingPath, output, keepOriginal), kept);
        }
        finally
        {
            foreach (var p in partPaths)
            {
                TryDelete(p);
            }

            TryDelete(listPath);
            TryDelete(output);
        }
    }

    private static PartsOutcome Failed(string message) => new(new TrimOutcome(false, null, message), Array.Empty<KeptPart>());

    /// <summary>How long a copied part's sound runs before its first picture, in seconds (0 when it can't be read).</summary>
    private async Task<double> AudioLeadAsync(string partPath, CancellationToken ct)
    {
        try
        {
            // Keyframe times are from the start of the file, which is where its sound starts.
            var keys = await Keyframes.AroundAsync(_encoder.ProbePath, partPath, 0, 3, ct).ConfigureAwait(false);
            return keys.Count > 0 ? Math.Clamp(keys[0], 0, 2) : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read where the picture starts in {Path}", partPath);
            return 0;
        }
    }

    private static ProcessStartInfo Psi(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    private static string Clock(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private async Task<(bool Success, string Message)> RunFfmpeg(
        string ffmpegPath, string inputPath, string outputPath, TrimPlan plan, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-nostdin");

        if (plan.LeadTrimSeconds > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(plan.LeadTrimSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);

        var keepDuration = plan.TotalDurationSeconds - plan.LeadTrimSeconds - plan.TrailTrimSeconds;
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(keepDuration.ToString("F3", CultureInfo.InvariantCulture));

        foreach (var a in await AudioChannelScanner.CopyMaps(_encoder.ProbePath, inputPath, cancellationToken).ConfigureAwait(false))
        {
            psi.ArgumentList.Add(a);
        }

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");

        // The cut starts on the keyframe before the movie's sound (and the padding before that), so
        // a few seconds of the old layout's sound (2.0) would open the file, and Jellyfin takes the
        // whole file's layout from its first sound: Dune: Part Two showed as stereo. That sound is
        // dropped; the picture still starts on its keyframe (packet times here are from the cut).
        if (plan.CoreStartSeconds is { } core && core - plan.LeadTrimSeconds > 0.05)
        {
            psi.ArgumentList.Add("-bsf:a");
            psi.ArgumentList.Add("noise=drop='lt(pts*tb," + (core - plan.LeadTrimSeconds).ToString("F3", CultureInfo.InvariantCulture) + ")'");
        }

        psi.ArgumentList.Add("-avoid_negative_ts");
        psi.ArgumentList.Add("make_zero");

        // Recordings keep a .mpg extension from Channels DVR even though the actual
        // container is MPEG-TS. Without an explicit -f, ffmpeg picks the muxer from
        // that extension and silently downgrades the output to MPEG-PS, which packs
        // AC3 into "private_stream_1" PES packets badly enough that players like VLC
        // misread the single 5.1 track as dozens of phantom audio tracks. Force TS so
        // the container always matches the source regardless of the file's extension.
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("mpegts");
        psi.ArgumentList.Add(outputPath);

        return await RunProcess(psi, inputPath, outputPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Success, string Message)> RunProcess(ProcessStartInfo psi, string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var log = new OutputTail();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => log.Add(e.Data);
        process.ErrorDataReceived += (_, e) => log.Add(e.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped: ffmpeg would otherwise keep writing a copy nobody will use.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            _logger.LogInformation("Trim of {Path} stopped", inputPath);
            return (false, "stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch ffmpeg to cut {Path}", inputPath);
            return (false, $"could not launch ffmpeg: {ex.Message}");
        }

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            _logger.LogWarning(
                "ffmpeg cut failed for {Path} (exit code {ExitCode}).\n--- ffmpeg output (tail) ---\n{Output}",
                inputPath, process.ExitCode, Tail(log.ToString(), 25));
            return (false, $"ffmpeg exited {process.ExitCode}: {Tail(log.ToString(), 1)}");
        }

        return (true, "ok");
    }

    /// <summary>
    /// Renames the original aside as the hidden backup (see <see cref="TrimBackups"/>) and moves
    /// the trimmed file into its exact place. The backup is kept for good with
    /// <paramref name="keepOriginal"/>, otherwise for the configured undo days (or deleted
    /// right away when that's 0). The trimmed file may be on a different filesystem than the
    /// recordings folder (when it was cut in the work directory), so falls back
    /// to copy+delete when a plain rename can't cross volumes.
    /// </summary>
    private TrimOutcome SwapIntoPlace(string recordingPath, string trimmedPath, bool keepOriginal)
    {
        // A second cut of the same recording (an audio fix, then a program start): the backup
        // already holds the recording as it was recorded, which is what Undo should bring back,
        // so the once-cut file is dropped instead of becoming the backup.
        var earlier = TrimBackups.Find(recordingPath);
        var backupPath = earlier ?? TrimBackups.PathFor(recordingPath);

        try
        {
            if (earlier is not null)
            {
                File.Delete(recordingPath);
            }
            else
            {
                File.Move(recordingPath, backupPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not move original recording aside before swapping in trimmed audio: {Path}", recordingPath);
            return new TrimOutcome(false, null, $"could not move original aside: {ex.Message}");
        }

        try
        {
            MoveAcrossVolumes(trimmedPath, recordingPath);
        }
        catch (Exception ex)
        {
            // Put the original back so the item is never left without a playable file.
            _logger.LogError(ex, "Could not move trimmed file into place for {Path}; restoring original", recordingPath);
            try
            {
                File.Move(backupPath, recordingPath, overwrite: true);
            }
            catch (Exception restoreEx)
            {
                _logger.LogCritical(restoreEx, "Failed to restore original recording after a failed swap: {Path} (backup at {Backup})", recordingPath, backupPath);
            }

            return new TrimOutcome(false, null, $"could not move trimmed file into place: {ex.Message}");
        }

        if (keepOriginal)
        {
            _backups.Forget(backupPath);
            return new TrimOutcome(true, backupPath, $"trim applied; original kept at {backupPath}");
        }

        var days = Plugin.Instance?.Configuration.TrimUndoDays ?? 0;
        if (days <= 0)
        {
            TryDelete(backupPath);
            _backups.Forget(backupPath);
            return new TrimOutcome(true, null, "trim applied; original discarded");
        }

        // A second cut keeps the first one's deadline: the original has been waiting since then.
        if (_backups.ExpiresUtc(backupPath) is null)
        {
            _backups.Expire(backupPath, DateTime.UtcNow.AddDays(days));
        }

        return new TrimOutcome(true, backupPath, $"trim applied; original kept {days} days for Undo at {backupPath}");
    }

    /// <summary>
    /// Moves the start of the cut to the keyframe at or before it (see <see cref="Keyframes"/>):
    /// started between keyframes, the picture would only appear at the next one, seconds into
    /// the sound.
    /// </summary>
    private async Task<TrimPlan> OnKeyframeAsync(string recordingPath, TrimPlan plan, CancellationToken ct)
    {
        if (plan.LeadTrimSeconds <= 0)
        {
            return plan;
        }

        double? key;
        try
        {
            key = await Keyframes.AtOrBeforeAsync(_encoder.ProbePath, recordingPath, plan.LeadTrimSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't read the keyframes of {Path}; cutting at {Lead:F2}s as asked", recordingPath, plan.LeadTrimSeconds);
            return plan;
        }

        if (key is null)
        {
            return plan;
        }

        var lead = Math.Max(0, key.Value - Keyframes.Margin);
        _logger.LogInformation("Cut of {Path} starts on the keyframe at {Key:F2}s (asked {Lead:F2}s)", recordingPath, key.Value, plan.LeadTrimSeconds);
        return plan with { LeadTrimSeconds = lead, CoreDurationSeconds = plan.TotalDurationSeconds - lead - plan.TrailTrimSeconds };
    }

    // Room left on the drive once a cut is written, so the cut itself never fills it.
    private const long MinFreeAfterCut = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// The hidden file a cut of <paramref name="recordingPath"/> is written to, in the recording's
    /// own folder (Jellyfin's library scan skips dot files); null when that folder can't be
    /// written. A cut left behind by a crash or a restart is removed first.
    /// </summary>
    private string? TempBeside(string recordingPath, string ext)
    {
        var folder = Path.GetDirectoryName(recordingPath);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        var prefix = "." + Path.GetFileNameWithoutExtension(recordingPath) + ".cutting-";
        try
        {
            foreach (var stale in Directory.EnumerateFiles(folder, prefix + "*"))
            {
                _logger.LogInformation("Removing an unfinished cut left behind: {Path}", stale);
                TryDelete(stale);
            }

            var path = Path.Combine(folder, $"{prefix}{Guid.NewGuid():N}{ext}");
            using (File.Create(path))
            {
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation("Can't write beside {Path} ({Message}); cutting in the work directory", recordingPath, ex.Message);
            return null;
        }
    }

    /// <summary>Free bytes on the drive holding <paramref name="path"/>, or -1 when unknown.</summary>
    private static long FreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetDirectoryName(path)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    private static void MoveAcrossVolumes(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: true);
            File.Delete(source);
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Tail(string text, int lines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(no output captured)";
        }

        var all = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        var start = Math.Max(0, all.Length - lines);
        return string.Join('\n', all[start..]);
    }
}
