using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.RepeatAds;

/// <param name="SeriesKey">Which show it belongs to (see <see cref="RepeatAdService.SeriesKey"/>): repeats only count between different ones.</param>
/// <param name="Length">The file's size and time when fingerprinted: a file changed since (cut, trimmed, re-recorded) no longer lines up.</param>
/// <param name="PreCut">Made of a hand-edited recording before its commercials were cut out (of the kept original): it lines up with the breaks from before the cut, whatever the file is now.</param>
public sealed record FingerprintEntry(string SeriesKey, string Name, long Length, DateTime FileTimeUtc, DateTime SavedUtc, bool PreCut = false);

/// <summary>
/// The audio fingerprints of recent recordings (32 bytes per second of sound, about 115 KB an
/// hour), kept for <see cref="Configuration.PluginConfiguration.RepeatAdKeepDays"/> days in
/// &lt;data&gt;/comskip-segments/fingerprints: one .fp file per recording and an index.
/// </summary>
public sealed class FingerprintStore
{
    private readonly ILogger<FingerprintStore> _logger;
    private readonly string _folder;
    private readonly string _indexPath;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, FingerprintEntry> _entries;

    public FingerprintStore(ILogger<FingerprintStore> logger, IApplicationPaths paths)
    {
        _logger = logger;
        _folder = Path.Combine(PluginDataFolder.Get(paths, logger), "fingerprints");
        Directory.CreateDirectory(_folder);
        _indexPath = Path.Combine(_folder, "index.json");
        _entries = Load();
    }

    public IReadOnlyList<(Guid Id, FingerprintEntry Entry)> Entries()
    {
        lock (_gate)
        {
            return _entries.Select(e => (e.Key, e.Value)).ToList();
        }
    }

    public bool TryGetEntry(Guid id, out FingerprintEntry entry)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out entry!);
        }
    }

    public void Save(Guid id, uint[] fp, FingerprintEntry entry)
    {
        lock (_gate)
        {
            try
            {
                File.WriteAllBytes(FilePath(id), MemoryMarshal.AsBytes(fp.AsSpan()).ToArray());
                _entries[id] = entry;
                SaveIndex();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not save the audio fingerprint of {Name}", entry.Name);
            }
        }
    }

    public uint[]? Load(Guid id)
    {
        try
        {
            var bytes = File.ReadAllBytes(FilePath(id));
            return MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(0, bytes.Length / 4 * 4)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Marks a fingerprint as made of a recording before its commercials were cut (see <see cref="FingerprintEntry.PreCut"/>).</summary>
    public void MarkPreCut(Guid id)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var e) && !e.PreCut)
            {
                _entries[id] = e with { PreCut = true };
                SaveIndex();
            }
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            if (_entries.Remove(id))
            {
                TryDelete(FilePath(id));
                SaveIndex();
            }
        }
    }

    /// <summary>Forgets fingerprints older than <paramref name="keepDays"/> days (but those <paramref name="keep"/> says to keep); returns how many.</summary>
    public int Prune(int keepDays, Func<Guid, bool>? keep = null)
    {
        lock (_gate)
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, keepDays));
            var old = _entries.Where(e => e.Value.SavedUtc < cutoff && keep?.Invoke(e.Key) != true).Select(e => e.Key).ToList();
            foreach (var id in old)
            {
                _entries.Remove(id);
                TryDelete(FilePath(id));
            }

            if (old.Count > 0)
            {
                SaveIndex();
            }

            return old.Count;
        }
    }

    /// <summary>How many recordings, the space they take, and the oldest one's date.</summary>
    public (int Count, long Bytes, DateTime? OldestUtc) Summary()
    {
        lock (_gate)
        {
            long bytes = 0;
            foreach (var id in _entries.Keys)
            {
                try
                {
                    bytes += new FileInfo(FilePath(id)).Length;
                }
                catch (IOException)
                {
                }
            }

            return (_entries.Count, bytes, _entries.Count == 0 ? null : _entries.Values.Min(e => e.SavedUtc));
        }
    }

    private string FilePath(Guid id) => Path.Combine(_folder, id.ToString("N") + ".fp");

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }

    private Dictionary<Guid, FingerprintEntry> Load()
    {
        try
        {
            if (File.Exists(_indexPath))
            {
                return JsonSerializer.Deserialize<Dictionary<Guid, FingerprintEntry>>(File.ReadAllText(_indexPath)) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read {Path}; starting the fingerprint list afresh", _indexPath);
        }

        return new();
    }

    private void SaveIndex()
    {
        try
        {
            var tmp = _indexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, _indexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {Path}", _indexPath);
        }
    }
}
