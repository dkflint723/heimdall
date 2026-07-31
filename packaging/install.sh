#!/usr/bin/env bash
#
# Installs Heimdall for the current user. No root, nothing outside $HOME.
#
# Run it from inside the extracted release directory:
#
#     tar -xzf heimdall-linux-x64.tar.gz
#     cd heimdall
#     ./install.sh
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

BIN="$HOME/.local/bin"
LIB="$HOME/.local/lib/heimdall"
APPS="$HOME/.local/share/applications"
ICONS="$HOME/.local/share/icons/hicolor/128x128/apps"

# A system package and this installer do not collide — different prefixes, and
# rpm knows nothing about $HOME — but `~/.local/bin` comes FIRST on PATH, so
# what lands here wins over every future package upgrade. Silently.
if [[ -e /usr/bin/heimdall ]]; then
    echo "note: a system-wide Heimdall is already installed at /usr/bin/heimdall."
    echo "      This installs to ~/.local, which takes precedence on PATH — so"
    echo "      this copy will run instead of the packaged one, including after"
    echo "      you upgrade the package."
    echo "      To use the package instead, stop now and run:"
    echo "          rm -f  ~/.local/bin/heimdall"
    echo "          rm -rf ~/.local/lib/heimdall"
    echo "          rm -f  ~/.local/share/applications/heimdall.desktop"
    echo
    read -r -p "Continue with the user install anyway? [y/N] " reply
    [[ "$reply" =~ ^[Yy] ]] || exit 0
fi

# The whole point of this script. NativeAOT does NOT produce a single file:
# SkiaSharp and HarfBuzz stay beside the executable and are loaded from its own
# directory. Copying out the binary alone gives a program that aborts at startup
# with "Unable to load shared library 'libSkiaSharp'" — which looks like a
# corrupt build rather than a missing file, so check it here and say so plainly.
for required in Heimdall.Ui libSkiaSharp.so; do
    if [[ ! -f "$HERE/$required" ]]; then
        echo "error: $required is missing from $HERE" >&2
        echo "       This directory is not a complete release. Extract the" >&2
        echo "       whole tarball and run install.sh from inside it." >&2
        exit 1
    fi
done

echo "Installing to $LIB"
mkdir -p "$LIB" "$BIN" "$APPS" "$ICONS"

# --delete so an upgrade cannot leave a stale library behind, which would be
# loaded in preference to nothing and fail in confusing ways.
if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete --exclude install.sh "$HERE"/ "$LIB"/
else
    rm -rf "${LIB:?}"/*
    cp -a "$HERE"/. "$LIB"/
    rm -f "$LIB/install.sh"
fi

chmod +x "$LIB/Heimdall.Ui"
ln -sfn "$LIB/Heimdall.Ui" "$BIN/heimdall"

[[ -f "$LIB/heimdall.png" ]] && cp -f "$LIB/heimdall.png" "$ICONS/heimdall.png"

# The symbolic variant if the release carries one. A dark panel wants a
# single-colour glyph, not the full-colour plate.
if [[ -f "$LIB/heimdall-symbolic.svg" ]]; then
    mkdir -p "$HOME/.local/share/icons/hicolor/symbolic/apps"
    cp -f "$LIB/heimdall-symbolic.svg" \
          "$HOME/.local/share/icons/hicolor/symbolic/apps/heimdall-symbolic.svg"
fi

cat > "$APPS/heimdall.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Heimdall
Comment=File manager
Exec=heimdall %U
Icon=heimdall
Terminal=false
# StartupWMClass is a SINGLE STRING, not a semicolon-separated list — the
# desktop-entry spec types it as `string`, and desktop-file-validate accepts a
# list happily because "a;b;" is a valid string that simply matches nothing.
#
# Program.cs sets WM_CLASS to "heimdall" so it already matches this file's
# basename, which is the default association rule and needs no override at all.
# This line only helps a binary built BEFORE that change, where WM_CLASS is
# still the assembly name. Confirm with `xprop WM_CLASS` before trusting it.
StartupWMClass=Heimdall.Ui
Categories=System;FileTools;FileManager;
MimeType=inode/directory;
EOF

command -v update-desktop-database >/dev/null 2>&1 \
    && update-desktop-database "$APPS" 2>/dev/null || true

echo "Installed."
echo "  binary   $LIB/Heimdall.Ui"
echo "  launcher $BIN/heimdall"

# A launcher nobody can run is worse than no launcher, and ~/.local/bin is on
# PATH by default on Fedora but not everywhere.
case ":$PATH:" in
    *":$BIN:"*) echo "  Run: heimdall" ;;
    *) echo
       echo "  NOTE: $BIN is not on your PATH. Either run it directly:"
       echo "      $BIN/heimdall"
       echo "  or add it:"
       echo "      echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.bashrc" ;;
esac
