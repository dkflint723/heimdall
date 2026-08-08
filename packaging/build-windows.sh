#!/usr/bin/env bash
#
# Builds the Windows installer on a local machine, the same way build.yml builds
# it on the runner. For when CI is not available — an exhausted Actions budget,
# an offline machine, or just wanting to try a change before tagging it.
#
# Run it from anywhere, in Git Bash or any bash on Windows:
#
#     packaging/build-windows.sh 0.5.2
#
# The version is required and is stamped into the binary, the installer's
# filename and its Add/Remove Programs entry. There is no default on purpose:
# every local build that guessed one would claim to be a release that does not
# exist, and `heimdall --version` is the thing people use to work out which copy
# they are running.
#
# The output is dist/heimdall-<version>-win-x64-setup.exe with a .sha256 beside
# it, which is exactly what the release page carries.
#
set -euo pipefail

VERSION="${1:-}"

if [[ -z "$VERSION" ]]; then
    echo "usage: packaging/build-windows.sh <version>" >&2
    echo >&2
    echo "  e.g. packaging/build-windows.sh 0.5.2" >&2
    echo >&2
    echo "  Pick something ABOVE whatever you have installed, or Windows treats" >&2
    echo "  the install as a downgrade. The most recent tag is:" >&2
    echo "      $(git -C "$(dirname "${BASH_SOURCE[0]}")/.." describe --tags --abbrev=0 2>/dev/null || echo "unknown")" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# **NativeAOT links with MSVC, and finds it by shelling out to vswhere.**
#
# This is the single reason a local Windows build fails when CI's does not. The
# runner image has vswhere on PATH; a normal Visual Studio install puts it in a
# fixed location and leaves PATH alone. Without it the ILCompiler targets emit
#
#     'vswhere.exe' is not recognized as an internal or external command
#     ... link.exe @obj/.../link.rsp" exited with code 123
#
# and the eye goes to link.exe, which is present and fine. Prepending the
# directory here costs nothing when it is already on PATH.
VSWHERE_DIR="/c/Program Files (x86)/Microsoft Visual Studio/Installer"

if [[ -x "$VSWHERE_DIR/vswhere.exe" ]]; then
    export PATH="$VSWHERE_DIR:$PATH"
elif ! command -v vswhere.exe >/dev/null 2>&1; then
    echo "vswhere.exe not found, and NativeAOT needs it to locate the MSVC linker." >&2
    echo "Install Visual Studio or the Build Tools with the C++ workload:" >&2
    echo "    winget install Microsoft.VisualStudio.2022.BuildTools" >&2
    exit 1
fi

# Searched rather than assumed, and across major versions. build.yml hardcodes
# the 6.x path because the runner image pins it; a real machine has whatever the
# person installed, and 7 lives somewhere 6 never did. Newest first.
ISCC=""
for candidate in \
    "/c/Program Files (x86)/Inno Setup 7/ISCC.exe" \
    "/c/Program Files/Inno Setup 7/ISCC.exe" \
    "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" \
    "/c/Program Files/Inno Setup 6/ISCC.exe"
do
    [[ -x "$candidate" ]] && { ISCC="$candidate"; break; }
done

if [[ -z "$ISCC" ]]; then
    echo "Inno Setup not found in any of the usual places." >&2
    echo "The script needs 6.3 or newer — heimdall.iss uses ArchitecturesAllowed=x64compatible." >&2
    echo "    winget install JRSoftware.InnoSetup" >&2
    exit 1
fi

echo "==> publishing $VERSION (NativeAOT, win-x64)"

dotnet publish src/Heimdall.Ui -c Release -r win-x64 \
    -p:PublishAot=true -p:Version="$VERSION"

PAYLOAD="src/Heimdall.Ui/bin/Release/net10.0/win-x64/publish"

# The same check build.yml makes, for the same reason: NativeAOT does not
# produce a single file. SkiaSharp, HarfBuzz and the ANGLE runtime sit beside
# the executable and are loaded from its own directory, so an installer packaged
# without them produces something that aborts before it draws anything — and it
# aborts at the user's machine, not here.
for required in Heimdall.Ui.exe libSkiaSharp.dll libHarfBuzzSharp.dll av_libglesv2.dll; do
    [[ -f "$PAYLOAD/$required" ]] || {
        echo "$required missing from the publish" >&2
        exit 1
    }
done

echo "==> compiling the installer"

# Logged for the same reason build.yml logs it: if the compile fails on
# ArchitecturesAllowed, this line is what explains why.
"$ISCC" | head -2 || true

# Doubled slashes: bash on Windows rewrites a leading /D into a path before the
# compiler ever sees it. cygpath for the payload, because ISCC is a native
# program and cannot read a /c/... path.
"$ISCC" //DAppVersion="$VERSION" //DPayload="$(cygpath -w "$ROOT/$PAYLOAD")" \
    packaging/heimdall.iss

SETUP="heimdall-$VERSION-win-x64-setup.exe"
[[ -f "dist/$SETUP" ]] || { echo "dist/$SETUP was not produced" >&2; exit 1; }

# Hashed from inside dist/ so the file records a bare filename. Done from the
# root it would say "dist/heimdall-...exe", which cannot be checked anywhere the
# installer is actually downloaded to.
( cd dist && sha256sum "$SETUP" > "$SETUP.sha256" )

echo
echo "==> dist/$SETUP"
echo "    $(cd dist && cut -d' ' -f1 < "$SETUP.sha256")"
