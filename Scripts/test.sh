#!/bin/bash
# Runs the unit tests on Xcode's toolchain. Extra arguments go to `swift test`
# (for example: Scripts/test.sh --filter BlockerTests).
set -euo pipefail
cd "$(dirname "$0")/.."

# Xcode's toolchain: SwiftUI's macros (@State, @Observable, #Preview) and the Liquid Glass
# APIs need it; the Command Line Tools alone do not ship the macro plug-ins.
if [[ -z "${DEVELOPER_DIR:-}" && -d /Applications/Xcode.app ]]; then
  export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi
if [[ "${DEVELOPER_DIR:-$(xcode-select -p)}" == */CommandLineTools ]]; then
  echo "Mosaic needs Xcode (27 or later), not just the Command Line Tools: install Xcode, or set DEVELOPER_DIR." >&2
  exit 1
fi

swift test "$@"
