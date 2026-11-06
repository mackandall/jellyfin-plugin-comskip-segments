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
        var seriesName = (item as Episode)?.SeriesName;
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            seriesName = item.Name;
        }

        return ShouldAutoDetectCore(item.Path, seriesName, () => resolveChannel(item, seriesName!), cfg, out reason);
    }

    /// <summary>
    /// Same gate, for a DVR recording that is still being written and so has no library
    /// item yet — used by
    /// <see cref="Jellyfin.Plugin.ComskipSegments.Detection.LiveScanStarter"/> to decide
    /// whether to bother starting a live Comskip process at all. The channel is already
    /// known directly from the in-progress recording, so there's no cache/guide lookup to
    /// make lazy here (unlike the overload above).
    /// </summary>
    public static bool ShouldAutoDetectForInProgressRecording(
        string? path,
        string? seriesOrShowName,
        string? channelName,
        string? channelNumber,
        PluginConfiguration cfg,
        out string reason)
    {
        ChannelResolver.ChannelInfo? channel =
            string.IsNullOrWhiteSpace(channelName) && string.IsNullOrWhiteSpace(channelNumber)
                ? null
                : new ChannelResolver.ChannelInfo(channelName, channelNumber);

        return ShouldAutoDetectCore(path, seriesOrShowName, () => channel, cfg, out reason);
    }

    private static bool ShouldAutoDetectCore(
        string? path,
        string? seriesName,
        Func<ChannelResolver.ChannelInfo?> resolveChannel,
        PluginConfiguration cfg,
        out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrEmpty(path))
        {
            reason = "item has no file path";
            return false;
        }

        if (LiveBuffers.IsUnkept(path))
        {
            reason = "it's a live TV buffer nobody has kept (JellyDVR Live deletes it in a few hours)";
            return false;
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
            var channel = resolveChannel();
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

            // Fail CLOSED, not open, when a block-list is configured but the channel
            // can't be determined at all (RecordingChannelCache entries expire after 2
            // days, and the by-name guide fallback returns nothing once a show ages out
            // of "recently aired" — confirmed happening live: an HBO recording correctly
            // skipped right after it finished slipped through a backstop scan run days
            // later once both lookups had gone cold). Risking a scan on an excluded
            // channel is worse than occasionally skipping one that would have passed.
            if (channelBlock.Count > 0 && channel is null)
            {
                reason = "channel could not be determined and a channel block-list is set; " +
                    "treating as blocked rather than risk scanning a channel meant to be excluded";
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
