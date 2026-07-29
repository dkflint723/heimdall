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

cat > "$APPS/heimdall.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Heimdall
Comment=File manager
Exec=heimdall %U
Icon=heimdall
Terminal=false
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
