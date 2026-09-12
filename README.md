# Comskip Commercial Segments (Jellyfin plugin)

Detects commercials in DVR recordings (and, on demand, existing library files) with an
external Comskip binary, and exposes the breaks as native Jellyfin **Commercial** media
segments so the built-in skip UI handles them. Modeled on how Emby's DVR does it, but
wired for Jellyfin's media-segment system.

## How it fits together

- **One detection queue, two triggers.** `RecordingWatcher` enqueues new recordings;
  `LibraryScanTask` enqueues whitelisted existing files. Both feed the same
  `DetectionManager`, which runs Comskip with a capped worker pool.
- **Detection is out of band.** `ComskipSegmentProvider` (the `IMediaSegmentProvider`)
  never runs Comskip during a scan — it only serves segments already computed and stored
  by the manager, so the Media Segment Scan stays fast. When a job finishes, the manager
  re-runs providers for that one item so results publish immediately.
- **Crash ≠ empty.** `DetectionStore` records "no commercials found" and "detection
  failed" differently, so a corrupt/truncated `.ts` is retried later instead of being
  marked done forever.
- **Read recordings, write a work dir.** Comskip is always invoked with an explicit
  `--output` work directory the service account owns; the read-only recordings folder is
  never written to.

## Build

Requires the .NET SDK matching your server (net10.0 for the Jellyfin 12.0.x line).

```
dotnet publish -c Release
```

The build output DLL is `Jellyfin.Plugin.ComskipSegments.dll` under
`bin/Release/net10.0/`.

> Pin the `Jellyfin.Controller` / `Jellyfin.Model` versions in the `.csproj` to your
> exact server version (Dashboard → About) before building. A mismatch is the usual
> cause of the plugin failing to load.

## Install

1. Find your Jellyfin plugins folder. On a Linux package install it's typically
   `/var/lib/jellyfin/plugins`, owned by the `jellyfin` service account — installing
   requires `sudo` (or write access as that user).
2. Create a subfolder and drop the DLL, `icon.png`, and `meta.json` in:
   ```
   sudo mkdir -p /var/lib/jellyfin/plugins/ComskipSegments
   sudo cp bin/Release/net10.0/Jellyfin.Plugin.ComskipSegments.dll icon.png meta.json /var/lib/jellyfin/plugins/ComskipSegments/
   ```
3. Restart Jellyfin.
4. Dashboard → Plugins → **Comskip Commercial Segments** → set the paths → click
   **Test paths**. This checks them *as the service account*, which catches the
   `/home` traversal and systemd-sandbox (`ProtectHome`) cases the browser can't see.

## Use

- **DVR:** leave "Automatically detect commercials on new recordings" on. New recordings
  under the recordings folder are queued automatically.
- **Existing files:** add absolute folders to "Extra scan folders", then run
  Dashboard → Scheduled Tasks → **Scan library for commercials (Comskip)** on demand.

Skip behavior (Auto vs Ask) is Jellyfin's own setting, per user, under playback
preferences — this plugin only supplies the segments.

## The one file to watch

`ComskipSegmentProvider.cs` is the only file touching the version-sensitive
media-segment API. If it doesn't compile against your server, match its three members to
`MediaBrowser.Controller/MediaSegments/IMediaSegmentProvider.cs` at your server's tag.
Everything else is plain .NET.

## Known follow-ups (not yet built)

- Duration sanity check (compare EDL end vs. `ffprobe` duration) to flag truncated
  recordings distinctly from clean short ones.
- Optional remux/cut path if you ever want removal rather than skip markers.
- Retry backoff/scheduling for `Failed` items.

## Repo metadata

GitHub's description and topics aren't stored in this repo, so they drift silently
after a rename/fork. Current canonical values, kept here so they can be diffed against
reality (`gh repo view --json description,repositoryTopics`):

- **Description:** Detect commercials in Jellyfin DVR recordings with Comskip and
  expose them as native MediaSegments
- **Topics:** `jellyfin`, `jellyfin-plugin`, `comskip`, `dvr`, `commercial-detection`,
  `media-segments`, `livetv`

## License

MIT — see [LICENSE](LICENSE).
