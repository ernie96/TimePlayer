#!/bin/sh
# Packages target/release/TimePlayer as target/release/TimePlayer-x86_64.AppImage, the file TimePlayer's
# updater on Linux looks for. GTK 3 and WebKitGTK 4.1 come from the system, as on any desktop that runs Orca.
# With LINUXDEPLOY set, the build machine's mpv goes in too, with its libraries, as usr/bin/mpv-bundled:
# TimePlayer uses the system's mpv when there is one, and this copy when there isn't.
# Run from v2/ with APPIMAGETOOL set to the appimagetool AppImage.
set -eu
release=target/release
appdir="$release/TimePlayer.AppDir"
here=$(dirname "$0")

rm -rf "$appdir"
mkdir -p "$appdir/usr/bin"
cp "$release/TimePlayer" "$appdir/usr/bin/TimePlayer"
cp "$here/timeplayer.desktop" "$appdir/timeplayer.desktop"
cp "$here/timeplayer.svg" "$appdir/timeplayer.svg"
ln -s usr/bin/TimePlayer "$appdir/AppRun"
if [ -n "${LINUXDEPLOY:-}" ]; then
    "$LINUXDEPLOY" --appimage-extract-and-run --appdir "$appdir" --executable "$(command -v mpv)"
    # Its libraries stay in usr/lib, found through mpv's own runpath ($ORIGIN/../lib).
    mv "$appdir/usr/bin/mpv" "$appdir/usr/bin/mpv-bundled"
    # It starts, and finds its libraries.
    "$appdir/usr/bin/mpv-bundled" --version | head -n 1
else
    echo "No LINUXDEPLOY, so this AppImage has no mpv of its own and needs the system's."
fi

ARCH=x86_64 "$APPIMAGETOOL" --appimage-extract-and-run "$appdir" "$release/TimePlayer-x86_64.AppImage"
echo "Built $release/TimePlayer-x86_64.AppImage"
