#!/bin/bash
# Builds a release of Mosaic and packages it as a disk image (drag-to-install) and a zip,
# ready to attach to a GitHub release.
#
# Usage: Scripts/release.sh <version>        e.g. Scripts/release.sh 0.2.0
#
# Output: build/release/Mosaic-<version>-macos-arm64.dmg and .zip, each with a .sha256 file.
# The app is ad-hoc signed (no Developer ID yet), so on another Mac it must be opened with
# right-click › Open the first time, or have its quarantine attribute removed; the release
# notes say so.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:?usage: Scripts/release.sh <version>}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version must look like 1.2.3" >&2; exit 2; }

cd "$ROOT"
echo "==> Tests"
Scripts/test.sh > build/release-tests.log 2>&1 || { tail -30 build/release-tests.log; exit 1; }
grep -E "Test run with .* passed" build/release-tests.log | tail -1

echo "==> Building Mosaic $VERSION"
MOSAIC_VERSION="$VERSION" Scripts/build.sh release

APP="$ROOT/build/Mosaic.app"
[[ -f "$APP/Contents/Resources/blocker/ublock-engine.js" ]] || { echo "the content blocker is missing from the build" >&2; exit 1; }
[[ -d "$APP/Contents/Frameworks/Chromium Embedded Framework.framework" ]] || { echo "Chromium is missing from the build" >&2; exit 1; }
codesign --verify --strict "$APP"

OUT="$ROOT/build/release"
ZIP="$OUT/Mosaic-$VERSION-macos-arm64.zip"
mkdir -p "$OUT"
rm -f "$ZIP" "$ZIP.sha256"
echo "==> Packaging"
# ditto keeps the framework's symlinks and the bundle's metadata intact; plain zip does not.
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
(cd "$OUT" && shasum -a 256 "$(basename "$ZIP")" > "$(basename "$ZIP").sha256")
echo "Packaged $ZIP ($(du -h "$ZIP" | cut -f1))"
cat "$ZIP.sha256"

Scripts/make-dmg.sh "$VERSION"
