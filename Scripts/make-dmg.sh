#!/bin/bash
# Packages build/Mosaic.app as a drag-to-install disk image: the app next to a link to
# /Applications, with Mosaic's icon as the volume icon.
#
# Usage: Scripts/make-dmg.sh <version>       (Scripts/release.sh calls it after building)
#
# Output: build/release/Mosaic-<version>-macos-arm64.dmg and a .sha256 file next to it.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:?usage: Scripts/make-dmg.sh <version>}"
APP="$ROOT/build/Mosaic.app"
OUT="$ROOT/build/release"
DMG="$OUT/Mosaic-$VERSION-macos-arm64.dmg"
VOLUME="Mosaic $VERSION"

[[ -d "$APP" ]] || { echo "build/Mosaic.app is missing; run Scripts/build.sh release first" >&2; exit 1; }
BUILT_VERSION="$(defaults read "$APP/Contents/Info" CFBundleShortVersionString)"
[[ "$BUILT_VERSION" == "$VERSION" ]] || { echo "build/Mosaic.app is version $BUILT_VERSION, not $VERSION" >&2; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$OUT"
rm -f "$DMG" "$DMG.sha256"

echo "==> Staging disk image"
# ditto keeps the framework's symlinks and the bundle's signature intact.
ditto "$APP" "$STAGE/Mosaic.app"
ln -s /Applications "$STAGE/Applications"
ICON="$APP/Contents/Resources/AppIcon.icns"
if [[ -f "$ICON" ]]; then
    cp "$ICON" "$STAGE/.VolumeIcon.icns"
    # Marks the folder as having a custom icon, which hdiutil carries onto the volume root.
    if command -v SetFile >/dev/null; then SetFile -a C "$STAGE"; fi
fi

echo "==> Creating $(basename "$DMG")"
hdiutil create -quiet -volname "$VOLUME" -srcfolder "$STAGE" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$DMG"
hdiutil verify -quiet "$DMG"
(cd "$OUT" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")
echo "Packaged $DMG ($(du -h "$DMG" | cut -f1))"
cat "$DMG.sha256"
