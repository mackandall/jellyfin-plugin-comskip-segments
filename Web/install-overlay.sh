#!/usr/bin/env bash
# Patches jellyfin-web's index.html to load this plugin's browser-side scripts: the
# commercial-marker seek-bar overlay, and the item-details "Scan for commercials" menu
# entry.
#
# index.html is owned by root (installed by the jellyfin-web package), and the jellyfin
# service account can't write to it — that's why this is a script you run with sudo
# instead of something the plugin does for itself at startup.
#
# Safe to re-run, and safe to re-run after upgrading this plugin even if you already ran
# an older version of this script: each snippet below is independently checked against
# its own marker comment and only inserted if missing, so re-running after a plugin
# update that added a new snippet patches in just the new one, leaving anything already
# installed untouched.
#
# Re-run after every `jellyfin-web` package upgrade too — that package overwrites
# index.html wholesale, which silently drops every patch here. (The plugin's own log
# will warn you when this happens: look for "web patch is missing" in the Jellyfin log.)
#
# Usage: sudo ./install-overlay.sh [path-to-index.html]
#   Default path: /usr/share/jellyfin/web/index.html

set -euo pipefail

WEB_INDEX="${1:-/usr/share/jellyfin/web/index.html}"

if [[ ! -f "$WEB_INDEX" ]]; then
    echo "error: $WEB_INDEX not found" >&2
    exit 1
fi

if [[ ! -w "$(dirname "$WEB_INDEX")" ]] && [[ "$(id -u)" -ne 0 ]]; then
    echo "error: need write access to $(dirname "$WEB_INDEX") — run with sudo" >&2
    exit 1
fi

backed_up=0
ensure_backup() {
    if [[ "$backed_up" -eq 0 ]]; then
        cp "$WEB_INDEX" "$WEB_INDEX.comskip-backup"
        backed_up=1
    fi
}

patch_snippet() {
    local marker_begin="$1"
    local snippet="$2"

    if grep -qF "$marker_begin" "$WEB_INDEX"; then
        echo "Already patched: $marker_begin"
        return 0
    fi

    ensure_backup

    WEB_INDEX="$WEB_INDEX" SNIPPET="$snippet" python3 <<'PYEOF'
import os

path = os.environ["WEB_INDEX"]
snippet = os.environ["SNIPPET"]

with open(path, "r", encoding="utf-8") as f:
    html = f.read()

if "</body>" not in html:
    raise SystemExit("error: no </body> tag found in " + path)

html = html.replace("</body>", snippet + "</body>", 1)

with open(path, "w", encoding="utf-8") as f:
    f.write(html)
PYEOF

    echo "Patched: $marker_begin"
}

patch_snippet "<!-- ComskipSegments:overlay:begin -->" \
'<!-- ComskipSegments:overlay:begin -->
<link rel="stylesheet" href="/ComskipSegments/web/overlay.css">
<script defer src="/ComskipSegments/web/overlay.js"></script>
<!-- ComskipSegments:overlay:end -->
'

patch_snippet "<!-- ComskipSegments:itemmenu:begin -->" \
'<!-- ComskipSegments:itemmenu:begin -->
<script defer src="/ComskipSegments/web/itemmenu.js"></script>
<!-- ComskipSegments:itemmenu:end -->
'

if [[ "$backed_up" -eq 1 ]]; then
    echo "Backup saved at $WEB_INDEX.comskip-backup"
    echo "Reload the Jellyfin web page (hard refresh) to pick up the change."
else
    echo "Nothing to do — already fully patched."
fi
