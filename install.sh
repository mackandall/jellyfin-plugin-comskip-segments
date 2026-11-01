#!/bin/bash
# Installs the built plugin into Jellyfin and restarts the server (stop, copy, start). Run with sudo.
#
# Installs into "<plugins>/Comskip Commercial Segments_<version>", the folder the plugin catalog
# uses, so a later catalog update replaces it cleanly. The plugin's state lives in
# <data>/comskip-segments, never in a plugin folder: Jellyfin deletes the old plugin folder on
# a catalog update.
set -euxo pipefail
REPO="$(dirname "$(readlink -f "$0")")"
SRC="$REPO/bin/Release/net10.0"
PLUGINS=/var/lib/jellyfin/plugins
VERSION=$(sed -n 's/.*"version": "\([0-9.]*\)".*/\1/p' "$REPO/meta.json")
DEST="$PLUGINS/Comskip Commercial Segments_$VERSION"
# Stop first: overwriting the DLL under a running server makes it fail on shutdown
# ("Bad IL range") because the runtime still reads code from the replaced file.
systemctl stop jellyfin
# Start again even if a copy step fails, so a failed install never leaves Jellyfin down.
trap 'systemctl start jellyfin' EXIT

# Other installed versions, so only this one loads.
for old in "$PLUGINS"/"Comskip Commercial Segments_"*; do
  if [ -d "$old" ] && [ "$old" != "$DEST" ]; then rm -rf -- "$old"; fi
done
# The old manual-install folder also held the state files (moved to <data>/comskip-segments on
# the first start of 1.3.3.0 or later): take out only the plugin's own files, never the state.
rm -f "$PLUGINS/ComskipSegments/Jellyfin.Plugin.ComskipSegments.dll" \
      "$PLUGINS/ComskipSegments/icon.png" "$PLUGINS/ComskipSegments/meta.json"

mkdir -p "$DEST"
cp "$SRC/Jellyfin.Plugin.ComskipSegments.dll" "$SRC/icon.png" "$SRC/meta.json" "$DEST/"
chown -R jellyfin:jellyfin "$DEST"
ls -la "$DEST"
