using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Editing;

/// <summary>
/// A small picture of every keyframe of a recording (one every few seconds), for the dashboard
/// editor's scrubbing: the picture under the mouse along the timeline, the overview grid, and
/// fast forward and rewind. Only keyframes are decoded, so a 22-minute episode takes about 7 s
/// and 1.3 MB. Made the first time a recording is opened in the editor, a picture at a time
/// (the editor shows them as they come), and kept in the plugin's folder until the file changes
/// or nobody has opened it for <see cref="KeepDays"/> days.
/// </summary>
public sealed partial class OverviewService
{
    public const int Width = 240;
    public const int KeepDays = 30;

    private readonly ILogger<OverviewService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _folder;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    // One at a time: each is a whole-file decode.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OverviewService(ILogger<OverviewService> logger, IApplicationPaths paths, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _folder = Path.Combine(PluginDataFolder.Get(paths, _logger), "overview");
        Directory.CreateDirectory(_folder);
    }

    /// <summary>Pictures from <paramref name="from"/> on, as far as made, and whether all are.</summary>
    public sealed record Snapshot(bool Done, string? Error, IReadOnlyList<(double T, byte[] Jpeg)> Frames);

    /// <summary>The overview's pictures from number <paramref name="from"/> on; starts making them when there are none yet.</summary>
    public Snapshot Get(Guid itemId, string path, int from)
    {
        var signature = Signature(path);
        var entry = _entries.GetOrAdd(itemId, _ => Load(itemId) ?? new Entry());
        lock (entry)
        {
            if (entry.Signature != signature && !entry.Running)
            {
                entry.Reset(signature);
                entry.Running = true;
                _ = Task.Run(() => MakeAsync(itemId, path, entry));
            }

            entry.UsedUtc = DateTime.UtcNow;
            var frames = new List<(double, byte[])>();
            for (var i = Math.Max(0, from); i < entry.Times.Count; i++)
            {
                frames.Add((entry.Times[i], ReadPicture(itemId, entry, i)));
            }

            return new Snapshot(entry.Done, entry.Error, frames);
        }
    }

    /// <summary>Removes overviews of recordings that are gone or haven't been opened for a while.</summary>
    public void CleanUp(Func<Guid, bool> exists)
    {
        foreach (var dir in Directory.EnumerateDirectories(_folder))
        {
            if (!Guid.TryParse(Path.GetFileName(dir), out var id) || (_entries.TryGetValue(id, out var e) && e.Running))
            {
                continue;
            }

            var index = Path.Combine(dir, "index.json");
            var used = File.Exists(index) ? File.GetLastWriteTimeUtc(index) : Directory.GetLastWriteTimeUtc(dir);
            if (_entries.TryGetValue(id, out var live) && live.UsedUtc > used)
            {
                used = live.UsedUtc;
            }

            if (!exists(id) || DateTime.UtcNow - used > TimeSpan.FromDays(KeepDays))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    _entries.TryRemove(id, out _);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not remove the overview {Dir}", dir);
                }
            }
        }
    }

    private async Task MakeAsync(Guid itemId, string path, Entry entry)
    {
        var dir = Path.Combine(_folder, itemId.ToString("N"));
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(dir);
                var encoder = _serviceProvider.GetRequiredService<IMediaEncoder>();
                var (start, _) = await AudioChannelScanner.ProbeFormatTimes(encoder.ProbePath, path, CancellationToken.None).ConfigureAwait(false);
                var sw = Stopwatch.StartNew();
                await DecodeAsync(encoder.EncoderPath, path, start, Path.Combine(dir, "pictures.bin"), entry).ConfigureAwait(false);
                lock (entry)
                {
                    entry.Done = true;
                }

                Save(itemId, entry);
                _logger.LogInformation("Overview of {Path}: {Count} pictures in {Seconds:F1}s", path, entry.Times.Count, sw.Elapsed.TotalSeconds);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not make the overview of {Path}", path);
            lock (entry)
            {
                entry.Error = ex.Message;
                entry.Done = true;
            }
        }
        finally
        {
            lock (entry)
            {
                entry.Running = false;
            }
        }
    }

    // Decodes the keyframes only, scaled small, and appends each JPEG to the pictures file with its
    // time as it comes: a picture's time is from showinfo (ffmpeg's line per frame, on stderr).
    private static async Task DecodeAsync(string ffmpeg, string path, double start, string picturesPath, Entry entry)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-nostdin", "-threads", "4", "-skip_frame", "nokey", "-copyts", "-i", path,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"showinfo,scale={Width}:-2", "-fps_mode", "passthrough",
            "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "6", "pipe:1"
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        var times = new List<double>();
        var stderr = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                var m = PtsTime().Match(line);
                if (m.Success)
                {
                    lock (times)
                    {
                        times.Add(Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - start, 4));
                    }
                }
            }
        });

        await using var pictures = new FileStream(picturesPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var stdout = process.StandardOutput.BaseStream;
        var buffer = new byte[1 << 16];
        var current = new MemoryStream();
        var made = 0;
        int read;
        while ((read = await stdout.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                current.WriteByte(buffer[i]);

                // A JPEG ends with FF D9 (inside one, an FF in the data is always followed by 00).
                if (buffer[i] == 0xD9 && current.Length >= 2 && current.GetBuffer()[current.Length - 2] == 0xFF)
                {
                    var offset = pictures.Position;
                    current.Position = 0;
                    await current.CopyToAsync(pictures).ConfigureAwait(false);
                    await pictures.FlushAsync().ConfigureAwait(false);
                    var length = (int)current.Length;
                    current.SetLength(0);

                    // Its time: showinfo prints a frame's line before the frame is encoded, but the
                    // two pipes are read apart, so wait for it briefly.
                    double t = -1;
                    for (var tries = 0; tries < 200; tries++)
                    {
                        lock (times)
                        {
                            if (times.Count > made)
                            {
                                t = times[made];
                                break;
                            }
                        }

                        await Task.Delay(5).ConfigureAwait(false);
                    }

                    made++;
                    if (t < 0)
                    {
                        continue;
                    }

                    lock (entry)
                    {
                        entry.Times.Add(t);
                        entry.Offsets.Add(offset);
                        entry.Lengths.Add(length);
                    }
                }
            }
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
        await stderr.ConfigureAwait(false);
    }

    private byte[] ReadPicture(Guid itemId, Entry entry, int i)
    {
        try
        {
            using var file = new FileStream(Path.Combine(_folder, itemId.ToString("N"), "pictures.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            file.Position = entry.Offsets[i];
            var bytes = new byte[entry.Lengths[i]];
            file.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return Array.Empty<byte>();
        }
    }

    // The file as it is now: a cut, a conversion or a re-recording changes this, and the overview is made again.
    private static string Signature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private Entry? Load(Guid itemId)
    {
        var index = Path.Combine(_folder, itemId.ToString("N"), "index.json");
        try
        {
            if (File.Exists(index) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(index)) is { } s
                && s.Times.Count == s.Offsets.Count && s.Times.Count == s.Lengths.Count)
            {
                var e = new Entry { Signature = s.Signature, Done = true };
                e.Times.AddRange(s.Times);
                e.Offsets.AddRange(s.Offsets);
                e.Lengths.AddRange(s.Lengths);
                return e;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the overview index {Index}", index);
        }

        return null;
    }

    private void Save(Guid itemId, Entry entry)
    {
        try
        {
            Saved s;
            lock (entry)
            {
                s = new Saved(entry.Signature, entry.Times.ToList(), entry.Offsets.ToList(), entry.Lengths.ToList());
            }

            var index = Path.Combine(_folder, itemId.ToString("N"), "index.json");
            File.WriteAllText(index + ".tmp", JsonSerializer.Serialize(s));
            File.Move(index + ".tmp", index, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the overview index of {ItemId}", itemId);
        }
    }

    [GeneratedRegex(@"\bn:\s*\d+\s+pts:\s*-?\d+\s+pts_time:(-?[0-9.]+)")]
    private static partial Regex PtsTime();

    private sealed record Saved(string Signature, List<double> Times, List<long> Offsets, List<int> Lengths);

    private sealed class Entry
    {
        public string Signature { get; set; } = string.Empty;

        public List<double> Times { get; } = new();

        public List<long> Offsets { get; } = new();

        public List<int> Lengths { get; } = new();

        public bool Done { get; set; }

        public bool Running { get; set; }

        public string? Error { get; set; }

        public DateTime UsedUtc { get; set; } = DateTime.UtcNow;

        public void Reset(string signature)
        {
            Signature = signature;
            Times.Clear();
            Offsets.Clear();
            Lengths.Clear();
            Done = false;
            Error = null;
        }
    }
}

/// <summary>Removes old overviews: at startup, then once a day.</summary>
public sealed class OverviewCleaner : BackgroundService
{
    private readonly OverviewService _overviews;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;

    public OverviewCleaner(OverviewService overviews, MediaBrowser.Controller.Library.ILibraryManager libraryManager)
    {
        _overviews = overviews;
        _libraryManager = libraryManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
                _overviews.CleanUp(id => _libraryManager.GetItemById(id) is not null);
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
