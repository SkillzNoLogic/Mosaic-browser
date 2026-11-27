#!/bin/bash
# Builds build/Mosaic.app (Apple Silicon only).
#
# Usage: Scripts/build.sh [debug|release]      (default: release)
#
# Xcode is not required; the Command Line Tools are enough.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIG="${1:-release}"
VERSION="0.1.0"
BUILD_NUMBER="$(date +%Y%m%d%H%M)"
CEF="$ROOT/Vendor/cef"
APP="$ROOT/build/Mosaic.app"

if [[ "$(uname -m)" != "arm64" ]]; then
  echo "Mosaic only supports Apple Silicon (arm64) Macs." >&2
  exit 1
fi
if [[ ! -f "$CEF/build/libcef_dll_wrapper/libcef_dll_wrapper.a" ]]; then
  echo "Chromium (CEF) is missing. Run Scripts/bootstrap.sh first." >&2
  exit 1
fi

cd "$ROOT"
echo "==> Compiling ($CONFIG)"
swift build -c "$CONFIG" --arch arm64
BIN="$(swift build -c "$CONFIG" --arch arm64 --show-bin-path)"

fill_plist() {  # template destination name suffix
  sed -e "s/__VERSION__/$VERSION/g" -e "s/__BUILD__/$BUILD_NUMBER/g" \
      -e "s/__NAME__/$3/g" -e "s/__SUFFIX__/$4/g" "$1" > "$2"
}

echo "==> Assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/gecko" "$APP/Contents/Frameworks"
cp "$BIN/Mosaic" "$APP/Contents/MacOS/Mosaic"
fill_plist "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist" "Mosaic" ""
cp "$ROOT/Resources/gecko/"* "$APP/Contents/Resources/gecko/"
[[ -f "$ROOT/Resources/AppIcon.icns" ]] && cp "$ROOT/Resources/AppIcon.icns" "$APP/Contents/Resources/"

# Built-in content blocker: uBlock Origin's engine (bundled from source by
# bootstrap.sh), a snapshot of its filter lists for first launch, and its licence.
BLOCKER="$ROOT/Vendor/ublock"
if [[ -f "$BLOCKER/ublock-engine.js" ]]; then
  mkdir -p "$APP/Contents/Resources/blocker/lists"
  cp "$BLOCKER/ublock-engine.js" "$BLOCKER/LICENSE-uBlock-Origin.txt" "$APP/Contents/Resources/blocker/"
  cp "$BLOCKER/lists/"* "$APP/Contents/Resources/blocker/lists/"
else
  echo "warning: content blocker not bundled (run Scripts/bootstrap.sh); building without it." >&2
fi

# Chromium: the framework plus one helper app per sub-process type. The names
# and locations are what CEF looks for; do not rename them.
# -c clones on APFS, so the ~300 MB framework costs no time or disk.
cp -Rc "$CEF/Release/Chromium Embedded Framework.framework" "$APP/Contents/Frameworks/"
helper() {  # name-suffix bundle-id-suffix
  local name="Mosaic Helper$1"
  local bundle="$APP/Contents/Frameworks/$name.app"
  mkdir -p "$bundle/Contents/MacOS"
  cp "$BIN/MosaicHelper" "$bundle/Contents/MacOS/$name"
  fill_plist "$ROOT/Resources/HelperInfo.plist" "$bundle/Contents/Info.plist" "$name" "$2"
}
helper "" ""
helper " (Alerts)" ".alerts"
helper " (GPU)" ".gpu"
helper " (Plugin)" ".plugin"
helper " (Renderer)" ".renderer"

# Ad-hoc signatures: enough to run on this Mac. Distribution needs a Developer ID.
echo "==> Signing (ad-hoc)"
sign() { codesign --force --sign - --timestamp=none "$@" 2>&1 | grep -v "replacing existing signature" || true; }
sign --deep "$APP/Contents/Frameworks/Chromium Embedded Framework.framework"
for bundle in "$APP/Contents/Frameworks/"*.app; do sign "$bundle"; done
sign "$APP"
codesign --verify --strict "$APP"

echo "Built $APP"
echo "Run it with: open \"$APP\""
