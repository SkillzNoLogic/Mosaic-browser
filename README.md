# Mosaic

A privacy-first, two-engine web browser for Apple Silicon Macs, written in Swift and SwiftUI.

- **Two real rendering engines, one window.** Every tab runs on **Mozilla (Gecko)** or
  **Chromium (Blink)**. Switch a tab's engine from the toolbar; the page stays on screen
  until the other engine has loaded it. Per-site rules can pick the engine automatically.
- **Mosaics.** Presets that transform the whole interface into a Zen/Arc, Chrome, Opera
  (with an Opera GX variant) or Safari-like layout, instantly and without reloading pages.
  Every preset is editable: drag toolbar and sidebar elements around on a layout canvas,
  design backgrounds in the Gradient Studio, save and share named configurations.
- **Built-in ad and tracker blocker.** uBlock Origin's filtering engine, compiled into the
  browser from uBO's source (not installed as an extension), in both engines: network
  blocking, cosmetic filtering, your own filters, a per-page "what was blocked and why".
- **Chrome extensions on the Mozilla engine.** Install from the Chrome Web Store or a `.crx`
  file; Mosaic converts them to run on Gecko and says exactly what does not carry over.
- **Everything a browser needs, inside the window.** Settings (14 categories, searchable),
  bookmarks, history, reading list, downloads (with Free Download Manager hand-off),
  passwords in the macOS Keychain, workspaces, tab groups, session restore, find, zoom,
  print/PDF, screenshots, reader mode, a command palette, private tabs.
- **No telemetry, no account.** Mosaic itself makes only the connections listed in
  Settings › About (filter-list updates, engine install/update checks, extension downloads,
  optional favicons and search suggestions).

Status: **v0.2, pre-release.** Built and verified by scripted runs in both engines on
isolated profiles; see [Known limitations](#known-limitations) and `To Do.md` for what is
partial or missing. Requirements: Apple Silicon Mac, macOS 14 or later (Liquid Glass needs
macOS 26 or later; older systems get solid materials).

## Install a release

Download `Mosaic-<version>-macos-arm64.dmg` from the GitHub release, open it and drag
Mosaic onto the Applications folder (a `.zip` of the same app is attached too). The app is not yet signed with a Developer ID, so macOS
blocks the first launch: right-click › **Open**, or run
`xattr -dr com.apple.quarantine /Applications/Mosaic.app`.

The Mozilla engine is installed from inside the app (Settings › Engine, or the prompt in a
Mozilla tab): Mosaic downloads Firefox ESR from Mozilla, checks that it is signed by
Mozilla, and keeps it as a private copy that never touches a Firefox you already have.
Until then Mosaic browses with Chromium.

## Build from source

Needs **Xcode 27** (SwiftUI's macros and the Liquid Glass APIs ship with Xcode, not with
the Command Line Tools alone), plus CMake and esbuild once (`brew install cmake esbuild`).

```sh
Scripts/bootstrap.sh     # once: Chromium (CEF), the private Firefox ESR, the blocker engine
Scripts/build.sh         # builds build/Mosaic.app
Scripts/test.sh          # unit tests
Scripts/release.sh 0.2.0 # tests, release build, .dmg and .zip for a GitHub release
```

| Piece | What `bootstrap.sh` fetches | Where it goes |
| --- | --- | --- |
| Chromium | Chromium Embedded Framework (CEF) 158, checksum-verified | `Vendor/cef/` (git-ignored), copied into the app |
| Gecko | Firefox ESR, from mozilla.org | `~/Library/Application Support/Mosaic/Engines/Firefox.app` |
| Blocker | uBlock Origin 1.77.2 source (checksum-verified), bundled with esbuild, plus uBO's default filter lists | `Vendor/ublock/` (git-ignored), copied into the app |

## Using it

| Action | How |
| --- | --- |
| Open a page or search | Type in the address bar (keywords work: `w term` searches Wikipedia) |
| Command palette | ⌘K: every action, open tabs, bookmarks, history, settings |
| Settings | ⌘, (inside the window; Esc closes) |
| Switch Mosaic | Settings › Mosaics, View menu (⌃⌘1–4), or the palette |
| Switch the tab's engine | Mozilla / Chromium control in the toolbar, or ⇧⌘E |
| Tabs | ⌘T new, ⌘W close, ⇧⌘T reopen closed, ⇧⌘A search tabs, ⌘1–9, ⇧⌘N private tab |
| Workspaces | Sidebar switcher or Tab menu; ⌃⌘N new, ⌃⌘← / ⌃⌘→ switch |
| Navigate | ⌘[ / ⌘] back/forward, ⌘R reload, ⌘L address bar, ⇧⌘H home |
| Find, zoom | ⌘F / ⌘G, ⌘+ / ⌘− / ⌘0 (zoom remembered per site) |
| Bookmarks | ⌘D bookmark, ⇧⌘B bookmarks bar, ⌥⌘B manager, ⌘Y history, ⇧⌘D reading list |
| Downloads | ⌥⌘L |
| Reader view | ⇧⌘R |
| Passwords | ⌘\ fill; Settings › Passwords & Autofill |
| Content blocker | Shield in the toolbar, or ⌥⇧⌘B |

All shortcuts can be changed in Settings › Shortcuts & Gestures. The coloured dot on each
tab shows its engine (orange Mozilla, blue Chromium; purple marks a private tab).

## How it works

```
┌──────────────────────────── Mosaic.app (Swift / SwiftUI) ─────────────────────────────┐
│ ShellView ← MosaicLayout (regions × components, appearance)  ← canvas, Gradient Studio │
│ BrowserModel: tabs, workspaces, groups, sessions, engine switching, feature services   │
│   ├─ Chromium tab ─► MCEFBrowserView (CEFBridge, Objective-C++) ─► CEF, in-process      │
│   └─ Gecko tab ───► GeckoEngine ─► RDPClient ─► unix socket ─► agent.sys.mjs (in Gecko) │
│ Services: library (SQLite), downloads, passwords (Keychain), privacy, blocker,          │
│           productivity (palette, reader, shortcuts), platform (engines, rules, updates) │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

### Interface: the composed shell and Mosaics

The window is composed from a `MosaicLayout` (`Sources/MosaicCore/Shell`): regions (title
bar, toolbar, tab bar, sidebar top/middle/bottom, rail) each hold an ordered list of
components (back, forward, reload, address field, tab strip, downloads, blocker, …), plus tab
placement, sidebar behaviour, density, corner radius, accent, light/dark, Liquid Glass or
solid materials, and a background. The four presets are starting layouts; the layout canvas
(Settings › Mosaics › Customise Layout) edits them by drag and drop or keyboard, with undo.
Switching layouts re-parents Chromium views and re-docks the Gecko window, so no page
reloads. Settings, bookmarks, history and downloads are drawn inside the window (internal
pages at `mosaic://…`).

### Chromium

Embedded with the Chromium Embedded Framework. `Sources/CEFBridge` hides CEF's C++ API
behind Objective-C classes Swift can use (`MosaicCEF`, `MCEFBrowserView`), split into files
per feature (downloads, passwords, privacy, page features, page scripts). Renderer, GPU and
utility processes run from the `Mosaic Helper*.app` bundles. Chromium owns the event loop, so
Mosaic uses an AppKit lifecycle with SwiftUI views inside.

### Gecko

**Gecko cannot be embedded in another app on macOS** (Mozilla ships no desktop embedding
API). Mosaic runs a private Firefox ESR as a companion process with its own profile, loads
an agent ([`Resources/gecko/agent.sys.mjs`](Resources/gecko/agent.sys.mjs)) into its parent
process over the remote debugging protocol on a private Unix socket, removes all of
Firefox's UI, and keeps the chromeless Gecko window exactly behind a transparent hole in
Mosaic's window. Anything Mosaic draws over a Gecko page appears on top of it. The agent
mirrors tabs, and implements Gecko's side of the blocker, downloads, passwords, privacy,
find, zoom, printing, permissions, reader view and shortcuts. It is shared byte-for-byte
with the Windows port (paused; see `docs/windows-later.md`).

### Chrome extensions on Gecko

`Sources/MosaicCore/Extensions` unpacks CRX2/CRX3 packages, converts the manifest (Gecko
add-on ID, MV3 service worker → event page with its `importScripts()` hoisted, renamed keys,
Chrome-only permissions removed), loads a small runtime shim that stubs Chrome-only APIs,
and records a compatibility note for anything Gecko cannot do.

### Built-in content blocker

`Resources/blocker/engine.js` imports uBlock Origin's own static network filtering engine
and parser from uBO's source; `bootstrap.sh` bundles it into `ublock-engine.js`, which runs in
JavaScriptCore for Chromium tabs (CEF asks before every sub-resource request) and in a
sandbox inside Gecko for Mozilla tabs, so both engines decide identically. Cosmetic filters
are compiled with uBO's parser and injected as CSS. Filter lists ship in the app and refresh
in the background.

## Known limitations

**Engines and windows**
- The Gecko page is a separate window: a "Firefox" icon appears in the Dock, and while a
  Gecko page has the keyboard the menu bar is Firefox's (Mosaic's main shortcuts are
  forwarded). The page follows a moment behind when the window is dragged.
- Only one Gecko page can be visible at a time, so there is no split view, and only one
  Mosaic window (multiple windows would need per-window Gecko windows).
- Page full screen was not verified, and Mosaic's own window full screen is disabled.
- Private tabs work in Chromium only (Gecko would need a second, private window).

**Features**
- Extension popups open as a tab, not a panel under the toolbar; extensions do not run in
  Chromium tabs (CEF Alloy has no extension support) and do not update themselves.
- The blocker does not run uBO's scriptlets, procedural filters or `$redirect`/`$csp`
  modifiers, so it blocks less than the uBO extension does.
- Clearing site data in Chromium honours time ranges for cookies only.
- Chromium tabs lose their back/forward history when unloaded to save memory, and Chromium
  cannot show web notifications.
- In Gecko, microphone streams were not verified; Firefox's own screen-sharing prompt
  cannot appear.
- Text-only zoom, mouse gestures, translation, AI features, sync and the proxy browser are
  not built (see `To Do.md`).

**Security and distribution (to fix before a public release)**
- The app is ad-hoc signed (no Developer ID, not notarised); macOS asks again for Keychain
  access to saved passwords after each new build.
- Chromium runs without its sandbox.
- Gecko is driven over a remote-debugging socket that only your user can reach; any
  process running as you could drive it.
- Mosaic itself has no licence yet; see [Third-party software](#third-party-software).

## Project layout

```
Package.swift
Scripts/        bootstrap, build, test, release, drive (automation), make-icon
Resources/      Info.plist, icons, gecko/ (agent and Gecko-side modules), blocker/, reader/
Sources/
  MosaicCore/   engine-independent logic, unit-tested: Search, Extensions, Gecko, Blocker,
                Shell, Canvas, Tabs, Library, Downloads, Passwords, Privacy, Productivity,
                Platform, Permissions, Engine
  CEFBridge/    Objective-C++ bridge to CEF
  MosaicHelper/ Chromium sub-process entry point
  Mosaic/       the app: App, Model, Engines, Shell, Views, Settings, Features/<area>
Tests/MosaicCoreTests/
docs/           AGENTS.md (how the parallel work was organised), windows-later.md, screenshots/
To Do.md        the feature list, crossed off as items are verified
```

Mosaic's data lives in `~/Library/Application Support/Mosaic/`; passwords are in the
login Keychain. Delete the folder to reset everything else.

## Development notes

**Isolated test copies.** `MOSAIC_DATA_DIR=<folder>` runs Mosaic on its own data folder and
preferences, beside your normal copy; `MOSAIC_AUTOMATION_ID=<name>` gives it its own
automation channel. Launched with `--mosaic-automation`, Mosaic can be driven from the shell
and never takes focus:

```sh
export MOSAIC_DATA_DIR=$TMPDIR/mosaic-test MOSAIC_AUTOMATION_ID=test
build/Mosaic.app/Contents/MacOS/Mosaic --mosaic-automation &
swift Scripts/drive.swift navigate example.com
swift Scripts/drive.swift mosaic chrome
swift Scripts/drive.swift dump /tmp/mosaic-state.json
swift Scripts/drive.swift quit
```

Each feature area adds its own commands and `dump` fields (see `Sources/Mosaic/App/Automation.swift`
and the `*Automation.swift` files).

## Third-party software

- **Chromium Embedded Framework** (BSD) and Chromium (its licences: `chrome://credits`).
- **Firefox ESR** (MPL 2.0), run unmodified as a separate program.
- **uBlock Origin**'s filtering engine, Copyright (C) Raymond Hill and contributors,
  **GNU GPL v3** (licence copied into the app as `blocker/LICENSE-uBlock-Origin.txt`). Filter
  lists have their own licences (see each list's header). Distributing a build that includes
  the blocker means complying with the GPL, which in practice means releasing Mosaic's own
  source under a GPL-compatible licence.
- **Readability.js** 0.6.0 (Apache 2.0), Mozilla, for Chromium reader view (`Resources/reader/`).
- Tracking-parameter list from Firefox's query-stripping list.

Firefox is a trademark of the Mozilla Foundation; Chrome and Chromium of Google; Arc, Zen,
Opera and Safari of their owners. Mosaic is not affiliated with any of them; its presets are
interface patterns, not copies of their code or assets.
