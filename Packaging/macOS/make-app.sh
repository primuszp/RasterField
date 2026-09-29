#!/usr/bin/env bash
# Wraps a `dotnet publish` output folder for osx-x64 / osx-arm64 into RasterField.app:
#   Contents/MacOS/      the published files (single-file executable + palette folder)
#   Contents/Resources/  RasterField.icns (Dock, Finder, Launchpad icon)
#   Contents/Info.plist  bundle metadata (identifier, version, icon, document types)
#
# usage: Packaging/macOS/make-app.sh <publish-dir> <output-dir> [version]
set -euo pipefail
PUBLISH_DIR=${1:?publish dir}
OUT_DIR=${2:?output dir}
VERSION=${3:-1.0.0}
VERSION=${VERSION#v}
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

APP="$OUT_DIR/RasterField.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/RasterField"
cp "$ROOT/Source/RasterField/Assets/RasterField.icns" "$APP/Contents/Resources/RasterField.icns"
sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
echo "Created $APP"
