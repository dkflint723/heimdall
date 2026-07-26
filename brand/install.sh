#!/usr/bin/env bash
# Installs the Heimdall icon set and desktop entry for the current user.
# Nothing here needs root: everything lands under ~/.local/share.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
share="${XDG_DATA_HOME:-$HOME/.local/share}"

# The binary the desktop entry points at. Adjust if you publish elsewhere.
binary="${1:-$HOME/dev/rove/src/Heimdall.Ui/bin/Release/net10.0/linux-x64/publish/Heimdall.Ui}"

if [ ! -x "$binary" ]; then
  echo "warning: $binary is not executable yet — the entry will still install," >&2
  echo "         but publish first or pass the path as an argument."          >&2
fi

# Icons, preserving the hicolor layout so the desktop picks the right size.
find "$here/icons" -name '*.svg' -print0 | while IFS= read -r -d '' src; do
  rel="${src#"$here/icons/"}"
  install -Dm644 "$src" "$share/icons/$rel"
done

# A launcher wrapper, so the desktop entry does not hardcode a build path that
# changes every time the project is rebuilt somewhere else.
install -d "$HOME/.local/bin"
printf '#!/bin/sh\nexec "%s" "$@"\n' "$binary" > "$HOME/.local/bin/heimdall"
chmod +x "$HOME/.local/bin/heimdall"

install -Dm644 "$here/heimdall.desktop" "$share/applications/heimdall.desktop"

# Refresh the caches; both are best-effort and absent on some systems.
gtk-update-icon-cache -f "$share/icons/hicolor" 2>/dev/null || true
update-desktop-database "$share/applications" 2>/dev/null || true

echo "installed:"
echo "  icons    $share/icons/hicolor/*/apps/heimdall.svg"
echo "  launcher $HOME/.local/bin/heimdall"
echo "  entry    $share/applications/heimdall.desktop"
echo
echo "If it does not appear in the menu, check that ~/.local/bin is on PATH."
