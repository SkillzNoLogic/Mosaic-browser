#!/bin/bash
# Runs the unit tests. Use this rather than a bare `swift test`.
#
# With only the Command Line Tools installed, SwiftPM does not always pass the
# compiler the location of Swift Testing's macro plug-in, and the build then
# fails with "plugin for module 'TestingMacros' not found". Naming the plug-in
# directory explicitly makes it reliable. With full Xcode selected nothing
# extra is needed.
set -euo pipefail
cd "$(dirname "$0")/.."

extra=()
developer_dir="$(xcode-select -p 2>/dev/null || true)"
plugins="$developer_dir/usr/lib/swift/host/plugins/testing"
if [[ "$developer_dir" == */CommandLineTools && -d "$plugins" ]]; then
  extra=(-Xswiftc -plugin-path -Xswiftc "$plugins")
fi

swift test "${extra[@]}" "$@"
