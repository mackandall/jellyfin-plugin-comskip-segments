using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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

public sealed record ComskipRunResult(ComskipOutcome Outcome, string? EdlPath, string Log);

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
        CancellationToken cancellationToken)
    {
        // Defensive: a stray leading/trailing space on a configured path makes it
        // non-rooted on Linux, so Comskip would write its output to a relative dir.
        comskipPath = comskipPath?.Trim() ?? string.Empty;
        iniPath = iniPath?.Trim() ?? string.Empty;
        workDir = workDir?.Trim() ?? string.Empty;
        mediaPath = mediaPath?.Trim() ?? string.Empty;

        Directory.CreateDirectory(workDir);

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

        var log = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Comskip timed out after {Minutes} min on {Media}", timeoutMinutes, mediaPath);
            TryKill(process);
            return new ComskipRunResult(ComskipOutcome.Failed, null, log.ToString());
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
            return new ComskipRunResult(ComskipOutcome.Failed, null, log.ToString());
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
            return new ComskipRunResult(ComskipOutcome.Failed, null, log.ToString());
        }

        _logger.LogDebug("Comskip finished for {Media} (exit code {ExitCode})", mediaPath, exitCode);
        return new ComskipRunResult(ComskipOutcome.Ran, edlPath, log.ToString());
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
