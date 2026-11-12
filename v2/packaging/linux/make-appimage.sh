#!/bin/sh
# Packages target/release/TimePlayer as target/release/TimePlayer-x86_64.AppImage, the file TimePlayer's
# updater on Linux looks for. GTK 3, WebKitGTK 4.1 and mpv come from the system, as on any desktop that runs
# Orca. Run from v2/ with APPIMAGETOOL set to the appimagetool AppImage.
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

ARCH=x86_64 "$APPIMAGETOOL" --appimage-extract-and-run "$appdir" "$release/TimePlayer-x86_64.AppImage"
echo "Built $release/TimePlayer-x86_64.AppImage"
