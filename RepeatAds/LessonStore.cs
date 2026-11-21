using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.RepeatAds;

/// <summary>
/// A hand edit kept as a lesson for its show: the breaks you saved, on the file they were saved on
/// (its size and time, which tie them to that file's sound fingerprint). Scans learn a show's breaks
/// from its teaching lessons, so re-running Comskip, cutting the commercials out or deleting the
/// recording no longer takes an edit out of what the show learns from.
/// </summary>
/// <param name="SeriesKey">The show (<see cref="RepeatAdService.SeriesKey"/>), kept for when the recording is gone.</param>
/// <param name="Show">The show's name, as shown.</param>
/// <param name="Name">The recording's name ("S01E02 · Title" for an episode).</param>
/// <param name="Breaks">Your breaks, on the file of <paramref name="FileLength"/> and <paramref name="FileTimeUtc"/>.</param>
/// <param name="Seconds">The recording's length.</param>
/// <param name="Teaching">False: kept, but not learned from (switched off on the Lessons tab).</param>
/// <param name="FileLength">The file's size when saved (null: not known, for lessons made of older edits).</param>
/// <param name="ScanBefore">The breaks the scan had before your first edit, when they were a scan's
/// (for showing what you corrected); null when not known.</param>
/// <param name="Origin">saved (on the dashboard), migrated (a hand edit from before lessons).</param>
public sealed record Lesson(
    string SeriesKey,
    string Show,
    string Name,
    IReadOnlyList<CommercialBreak> Breaks,
    double Seconds,
    DateTime SavedUtc,
    bool Teaching = true,
    long? FileLength = null,
    DateTime? FileTimeUtc = null,
    IReadOnlyList<CommercialBreak>? ScanBefore = null,
    string Origin = "saved");

/// <summary>The lessons, by recording (lessons.json in the plugin's data folder).</summary>
public sealed class LessonStore
{
    private readonly ILogger<LessonStore> _logger;
    private readonly string _path;
    private readonly object _ioLock = new();
    private readonly ConcurrentDictionary<Guid, Lesson> _lessons;
    private bool _migrated;

    public LessonStore(ILogger<LessonStore> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;
        var dataFolder = PluginDataFolder.Get(applicationPaths, _logger);
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "lessons.json");
        (_lessons, _migrated) = Load();
    }

    /// <summary>The hand edits from before lessons existed have been made into lessons.</summary>
    public bool Migrated => _migrated;

    public IReadOnlyCollection<KeyValuePair<Guid, Lesson>> All() => _lessons.ToArray();

    public bool TryGet(Guid id, out Lesson lesson) => _lessons.TryGetValue(id, out lesson!);

    public bool Has(Guid id) => _lessons.ContainsKey(id);

    /// <summary>The show's lessons that teach (all of them with <paramref name="teachingOnly"/> false).</summary>
    public List<(Guid Id, Lesson Lesson)> OfShow(string seriesKey, bool teachingOnly = true) =>
        _lessons.Where(kv => kv.Value.SeriesKey == seriesKey && (!teachingOnly || kv.Value.Teaching))
            .Select(kv => (kv.Key, kv.Value))
            .OrderByDescending(x => x.Value.SavedUtc)
            .ToList();

    public void Save(Guid id, Lesson lesson)
    {
        _lessons[id] = lesson;
        Persist();
    }

    public bool SetTeaching(Guid id, bool teaching)
    {
        if (!_lessons.TryGetValue(id, out var lesson))
        {
            return false;
        }

        _lessons[id] = lesson with { Teaching = teaching };
        Persist();
        return true;
    }

    public bool Remove(Guid id)
    {
        var removed = _lessons.TryRemove(id, out _);
        if (removed)
        {
            Persist();
        }

        return removed;
    }

    /// <summary>Adds lessons made of the hand edits from before lessons existed (once; never replaces one).</summary>
    public int AddMigrated(IEnumerable<(Guid Id, Lesson Lesson)> found)
    {
        var added = 0;
        foreach (var (id, lesson) in found)
        {
            if (_lessons.TryAdd(id, lesson))
            {
                added++;
            }
        }

        _migrated = true;
        Persist();
        return added;
    }

    private sealed record FileShape(Dictionary<Guid, Lesson>? Lessons, bool Migrated);

    private (ConcurrentDictionary<Guid, Lesson>, bool) Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var data = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path));
                if (data is not null)
                {
                    return (new ConcurrentDictionary<Guid, Lesson>(data.Lessons ?? new()), data.Migrated);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the lessons; starting fresh");
        }

        return (new ConcurrentDictionary<Guid, Lesson>(), false);
    }

    private void Persist()
    {
        lock (_ioLock)
        {
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new FileShape(new Dictionary<Guid, Lesson>(_lessons), _migrated)));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not save the lessons");
            }
        }
    }
}
