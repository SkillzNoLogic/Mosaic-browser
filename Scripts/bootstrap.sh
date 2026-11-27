#!/bin/bash
# Fetches the two engine runtimes Mosaic needs. Safe to re-run; finished steps are skipped.
#
#   Chromium -> Chromium Embedded Framework (CEF), unpacked into Vendor/cef and
#               linked into the app at build time.
#   Gecko    -> Firefox ESR, installed privately into
#               ~/Library/Application Support/Mosaic/Engines/Firefox.app
#               (your normal Firefox, if any, is never touched).
#
# Usage: Scripts/bootstrap.sh [--cef-only | --gecko-only]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VENDOR="$ROOT/Vendor"
ENGINES="$HOME/Library/Application Support/Mosaic/Engines"

CEF_VERSION="158.0.6+g23f1736+chromium-158.0.8127.89"
CEF_SHA1="63faae7ccd6a497430c06d26b17c4cac20dca387"
CEF_NAME="cef_binary_${CEF_VERSION}_macosarm64_minimal"

GECKO_URL="https://download.mozilla.org/?product=firefox-esr-latest-ssl&os=osx&lang=en-US"

if [[ "$(uname -m)" != "arm64" ]]; then
  echo "Mosaic only supports Apple Silicon (arm64) Macs." >&2
  exit 1
fi

want_cef=1
want_gecko=1
case "${1:-}" in
  --cef-only) want_gecko=0 ;;
  --gecko-only) want_cef=0 ;;
  "") ;;
  *) echo "unknown option: $1" >&2; exit 2 ;;
esac

fetch_cef() {
  local dest="$VENDOR/cef"
  if [[ -f "$dest/.mosaic-version" && "$(cat "$dest/.mosaic-version")" == "$CEF_VERSION" ]]; then
    echo "==> CEF $CEF_VERSION already unpacked"
  else
    mkdir -p "$VENDOR/downloads"
    local archive="$VENDOR/downloads/$CEF_NAME.tar.bz2"
    local url="https://cef-builds.spotifycdn.com/${CEF_NAME//+/%2B}.tar.bz2"
    if [[ ! -f "$archive" ]] || [[ "$(shasum -a 1 "$archive" | cut -d' ' -f1)" != "$CEF_SHA1" ]]; then
      echo "==> Downloading CEF $CEF_VERSION (~135 MB)"
      curl -fL --retry 3 -C - -o "$archive" "$url"
    fi
    echo "==> Verifying checksum"
    echo "$CEF_SHA1  $archive" | shasum -a 1 -c -
    echo "==> Unpacking"
    rm -rf "$dest" "$VENDOR/$CEF_NAME"
    tar -xjf "$archive" -C "$VENDOR"
    mv "$VENDOR/$CEF_NAME" "$dest"
    echo "$CEF_VERSION" > "$dest/.mosaic-version"
  fi

  if [[ -f "$dest/build/libcef_dll_wrapper/libcef_dll_wrapper.a" ]]; then
    echo "==> libcef_dll_wrapper already built"
  else
    echo "==> Building libcef_dll_wrapper"
    cmake -S "$dest" -B "$dest/build" -G "Unix Makefiles" \
      -DPROJECT_ARCH=arm64 -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_OSX_ARCHITECTURES=arm64 > "$dest/build-configure.log" 2>&1 \
      || { tail -40 "$dest/build-configure.log"; exit 1; }
    make -C "$dest/build" -j"$(sysctl -n hw.ncpu)" libcef_dll_wrapper > "$dest/build-make.log" 2>&1 \
      || { tail -60 "$dest/build-make.log"; exit 1; }
  fi
  echo "==> CEF ready: $dest"
}

fetch_gecko() {
  local app="$ENGINES/Firefox.app"
  if [[ -x "$app/Contents/MacOS/firefox" ]]; then
    echo "==> Gecko runtime already installed: $app"
    return
  fi
  mkdir -p "$ENGINES" "$VENDOR/downloads"
  local dmg="$VENDOR/downloads/firefox-esr.dmg"
  echo "==> Downloading Firefox ESR (Gecko runtime)"
  curl -fL --retry 3 -o "$dmg" "$GECKO_URL"
  local mount
  mount="$(mktemp -d)"
  echo "==> Installing into $ENGINES"
  hdiutil attach -nobrowse -readonly -quiet -mountpoint "$mount" "$dmg"
  trap 'hdiutil detach -quiet "$mount" 2>/dev/null || true' EXIT
  local src
  src="$(find "$mount" -maxdepth 1 -name '*.app' | head -1)"
  [[ -n "$src" ]] || { echo "no .app inside the Firefox disk image" >&2; exit 1; }
  rm -rf "$app"
  cp -R "$src" "$app"
  hdiutil detach -quiet "$mount"
  trap - EXIT
  echo "==> Gecko ready: $app"
}

[[ $want_cef == 1 ]] && fetch_cef
[[ $want_gecko == 1 ]] && fetch_gecko
echo "Done. Next: Scripts/build.sh"
