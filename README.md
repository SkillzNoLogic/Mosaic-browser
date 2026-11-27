# Mosaic

A two-engine web browser for Apple Silicon Macs, written in Swift and SwiftUI.

- **Two rendering engines, one window.** Every tab runs on either **Mozilla (Gecko)** or
  **Chromium (Blink)**. A toolbar switch moves the current tab to the other engine and
  reloads it there. Mozilla is the default.
- **Switchable search engines.** Google, DuckDuckGo, Bing, Brave Search, Startpage, Ecosia
  and Kagi, picked from the toolbar or cycled with ⌘⇧S.
- **Chrome extensions on the Mozilla engine.** Mosaic downloads a Chrome extension's `.crx`
  (from a Chrome Web Store link, an extension ID, or a file), converts it, and runs it on
  Gecko.
- **Built-in ad and tracker blocker.** uBlock Origin's filtering engine, compiled into the
  browser from uBO's source (not installed as an extension), with uBO's default filter
  lists. On by default, in both engines; switch it off globally or per site from the shield
  in the toolbar.

Status: **v0.1, an early prototype.** The pieces above work; a lot of ordinary browser
features (bookmarks, history, downloads UI, settings) do not exist yet. Read
[Known limitations](#known-limitations) before relying on it.

Requirements: Apple Silicon Mac, macOS 14 or later. Intel Macs are not supported.

## Build and run

Only the Xcode Command Line Tools are needed (`xcode-select --install`); full Xcode is
optional. CMake and esbuild are needed once, to build Chromium's wrapper library and the
content blocker (`brew install cmake esbuild`).

```sh
Scripts/bootstrap.sh     # one time: fetches both engines and the blocker (~300 MB download)
Scripts/build.sh         # builds build/Mosaic.app
open build/Mosaic.app
```

`bootstrap.sh` downloads:

| Engine | What | Where it goes |
| --- | --- | --- |
| Chromium | Chromium Embedded Framework (CEF) 158, checksum-verified | `Vendor/cef/` (git-ignored), copied into the app at build time |
| Gecko | Firefox ESR, from mozilla.org | `~/Library/Application Support/Mosaic/Engines/Firefox.app` |
| Blocker | uBlock Origin 1.77.2 source (checksum-verified), bundled with esbuild, plus a snapshot of uBO's default filter lists | `Vendor/ublock/` (git-ignored), copied into the app at build time |

The Gecko runtime is a private copy with its own profile. Mosaic never reads or modifies a
Firefox you already have. If the private copy is missing, Mosaic falls back to a Gecko
browser found in `/Applications` (Firefox, Firefox Developer Edition, Nightly, LibreWolf,
Zen), still with its own separate profile.

Run the tests with `Scripts/test.sh`.

## Using it

| Action | How |
| --- | --- |
| Open a page or search | Type in the address bar. Things that look like addresses are opened; anything else is searched. |
| Change search engine | Toolbar menu, or ⌘⇧S to cycle |
| Switch the tab's engine | **Mozilla / Chromium** switch in the toolbar, or ⌘⇧E |
| Tabs | ⌘T new, ⌘W close, ⌘⇧] / ⌘⇧[ next / previous |
| Navigate | ⌘[ back, ⌘] forward, ⌘R reload, ⌘L address bar |
| Extensions | Puzzle-piece button, or ⌘⇧X |
| Content blocker | Shield button (shows the count blocked on the page), or ⌘⇧B to switch it on/off |

The coloured dot on each tab shows its engine: orange for Mozilla, blue for Chromium. New
tabs open in the engine of the tab you are on. The engine you last picked with the switch
is the one Mosaic starts with next time.

### Installing a Chrome extension

Either:

- open the extension's page on the Chrome Web Store in a tab and press **Add to Mosaic** in
  the toolbar, or
- open Extensions and paste a Web Store link or extension ID, or choose a `.crx` file.

The extension is listed with any compatibility notes. **Open Popup** and **Options** open
the extension's own pages in a tab.

## How it works

```
┌────────────────────────── Mosaic.app (Swift / SwiftUI) ───────────────────────────┐
│  BrowserModel ── tabs, search engine, engine switching                            │
│     │                                                                             │
│     ├─ Chromium tab ─► MCEFBrowserView (CEFBridge, Objective-C++) ─► CEF, in-process│
│     │                                                                             │
│     └─ Gecko tab ───► GeckoEngine ─► RDPClient ─► unix socket ─► agent.sys.mjs    │
│                                                         (inside the Gecko process)│
│  ExtensionManager ─► ExtensionStore: .crx → unpack → ManifestConverter → Gecko    │
└───────────────────────────────────────────────────────────────────────────────────┘
```

### Chromium

Chromium is embedded with the Chromium Embedded Framework. `Sources/CEFBridge` is a small
Objective-C++ layer that hides CEF's C++ API behind two Objective-C classes Swift can use:
`MosaicCEF` (process lifecycle) and `MCEFBrowserView` (an `NSView` hosting one browser).
Renderer, GPU and utility processes run from the `Mosaic Helper*.app` bundles inside the
app. Because Chromium has to own the application class and the event loop, Mosaic uses an
AppKit lifecycle (`main.swift`, `MosaicApplication`) with SwiftUI views inside.

### Gecko

**Gecko cannot be embedded in another app on macOS.** Mozilla does not ship an embedding
API for the desktop (GeckoView is Android-only), so there is no Gecko equivalent of
`WKWebView` or CEF. Mosaic's approach:

1. It runs a private Gecko runtime as a companion process, with a dedicated profile.
2. It loads an agent, [`Resources/gecko/agent.sys.mjs`](Resources/gecko/agent.sys.mjs), into
   Gecko's parent process over the Firefox remote debugging protocol, on a Unix socket in
   the user's private temporary directory. The agent removes all of Firefox's own UI and
   makes the window borderless.
3. Mosaic's own window is transparent in the content area of a Gecko tab. The chromeless
   Gecko window is kept at exactly that rectangle, so the page shows through and receives
   the clicks, while Mosaic's tab strip and toolbar frame it. Moving or resizing Mosaic
   moves Gecko with it.
4. Mosaic polls the agent a few times a second for tab state (URL, title, loading, history)
   and mirrors it. Tabs that Gecko opens itself (links with `target=_blank`, extension
   pages) are adopted as Mosaic tabs.

The result looks and behaves like one window, but it is two, and that shows at the edges;
see the limitations below. The agent is the only place that touches Firefox internals,
which is why Firefox ESR (stable internals for a year) is the default runtime.

### Chrome extensions on Gecko

Gecko and Chromium both implement the WebExtensions API, so most of an extension runs
unchanged. `Sources/MosaicCore/Extensions` handles the differences:

- **Unpacking.** `CRXArchive` reads CRX2 and CRX3 packages (and plain ZIPs), and recovers
  the extension ID from the signed header or public key.
- **Manifest conversion.** `ManifestConverter` assigns a Gecko add-on ID, turns an MV3
  background service worker into a Gecko event page (hoisting its `importScripts()` into
  the script list), rewrites keys Gecko spells differently, and removes Chrome-only
  permissions.
- **Runtime shim.** A small script loaded first in the background context supplies the
  service-worker globals an event page lacks, and replaces Chrome-only API namespaces
  (`chrome.sidePanel`, `chrome.offscreen`, …) with inert stubs, so a call to one degrades to
  a no-op instead of crashing the extension's background script.
- **Honest reporting.** Anything Gecko genuinely cannot do is recorded as a compatibility
  note (info / warning / blocker) and shown next to the extension.

Converted extensions are unsigned, so they are loaded as temporary add-ons each time Gecko
starts. Their storage persists because each keeps a stable ID (`<chrome id>@crx.mosaic`).

### Built-in content blocker

The blocker is part of the browser, not an extension. `Resources/blocker/engine.js` imports
uBlock Origin's own static network filtering engine (`static-net-filtering.js` and its
dependencies, the same modules uBO's Node.js build uses) straight from uBO's source;
`bootstrap.sh` bundles them into one script, `ublock-engine.js`. One uBO module is
substituted (`publicsuffix-static.js`, which removes a top-level `await` the bundle format
cannot use), and `polyfills.js` supplies the few web APIs a bare JavaScript engine lacks.
The bundle then runs in two places, so both engines make identical decisions from the same
lists:

- **Chromium tabs:** a JavaScriptCore context inside Mosaic (`BlockerEngine`). CEF asks
  `RequestFilter` before every sub-resource request, on its network thread.
- **Mozilla tabs:** a sandbox in Gecko's parent process, loaded by the agent
  (`configureBlocker`), which cancels matching requests from an `http-on-modify-request`
  observer.

A page's own document is never blocked, and non-web pages (`about:`, `file:`, extension
pages) are never filtered. The filter lists ship in the app, so blocking works offline from
the first launch; Mosaic refreshes them in the background when they are more than two days
old, or on demand with **Update Lists**. A download that does not look like a filter list
never replaces a working one. Settings live in Mosaic's preferences (`blockerEnabled`,
`blockerAllowlist`).

To toggle it from source rather than the UI, change the default in `ContentBlocker.init`
(`defaults.object(forKey: Keys.enabled) as? Bool ?? true`), or leave `Vendor/ublock` out of
the build: `build.sh` then builds the app without the blocker and the shield says so.

## Known limitations

**Extensions**

- Not every Chrome extension can work on Gecko. Extensions built on Chrome-only APIs
  (`debugger`, `offscreen`, `sidePanel`, `tabCapture`, `desktopCapture`, Google sign-in via
  `identity.getAuthToken`, …) will be partly or wholly broken; Mosaic tells you which.
- There is no toolbar button or popup bubble for extensions yet. Popups open as a tab.
- Extensions run on the Mozilla engine only. Chromium tabs do not load them: CEF's embedded
  ("Alloy") browsers have no extension support.
- Extensions do not update themselves. Reinstall to update.
- Tested end to end with one extension so far (Dark Reader, MV3).

**Content blocker**

- Network blocking only. uBO's cosmetic filtering (hiding leftover ad containers) and
  scriptlet injection (which defeats many anti-adblock scripts and some video ads) need
  content scripts and are not implemented, so pages can show empty ad slots and some ads
  still get through. Expect it to block noticeably less than the uBO extension does.
- uBO's redirect filters (`$redirect`, replacing a blocked script with a harmless stub),
  `$removeparam`, `$csp` and other modifiers are not applied; matching requests are simply
  blocked or let through.
- No per-request logger, element picker, custom filters or list selection yet.
- Blocking applies to requests a tab makes. Service-worker traffic in Mozilla tabs may not
  be attributed to its tab and is then let through.

**The Gecko window**

- It is a second app. A "Firefox" icon appears in the Dock, and when a Gecko page has
  keyboard focus the menu bar is Firefox's. ⌘L, ⌘T, ⌘N, ⌘W and ⌘⇧E are forwarded to Mosaic;
  other shortcuts act on Gecko directly.
- While dragging or resizing Mosaic's window, the page follows a moment behind.
- Full screen is disabled, and moving Mosaic to another Space does not take the Gecko
  window along.
- If another app's window ends up between the two, Mosaic re-stacks them when it becomes
  active. This path has had little testing.

**Security and privacy (prototype shortcuts, to fix before any release)**

- Chromium runs without its sandbox and with a mock keychain (cookies are not protected by
  the login keychain).
- The app is ad-hoc signed; it runs on the Mac that built it. Distribution needs a
  Developer ID signature and notarisation.
- Gecko runs with remote debugging enabled on a socket only your user can reach. Any
  process running as you could drive it.
- The private Firefox does not auto-update; re-run `Scripts/bootstrap.sh` after deleting
  `~/Library/Application Support/Mosaic/Engines` to refresh it.

**Missing browser features:** bookmarks, history UI, downloads UI, find in page, private
windows, settings, session restore, multiple windows, password management, default-browser
registration.

## Project layout

```
Package.swift
Scripts/
  bootstrap.sh        fetch both engines
  build.sh            build build/Mosaic.app
  test.sh             run the unit tests
  make-icon.swift     regenerate Resources/AppIcon.icns
  drive.swift         send automation commands to a running Mosaic
Resources/
  Info.plist, HelperInfo.plist, AppIcon.icns
  gecko/agent.sys.mjs Mosaic's agent inside Gecko
Sources/
  MosaicCore/         engine-independent logic, unit tested
    Search/           search engines, address-bar parsing
    Extensions/       CRX parsing, manifest conversion, extension library
    Gecko/            remote debugging client, runtime lookup, profile
  CEFBridge/          Objective-C++ wrapper around CEF
  MosaicHelper/       Chromium sub-process entry point
  Mosaic/             the app: model, engines, SwiftUI views
Tests/MosaicCoreTests/
```

Mosaic's data lives in `~/Library/Application Support/Mosaic/` (`Chromium/`, `Gecko/Profile/`,
`Extensions/`, `Engines/`). Delete that folder to reset everything.

## Development notes

**Building without Xcode.** On the macOS 27 SDK, SwiftUI's `@State` is a macro whose
compiler plug-in ships only with Xcode. So that the project builds with just the Command
Line Tools, views declare `State` storage directly (`private var x = State(initialValue:)`)
instead of using `@State`. Keep to that pattern, or the CLT-only build breaks. For the same
reason, run tests through `Scripts/test.sh`: a bare `swift test` intermittently fails under
the Command Line Tools with "plugin for module 'TestingMacros' not found".

**Scripted checks.** Launching with `--mosaic-automation` lets you drive the app from the
shell, which is how the end-to-end behaviour was verified:

```sh
build/Mosaic.app/Contents/MacOS/Mosaic --mosaic-automation &
swift Scripts/drive.swift navigate example.com
swift Scripts/drive.swift engine chromium
swift Scripts/drive.swift installExtension eimadpbcbfnmbkopoojfekhnkhdbieeh
swift Scripts/drive.swift dump /tmp/mosaic-state.json
```

Commands are listed in `Sources/Mosaic/App/Automation.swift`. In this mode Mosaic never
takes keyboard focus on its own.

## Third-party software

Mosaic bundles the Chromium Embedded Framework (BSD licence; Chromium's own licences are in
`Vendor/cef/CREDITS.html` after bootstrap) and runs an unmodified Firefox ESR (MPL 2.0).

The content blocker contains uBlock Origin's filtering engine, Copyright (C) Raymond Hill
and contributors, licensed under the **GNU GPL v3** (its licence is copied into the app as
`blocker/LICENSE-uBlock-Origin.txt`). Distributing a Mosaic build that includes it means
complying with the GPL for that distribution, which in practice means releasing Mosaic's
own source under a GPL-compatible licence. The filter lists have their own licences (GPL v3,
CC BY-SA 3.0 and others; see each list's header).
Firefox is a trademark of the Mozilla Foundation; Mosaic is not affiliated with Mozilla or
Google.
