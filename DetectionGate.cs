using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// The single "should an automatic trigger run detection on this item?" decision.
/// Every non-forced enqueue path (recording watcher, backstop scan, media-segment-scan
/// auto-enqueue) funnels through here, so the show/folder allow- and block-lists are
/// enforced in exactly one place. Explicit/forced requests bypass it.
/// </summary>
public static class DetectionGate
{
    /// <summary>
    /// Returns true if automatic detection is allowed for <paramref name="item"/> under
    /// the current config. When false, <paramref name="reason"/> explains why (for logs).
    /// </summary>
    /// <param name="resolveChannel">
    /// Lazily resolves the item's DVR channel (name + number) — checking
    /// <see cref="RecordingChannelCache"/> by path first (captured live while the
    /// recording was in progress, the reliable source) and falling back to a by-name
    /// guide lookup via <see cref="ChannelResolver"/>. Only invoked when a channel
    /// allow- or block-list is actually configured, so callers can wire a real
    /// (I/O-costing) lookup without paying for it on every gate check.
    /// </param>
    public static bool ShouldAutoDetect(
        BaseItem item,
        PluginConfiguration cfg,
        out string reason,
        Func<BaseItem, string, ChannelResolver.ChannelInfo?> resolveChannel)
    {
        reason = string.Empty;

        var path = item.Path;
        if (string.IsNullOrEmpty(path))
        {
            reason = "item has no file path";
            return false;
        }

        var seriesName = (item as Episode)?.SeriesName;
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            seriesName = item.Name;
        }

        var candidates = Candidates(path, seriesName);

        var allow = Patterns(cfg.SeriesAllowList);
        if (allow.Count > 0 && !allow.Any(p => candidates.Any(c => Matches(p, c))))
        {
            reason = $"\"{seriesName}\" is not in the allow-list";
            return false;
        }

        var blocked = Patterns(cfg.SeriesBlockList)
            .FirstOrDefault(p => candidates.Any(c => Matches(p, c)));
        if (blocked is not null)
        {
            reason = $"\"{seriesName}\" matches block-list entry \"{blocked}\"";
            return false;
        }

        var channelAllow = Patterns(cfg.ChannelAllowList);
        var channelBlock = Patterns(cfg.ChannelBlockList);
        if (channelAllow.Count > 0 || channelBlock.Count > 0)
        {
            var channel = resolveChannel(item, seriesName!);
            var channelCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(channel?.Name))
            {
                channelCandidates.Add(channel.Value.Name!);
            }

            if (!string.IsNullOrWhiteSpace(channel?.Number))
            {
                channelCandidates.Add(channel.Value.Number!);
            }

            if (channelAllow.Count > 0 && !channelAllow.Any(p => channelCandidates.Any(c => Matches(p, c))))
            {
                reason = channel is null
                    ? "channel could not be determined and a channel allow-list is set"
                    : $"channel \"{channel.Value.Name ?? channel.Value.Number}\" is not in the channel allow-list";
                return false;
            }

            var blockedChannel = channelBlock.FirstOrDefault(p => channelCandidates.Any(c => Matches(p, c)));
            if (blockedChannel is not null)
            {
                reason = $"channel \"{channel!.Value.Name ?? channel.Value.Number}\" matches channel block-list entry \"{blockedChannel}\"";
                return false;
            }
        }

        return true;
    }

    /// <summary>Series name + file name + every folder segment of the path.</summary>
    private static List<string> Candidates(string path, string? seriesName)
    {
        var list = new List<string>();

        if (!string.IsNullOrWhiteSpace(seriesName))
        {
            list.Add(seriesName.Trim());
        }

        list.Add(Path.GetFileNameWithoutExtension(path));

        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            var leaf = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(leaf))
            {
                list.Add(leaf);
            }

            var parent = Path.GetDirectoryName(dir);
            if (string.Equals(parent, dir, StringComparison.Ordinal))
            {
                break;
            }

            dir = parent;
        }

        return list;
    }

    private static List<string> Patterns(string? raw) =>
        (raw ?? string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private static bool Matches(string pattern, string candidate)
    {
        if (pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal))
        {
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(candidate, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase);
    }
}
