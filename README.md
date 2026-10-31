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
- **Audio-format trim is the deliberate exception to that.** Some broadcast recordings
  open (and/or close) with a different audio channel layout than the movie itself — e.g.
  a network preview/bumper block in 2.0 stereo before a movie broadcast in 5.1 — which
  makes the whole file read as 2.0 even though the movie is 5.1, because Jellyfin probes
  audio format from the start of the file. `AudioTrim/AudioChannelScanner.cs` decodes the
  primary audio stream's per-frame channel count across the whole file via `ffprobe` and
  groups it into contiguous runs; the run containing the recording's own temporal midpoint
  is treated as the movie (this is what correctly survives DVR pre-roll padding that
  starts mid-way through a differently-formatted previous program, rather than just
  trusting whatever's at t=0). `AudioTrim/RecordingTrimmer.cs` then cuts everything
  outside that run with `ffmpeg -c copy` in the work directory first, and only once that
  output passes a sanity check does it swap the result into the recording's own path —
  the one place this plugin does write into the recordings folder, and only when this
  feature is explicitly turned on. Off by default; see "Audio format trim" below.

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

### Option A: via a plugin repository (no build needed)

1. Dashboard → Plugins → **Repositories** → add:
   ```
   https://raw.githubusercontent.com/mackandall/jellyfin-plugin-comskip-segments/main/manifest.json
   ```
2. Dashboard → Plugins → **Catalog** → **Comskip Commercial Segments** → Install.
3. Restart Jellyfin.

The JellyDVR plugin repository lists Comskip Commercial Segments too, beside the other JellyDVR
plugins (Episode History, JellyDVR Space, Schedule and Live, XMLTV Guide Cleaner):
`https://raw.githubusercontent.com/mackandall/jdvr-client/main/plugins/manifest.json`. Add one or the other, not both.

### Option B: build and install manually

1. Find your Jellyfin plugins folder. On a Linux package install it's typically
   `/var/lib/jellyfin/plugins`, owned by the `jellyfin` service account — installing
   requires `sudo` (or write access as that user).
2. Create a subfolder and drop the DLL, `icon.png`, and `meta.json` in:
   ```
   sudo mkdir -p /var/lib/jellyfin/plugins/ComskipSegments
   sudo cp bin/Release/net10.0/Jellyfin.Plugin.ComskipSegments.dll icon.png meta.json /var/lib/jellyfin/plugins/ComskipSegments/
   ```
3. Restart Jellyfin.

### Either way: Comskip itself

Dashboard → Plugins → **Comskip Commercial Segments** → **Install Comskip**. On a Linux
server (x86-64 or arm64) this downloads a self-contained Comskip 0.83.001 built from the
open-source [Comskip](https://github.com/erikkaashoek/Comskip) (GPL-2.0; all the former
"donator" features, H.264/HEVC and multi-threading, included), checks its SHA-256, puts it
with a default `comskip.ini` and a work folder in Jellyfin's data folder
(`<data>/comskip`), and fills in the three paths. How it's built: `comskip/README.md`.

Elsewhere (Windows, other processors), or to use your own Comskip, set the paths and click
**Test paths**. This checks them *as the service account*, which catches the
`/home` traversal and systemd-sandbox (`ProtectHome`) cases the browser can't see.

### Browser overlay & item-menu buttons (optional, separate step)

The seek-bar commercial-marker overlay and the two item-details `...` menu entries
("Scan for commercials" and "Fix opening/closing audio format") are browser-side scripts
that jellyfin-web's `index.html` has to load. Jellyfin's own skip prompt works without them.

**Easiest:** Dashboard → Plugins → **Comskip Commercial Segments** → **Web overlay** →
**Install web overlay**. That works wherever Jellyfin may write `index.html` (Docker and
Windows usually). On a Linux package install the file belongs to root: the page then shows
a one-time `chown` to run (on Docker, inside the container), after which the button works. With
"Put it back by itself on startup" on (the default), the plugin re-adds it after a jellyfin-web
update wherever it may write the file.

**Or** run the script with sudo:

```
sudo Web/install-overlay.sh
# or, if your web root isn't the default /usr/share/jellyfin/web:
sudo Web/install-overlay.sh /path/to/jellyfin-web/index.html
```

Safe to re-run any time (each patch checks its own marker comment and only inserts if
missing). **Re-run it after every `jellyfin-web` package upgrade** — that package
overwrites `index.html` wholesale, silently dropping the patch. If that happens, the
plugin's own startup log will warn with "web patch is missing at ...". Hard-refresh the
browser tab after (re)running it.

Both menu entries only ever appear for users with the administrator role — regular
accounts never see them, regardless of this patch. "Fix opening/closing audio format"
also always appears once the patch is applied, even when **Enable audio-format trim**
is off in settings; clicking it just returns a "disabled in plugin settings" message in
that case rather than being hidden outright.

## Use

- **DVR:** leave "Automatically detect commercials on new recordings" on. New recordings
  under the recordings folder are queued automatically.
- **Existing files:** add absolute folders to "Extra scan folders", then run
  Dashboard → Scheduled Tasks → **Scan library for commercials (Comskip)** on demand.
  Comskip only runs automatically on files under the recordings folder and those extra
  folders; any other library (e.g. archives on a NAS) is never scanned unless you ask for
  it from an item's "..." menu.
- **Archives with an .edl already:** when a video has an `.edl` beside it (same name,
  `.edl` extension) written by another program — SageTV, Channels DVR, Emby, a standalone
  Comskip — the plugin uses it instead of running Comskip, automatically (on Jellyfin's
  Media Segment Scan) and for manual scans alike. Both EDL styles are understood:
  Comskip's `0` (cut) and Kodi/Channels-style `3` (commercial break). Such a file is never
  overwritten; "Also place a copy of the .edl next to the recording" only replaces `.edl`
  files this plugin wrote itself.
  Jellyfin's library scan doesn't ask for segments, so after adding a library (or files)
  that come with `.edl`s, run Dashboard → Scheduled Tasks → **Media Segment Scan** once
  rather than wait for it; until then those videos show no commercials.
- **Library renamed or its folders edited:** Jellyfin deletes and re-creates every item
  of the library, which also deletes their segments. The plugin notices each re-added
  item that it has breaks for and publishes them again about 15 seconds later.

Skip behavior (Auto vs Ask) is Jellyfin's own setting, per user, under playback
preferences — this plugin only supplies the segments.

### Manual re-detect / clearing a bad result

The config page's "Manual re-detect" section (item ID + buttons) covers three cases for
one item at a time:

- **Run detection**, Force checked: clears any existing result and segments, then
  re-scans — bypassing the show/channel allow/block lists, same as a forced action
  everywhere else in this plugin.
- **Check status**: shows the stored result without changing anything.
- **Clear result (no re-scan)**: wipes the published segments and stored record only —
  no re-scan is queued. Use this when a result was produced by something that shouldn't
  have run in the first place (most commonly: a channel-cache entry that expired before
  a later automatic pass picked the item up anyway), where a forced re-run would just
  reproduce the same unwanted result.

### Audio format trim

Off by default (**Enable audio-format trim** in plugin settings). Once on, you can run
it two ways:

- **Automatically:** turn on **Apply automatically as recordings finish** too. Runs as
  part of the normal post-recording pipeline, before Comskip, so break timestamps come
  out correct against the final file from the start.
- **Per item, manually:** leave automatic apply off and either click **"Fix
  opening/closing audio format"** in an item's `...` action-sheet menu, or use the "Audio
  format trim" section's item-ID field and buttons on the plugin's config page. Good way
  to build confidence in the detector against your own channels before trusting it
  unattended. If Comskip had already published commercial-skip segments for that item,
  a trim that cuts anything off the front invalidates their timestamps — this is detected
  and handled automatically (segments cleared, then a normal, gate-respecting Comskip
  re-detect is queued — so a block-listed channel that a trim just touched stays
  block-listed rather than getting silently re-scanned as a side effect).

**Kept originals.** A trim or cut keeps the untouched original as a hidden file beside the
recording (`.<name>.original<ext>`; Jellyfin doesn't list hidden files) so it can be undone.
By default it's kept for **7 days** (*Otherwise, keep it for undo (days)*) and then deleted;
**Keep the original file as a backup** keeps it for good instead (it was on by default before
1.3.1.0 — while an original is kept, a cut saves no space). The dashboard shows how much
space the kept originals take, with **Delete all originals** and a Delete original button per
cut recording. An original whose recording was deleted or moved without it can't be undone
to, so it's deleted a day after it's found that way (the plugin looks once a day; setting
*Delete originals left without their recording*, on by default).

### Commercial segments dashboard

Dashboard → **Commercial segments** (admins) lists the recordings with their breaks, with
filters, search and a bar per recording showing where the breaks fall. Open one to fix
its breaks frame by frame and, if you like, cut them out of the file.

- **Moving around:** the timeline shows every break; point at it to see that moment's
  picture, drag along it to scrub, or open the **Overview** grid of the whole recording
  (one picture per keyframe, made in the background the first time a recording is
  opened, a few seconds per episode, kept in the plugin's folder until the file changes
  or 30 days unopened). The row of frames under the picture scrolls: drag it (or its scroll
  bar) to look along it, click a frame to go there; the frame you're on is outlined in yellow.
- **Playing:** **Play ▶** and **◀ Reverse** at 1×, 2×, 4×, 8×, 16× or 32× (J/K/L as in
  editing programs: again = faster; Space plays/stops). Up to 8× shows real frames decoded on
  the server (on Jellyfin's GPU when it has a VAAPI/QSV one), 16× and 32× the overview's
  keyframes; sound plays forwards up to 4×.
- **Stepping:** ←/→ or the mouse wheel step one frame, Shift one second, ↑/↓ (or Ctrl +
  wheel) the previous/next keyframe, `,`/`.` the previous/next black frames.
- **Fixing breaks:** `[` and `]` (or the Start/End buttons) move the edge of the break
  you're in, or mark a new break's start and end anywhere else. Save writes the breaks
  (and the `.edl` beside the recording when that setting is on); only commercial
  segments are edited, intro/recap segments from other plugins are left alone.
- **Cut commercials out:** removes the breaks from the file with a fast stream copy (no
  re-encode), in the background with progress. Cuts land on keyframes; **Cut rounding**
  in settings picks whether a split second of ad or of show is kept at each edge. Not
  while the recording is still recording or someone is watching it. Resume positions
  and the program start move with the cut, and **Undo cut** puts the original back for
  as long as it's kept (the audio-trim backup settings).
- **Checking a cut:** open a cut recording and the places where the cut joined what it
  kept are marked green on the timeline; **‹ Join** / **Join ›** land a few seconds before
  each, with how much was taken out there, so you can play across it.
- **Delete original:** frees the kept original's space before its time is up (in the Cut
  list and the editor); the recording stays cut but can't be undone after. Another
  program's `.edl` that no longer matched the cut file is kept beside the original and
  goes with it. With JellyDVR Space, moving or deleting a recording takes its kept
  original along, and Undo still works after a move.
- **Picture size:** **Fit the window** (default) keeps the frames, controls and timeline
  in view below the picture; or Small, Medium, Large, or drag the picture's corner.

#### Screenshots

The recordings list, each with a bar showing where its breaks are:

![The recordings list](docs/screenshots/recordings-list.jpg)

The editor, inside a break: the picture at the playhead, the row of frames around it (the
yellow one is where you are; drag the row to look along it), stepping and play controls:

![The editor](docs/screenshots/editor.jpg)

The timeline and the list of breaks, with their exact start and end:

![Timeline and breaks](docs/screenshots/editor-breaks.jpg)

Playing at 2× through the break:

![Playing](docs/screenshots/playing.jpg)

Cutting the commercials out: what it will do, the cut running, and done (with Undo):

![Before cutting](docs/screenshots/cut-confirm.jpg)

![The cut running](docs/screenshots/cut-running.jpg)

![Cut done](docs/screenshots/cut-done.jpg)

## The one file to watch

`ComskipSegmentProvider.cs` is the only file touching the version-sensitive
media-segment API. If it doesn't compile against your server, match its three members to
`MediaBrowser.Controller/MediaSegments/IMediaSegmentProvider.cs` at your server's tag.
Everything else is plain .NET.

## Cutting a release

The repository manifest (`manifest.json`) and the GitHub Release it points to have to
be updated together, or Option A installs above break:

1. Bump `AssemblyVersion`/`FileVersion` in the `.csproj`, `dotnet build -c Release`.
2. Zip just the DLL + `icon.png` (not `meta.json` — the server writes its own from the
   manifest on a repository install) as
   `jellyfin-plugin-comskip-segments_<version>.zip`.
3. `gh release create v<version> <zip>` and note the asset's download URL.
4. In `manifest.json`, append a new entry to `versions` with that `sourceUrl`, the new
   `version`, and the zip's MD5 (`md5sum <zip>`) as `checksum`.
5. Add a line to Revision History below.

## Revision History

| Version | Date | Notes |
|---|---|---|
| 1.3.2.0 | 2026-10-31 | Fixes: a live scan no longer shows an earlier scan's breaks for the same file name; a recording Jellyfin restarted part-way ("Name - 1.ts") gets its live scan and live skip; the web overlay asks for live breaks with the recording being played. The `.edl` beside a moved or newly added video (and its other versions) is imported when it's added, not at the next Media Segment Scan; a folder left with no video once its kept original is deleted is removed. |
| 1.3.1.0 | 2026-10-21 | Check a cut: its joins are marked on the timeline, with **‹ Join / Join ›**. **Delete original** per cut recording and **Delete all originals** with the space they take; originals left without their recording are deleted after a day. Kept originals now go after 7 days by default (*Keep the original file as a backup* is off for new installs; an existing setting is kept). Another program's `.edl` stays with the original after a cut. The editor's picture fits the window (or Small/Medium/Large/drag), with the timeline under the controls. Fixes: frames follow quick jumps; the start lands on the first frame. With JellyDVR Space, a moved recording keeps its Undo. |
| 1.3.0.0 | 2026-10-21 | **Commercial segments dashboard** (admins): list of recordings with their breaks, and an editor to fix breaks frame by frame (wheel/keys, keyframes, previous/next black, mark-in/out), with timeline hover and scrubbing, an overview grid of every keyframe and fast forward/rewind. **Cut commercials out** of a recording with a fast stream copy, in the background, with Undo; resume positions and program start follow the cut. New setting: Cut rounding. Edited breaks survive Jellyfin re-reading the file. |
| 1.2.0.2 | 2026-10-20 | Web overlay works behind a Base URL (it used absolute /ComskipSegments/ paths); **Install web overlay** button and status on the settings page, with the one-time permission command when Jellyfin can't change index.html; the plugin puts the overlay back by itself after a jellyfin-web update when it may (setting, on by default). Older patches show as "older version": press Update web overlay or re-run Web/install-overlay.sh. |
| 1.2.0.1 | 2026-10-19 | Fix: Jellyfin could crash while Comskip ran (its output and error output were written into one buffer from two threads at once). Same fix for live scans and audio trims. |
| 1.2.0.0 | 2026-10-10 | **Install Comskip** button (self-contained Comskip 0.83.001 for Linux x86-64/arm64, built from the GPL-2.0 source) and a clear "Comskip isn't installed" message instead of a silent failure; program start from remembered channel cards (skip marker or trim, in the background with progress, Undo trim for 7 days); recording edits limited to chosen users with a confirmation step; Remove commercials keeps them removed; web item menu follows the item's state. Re-run Web/install-overlay.sh for the web features. |
| 1.1.0.0 | 2026-09-28 | Skip commercials in recordings still in progress (web + TV forks), web commercial badges, "Watch from start" for programmes being recorded, use existing .edl files, AudioTrim writes MPEG-TS for .mpg, menu and live-scan fixes. Re-run Web/install-overlay.sh for the web features. |
| 1.0.1.0 | 2026-09-12 | Version bump only, no code changes. |
| 1.0.0.0 | 2026-09-12 | Initial public release. |

## Known follow-ups (not yet built)

- Duration sanity check (compare EDL end vs. `ffprobe` duration) to flag truncated
  recordings distinctly from clean short ones.
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

GPL-2.0-or-later: you can redistribute and change it under the terms of the GNU General Public
License, version 2 or (at your option) any later version. See [LICENSE](LICENSE).

The official source and releases are at <https://github.com/mackandall/jellyfin-plugin-comskip-segments>.
Releases up to 1.3.1.0 were published under the MIT licence.
