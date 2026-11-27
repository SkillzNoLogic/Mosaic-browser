#!/bin/bash
# Fetches the two engine runtimes Mosaic needs. Safe to re-run; finished steps are skipped.
#
#   Chromium -> Chromium Embedded Framework (CEF), unpacked into Vendor/cef and
#               linked into the app at build time.
#   Gecko    -> Firefox ESR, installed privately into
#               ~/Library/Application Support/Mosaic/Engines/Firefox.app
#               (your normal Firefox, if any, is never touched).
#
#   Blocker  -> uBlock Origin's source at a pinned release, from which its
#               network filtering engine is bundled into Vendor/ublock, plus a
#               snapshot of uBO's default filter lists. Needs esbuild
#               (`brew install esbuild`).
#
# Usage: Scripts/bootstrap.sh [--cef-only | --gecko-only | --blocker-only]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VENDOR="$ROOT/Vendor"
ENGINES="$HOME/Library/Application Support/Mosaic/Engines"

CEF_VERSION="158.0.6+g23f1736+chromium-158.0.8127.89"
CEF_SHA1="63faae7ccd6a497430c06d26b17c4cac20dca387"
CEF_NAME="cef_binary_${CEF_VERSION}_macosarm64_minimal"

GECKO_URL="https://download.mozilla.org/?product=firefox-esr-latest-ssl&os=osx&lang=en-US"

UBO_VERSION="1.77.2"
UBO_SHA256="470b6d6338ba3402a74d8412026871b0ec54ba45f29147270d932dd6a72e800e"

if [[ "$(uname -m)" != "arm64" ]]; then
  echo "Mosaic only supports Apple Silicon (arm64) Macs." >&2
  exit 1
fi

want_cef=1
want_gecko=1
want_blocker=1
case "${1:-}" in
  --cef-only) want_gecko=0; want_blocker=0 ;;
  --gecko-only) want_cef=0; want_blocker=0 ;;
  --blocker-only) want_cef=0; want_gecko=0 ;;
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

# Builds Mosaic's content-blocking engine from uBlock Origin's source.
#
# The engine is uBO's own static network filtering code. Resources/blocker/engine.js
# is Mosaic's entry point for it (the counterpart of uBO's platform/nodejs/index.js);
# esbuild bundles the two into one script that loads both in JavaScriptCore and
# in Gecko. One uBO module is substituted: see Resources/blocker/publicsuffix-static.js.
fetch_blocker() {
  local dest="$VENDOR/ublock"
  local source="$VENDOR/ublock-src"
  command -v esbuild >/dev/null || { echo "esbuild is required: brew install esbuild" >&2; exit 1; }

  if [[ ! -f "$source/.mosaic-version" || "$(cat "$source/.mosaic-version")" != "$UBO_VERSION" ]]; then
    mkdir -p "$VENDOR/downloads"
    local archive="$VENDOR/downloads/uBlock-$UBO_VERSION.tar.gz"
    if [[ ! -f "$archive" ]] || ! echo "$UBO_SHA256  $archive" | shasum -a 256 -c - >/dev/null 2>&1; then
      echo "==> Downloading uBlock Origin $UBO_VERSION source"
      curl -fL --retry 3 -o "$archive" "https://github.com/gorhill/uBlock/archive/refs/tags/$UBO_VERSION.tar.gz"
    fi
    echo "$UBO_SHA256  $archive" | shasum -a 256 -c -
    rm -rf "$source"
    mkdir -p "$source"
    tar -xzf "$archive" -C "$source" --strip-components 1
    echo "$UBO_VERSION" > "$source/.mosaic-version"
  fi

  echo "==> Bundling the filtering engine"
  rm -rf "$dest/src"
  mkdir -p "$dest/src" "$dest/lists"
  cp -R "$source/src/js" "$source/src/lib" "$dest/src/"
  cp "$ROOT/Resources/blocker/publicsuffix-static.js" "$dest/src/js/publicsuffix.js"
  cp "$source/LICENSE.txt" "$dest/LICENSE-uBlock-Origin.txt"
  local banner="/* Mosaic content blocker. Contains uBlock Origin $UBO_VERSION's network filtering engine,
 * Copyright (C) 2014-present Raymond Hill and contributors, https://github.com/gorhill/uBlock
 * Licensed under the GNU General Public License, version 3 or later; see LICENSE-uBlock-Origin.txt.
 * Built by Scripts/bootstrap.sh from Resources/blocker/engine.js. Do not edit. */"
  (cd "$ROOT" && esbuild Resources/blocker/engine.js --bundle --format=iife \
      --global-name=MosaicBlocker --alias:ublock=./Vendor/ublock/src \
      --define:__UBO_VERSION__="\"$UBO_VERSION\"" --target=es2022 --charset=utf8 \
      --banner:js="$banner" --log-level=warning --outfile=Vendor/ublock/ublock-engine.js)

  # A snapshot of uBO's default lists, shipped in the app so blocking works on
  # first launch. The app refreshes them itself afterwards. Keep in step with
  # FilterListCatalog in Sources/MosaicCore/Blocker/FilterLists.swift.
  echo "==> Fetching filter lists"
  fetch_list() {
    curl -fsSL --retry 3 -m 120 -o "$dest/lists/$1.part" "$2" && mv "$dest/lists/$1.part" "$dest/lists/$1" \
      || { rm -f "$dest/lists/$1.part"; echo "    could not fetch $1" >&2; [[ -f "$dest/lists/$1" ]] || exit 1; }
  }
  local uassets="https://ublockorigin.github.io/uAssets"
  fetch_list public_suffix_list.dat "https://publicsuffix.org/list/public_suffix_list.dat"
  fetch_list ublock-filters.txt     "$uassets/filters/filters.txt"
  fetch_list ublock-badware.txt     "$uassets/filters/badware.txt"
  fetch_list ublock-privacy.txt     "$uassets/filters/privacy.txt"
  fetch_list ublock-unbreak.txt     "$uassets/filters/unbreak.txt"
  fetch_list ublock-quick-fixes.txt "$uassets/filters/quick-fixes.txt"
  fetch_list easylist.txt           "$uassets/thirdparties/easylist.txt"
  fetch_list easyprivacy.txt        "$uassets/thirdparties/easyprivacy.txt"
  fetch_list urlhaus-1.txt          "https://malware-filter.gitlab.io/urlhaus-filter/urlhaus-filter-vivaldi-online.txt"
  fetch_list plowe-0.txt            "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=1&mimetype=plaintext"
  echo "==> Content blocker ready: $dest"
}

[[ $want_cef == 1 ]] && fetch_cef
[[ $want_gecko == 1 ]] && fetch_gecko
[[ $want_blocker == 1 ]] && fetch_blocker
echo "Done. Next: Scripts/build.sh"
