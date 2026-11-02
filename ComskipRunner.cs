using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Comskip;

public enum ComskipOutcome
{
    /// <summary>Ran and produced a parseable .edl (which may contain zero commercials).</summary>
    Ran,

    /// <summary>Crashed, timed out, or produced no .edl at all. Should be retried later.</summary>
    Failed
}

/// <param name="Error">Why it failed, in a few words (the Queue tab shows it).</param>
/// <param name="Pieces">Comskip's pieces (see <see cref="ComskipPieces"/>), when asked for and found.</param>
public sealed record ComskipRunResult(ComskipOutcome Outcome, string? EdlPath, string Log, string? Error = null, IReadOnlyList<ComskipPiece>? Pieces = null);

/// <summary>
/// Launches the external Comskip binary as a child process.
/// Design choices baked in from testing:
///   - Always passes an explicit --output work dir; never relies on Comskip's default
///     (which writes next to the source and fails on the read-only recordings folder).
///   - Uses ArgumentList so spaces/apostrophes in filenames ("Grey's Anatomy") survive.
///   - Treats "a parseable .edl appeared" as success; Comskip's exit codes are unreliable.
///   - Kills the process on timeout so a corrupt .ts can't hold a worker slot forever.
/// </summary>
public sealed class ComskipRunner
{
    private readonly ILogger<ComskipRunner> _logger;

    public ComskipRunner(ILogger<ComskipRunner> logger) => _logger = logger;

    public async Task<ComskipRunResult> RunAsync(
        string comskipPath,
        string iniPath,
        string workDir,
        string mediaPath,
        int timeoutMinutes,
        CancellationToken cancellationToken,
        Action<int>? started = null,
        Func<TimeSpan>? activeTime = null,
        bool wantPieces = false)
    {
        // Defensive: a stray leading/trailing space on a configured path makes it
        // non-rooted on Linux, so Comskip would write its output to a relative dir.
        comskipPath = comskipPath?.Trim() ?? string.Empty;
        iniPath = iniPath?.Trim() ?? string.Empty;
        workDir = workDir?.Trim() ?? string.Empty;
        mediaPath = mediaPath?.Trim() ?? string.Empty;

        Directory.CreateDirectory(workDir);
        ClearOldOutput(workDir, mediaPath, _logger);
        var configuredIni = iniPath;
        iniPath = FinishedFileIni(iniPath, workDir, _logger, wantPieces);

        var psi = new ProcessStartInfo
        {
            FileName = comskipPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir
        };
        psi.ArgumentList.Add($"--ini={iniPath}");
        psi.ArgumentList.Add($"--output={workDir}");
        psi.ArgumentList.Add(mediaPath);

        var log = new OutputTail();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => log.Add(e.Data);
        process.ErrorDataReceived += (_, e) => log.Add(e.Data);

        try
        {
            process.Start();
            started?.Invoke(process.Id);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeout = TimeSpan.FromMinutes(timeoutMinutes);
            if (activeTime is null)
            {
                timeoutCts.CancelAfter(timeout);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            else
            {
                // Timed on the time it actually ran: a scan paused from the Queue tab isn't timed out.
                var exited = process.WaitForExitAsync(timeoutCts.Token);
                while (!exited.IsCompleted)
                {
                    await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!exited.IsCompleted && activeTime() > timeout)
                    {
                        timeoutCts.Cancel();
                    }
                }

                await exited.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Comskip timed out after {Minutes} min on {Media}", timeoutMinutes, mediaPath);
            TryKill(process);
            return new ComskipRunResult(ComskipOutcome.Failed, null, log.ToString(), $"Timed out after {timeoutMinutes} min");
        }
        catch (OperationCanceledException)
        {
            // Server shutdown / task cancel — propagate.
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch Comskip for {Media}", mediaPath);
            return new ComskipRunResult(ComskipOutcome.Failed, null, log.ToString(), "Comskip couldn't be started: " + ex.Message);
        }

        var exitCode = TryGetExitCode(process);

        // Success is judged by output, not exit code: did a .edl land in the work dir?
        var baseName = Path.GetFileNameWithoutExtension(mediaPath);
        var edlPath = Path.Combine(workDir, baseName + ".edl");

        if (!File.Exists(edlPath))
        {
            // Could be a genuinely corrupt/truncated recording, or Comskip itself is
            // misconfigured (bad ini path, unwritable work dir, missing codec support).
            // Surface the exit code and its output so the two are actually tellable apart.
            _logger.LogWarning(
                "Comskip produced no .edl for {Media} (exit code {ExitCode}). Either a corrupt/truncated "
                + "recording or a Comskip config problem. Will retry later.\nComskip args: --ini={Ini} --output={Work}\n"
                + "--- Comskip output (tail) ---\n{Output}",
                mediaPath, exitCode, iniPath, workDir, Tail(log.ToString(), 20));
            return new ComskipRunResult(ComskipOutcome.Failed, null, log.ToString(), $"Comskip wrote no .edl (exit code {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"})");
        }

        _logger.LogDebug("Comskip finished for {Media} (exit code {ExitCode})", mediaPath, exitCode);
        IReadOnlyList<ComskipPiece>? pieces = null;
        if (wantPieces)
        {
            var logPath = Path.Combine(workDir, baseName + ".log");
            pieces = ComskipPieces.TryRead(logPath);
            if (pieces is null or { Count: 0 })
            {
                _logger.LogDebug("No piece list in {Log}", logPath);
            }

            if (!VerboseIsOn(configuredIni))
            {
                TryDelete(logPath); // ours, about 350 KB: only the table was wanted
            }
        }

        return new ComskipRunResult(ComskipOutcome.Ran, edlPath, log.ToString(), Pieces: pieces);
    }

    /// <summary>
    /// Deletes an earlier scan's output for this recording name from the work folder (a past
    /// recording of the same episode, kept when "delete clutter" is off), so it's never read
    /// as this scan's result: Comskip writes no .edl until it finds a break, or at all if it fails.
    /// </summary>
    internal static void ClearOldOutput(string workDir, string mediaPath, ILogger logger)
    {
        var baseName = Path.GetFileNameWithoutExtension(mediaPath);
        foreach (var ext in new[] { ".edl", ".txt", ".log" })
        {
            var f = Path.Combine(workDir, baseName + ext);
            try
            {
                if (File.Exists(f))
                {
                    File.Delete(f);
                    logger.LogInformation("Removed an earlier scan's {File} before scanning again", f);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove an earlier scan's {File}", f);
            }
        }
    }

    // Every live_tv=N setting, not live_tv_retries (Comskip finds "live_tv=" anywhere in the
    // file, a comment included, so every match is changed).
    private static readonly System.Text.RegularExpressions.Regex LiveTvSetting =
        new(@"(?<![A-Za-z_])live_tv\s*=\s*\d+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The ini for scanning a finished file: the configured one with <c>live_tv=0</c>, written to the
    /// work folder (rewritten only when it changes). The live scan needs <c>live_tv=1</c> to follow a
    /// recording as it grows, but with it Comskip also waits at the end of a FINISHED file for more
    /// to arrive: 4 s, <c>live_tv_retries</c> times (16 here, so 64 s of nothing per scan, seen
    /// 2026-11-01). Without it the .edl and .txt came out byte for byte the same, a minute sooner.
    /// Falls back to the configured ini if the copy can't be made.
    /// </summary>
    internal static string FinishedFileIni(string iniPath, string workDir, ILogger logger, bool verbose = false)
    {
        try
        {
            var text = File.ReadAllText(iniPath);
            var finished = LiveTvSetting.IsMatch(text) ? LiveTvSetting.Replace(text, "live_tv=0") : text;
            if (verbose && !VerboseSetting.Matches(finished).Any(m => m.Groups[1].Value != "0"))
            {
                // Comskip's piece list is only written to its log with verbose on.
                finished = VerboseSetting.IsMatch(finished)
                    ? VerboseSetting.Replace(finished, "verbose=" + VerboseForPieces)
                    : finished.TrimEnd() + "\nverbose=" + VerboseForPieces + "\n";
            }

            if (finished == text)
            {
                return iniPath; // nothing to change
            }

            var target = Path.Combine(workDir, verbose ? "comskip-finished-files-pieces.ini" : "comskip-finished-files.ini");
            if (!File.Exists(target) || File.ReadAllText(target) != finished)
            {
                File.WriteAllText(target, finished);
            }

            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not make a live_tv=0 copy of {Ini}; scanning with it as is (each scan waits about a minute at the end)", iniPath);
            return iniPath;
        }
    }

    /// <summary>The <c>verbose</c> level asked for when the piece list is wanted (the table is written from level 1).</summary>
    internal const int VerboseForPieces = 1;

    private static readonly System.Text.RegularExpressions.Regex VerboseSetting =
        new(@"(?m)^\s*verbose\s*=\s*(\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool VerboseIsOn(string iniPath)
    {
        try
        {
            return VerboseSetting.Matches(File.ReadAllText(iniPath)).Any(m => m.Groups[1].Value != "0");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static int? TryGetExitCode(Process p)
    {
        try
        {
            return p.HasExited ? p.ExitCode : null;
        }
        catch
        {
            return null;
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

    private void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not kill Comskip process");
        }
    }
}
