# Working agreement for the macOS feature and UI pass

Several engineers work on the macOS app in parallel, each in their own git worktree on a
branch off `next`. This file is the contract between them. `To Do.md` is the feature list;
`docs/windows-later.md` is out of scope (do not touch Windows).

## The product, in one paragraph

Mosaic is a free, privacy-first macOS browser (Swift, SwiftUI with AppKit where needed,
SF Symbols, Liquid Glass) with **two real engines**: Chromium (CEF 158, embedded in-process)
and Mozilla Gecko (a private Firefox ESR run as a companion process). Its defining feature
is **Mosaics**: presets that transform the whole interface into a Zen/Arc, Chrome,
Opera/Opera GX or Safari-like layout without reloading pages or losing data. It has a
built-in uBlock Origin-based blocker, converts Chrome extensions to run on Gecko, and has
no telemetry. The owner's complaint about the current UI: *not ergonomic; settings must be
inside the main window; crashes; the layout is not easy for humans.* Design principles:
minimal, native, fast, accessible, keyboard-friendly, content first. No placeholder
features, no dead buttons, no decorative-only settings. Where something cannot be done,
say so in the UI and the README instead of faking it.

## How the app works today (read before designing)

- `Sources/Mosaic/App/`: `main.swift` (AppKit lifecycle, because Chromium owns the event
  loop), `AppDelegate.swift` (window, menus), `Automation.swift` (scripted control).
- `Sources/Mosaic/Model/`: `BrowserModel` (tabs, navigation, engine switching, mirroring
  Gecko's tabs), `BrowserTab`, `ExtensionManager`, `ContentBlocker`, `TabOrganization`.
- `Sources/Mosaic/Engines/GeckoEngine.swift`: launches and drives Gecko; `AppPaths`.
- `Sources/Mosaic/Views/`: the current UI (to be replaced by the shell).
- `Sources/CEFBridge/`: Objective-C++ bridge to CEF (`MosaicCEF`, `MCEFBrowserView`).
- `Resources/gecko/agent.sys.mjs`: the agent loaded inside Gecko (tabs, navigation,
  blocker, extensions). **Shared byte-for-byte with the Windows app**: keep changes
  platform-neutral or guarded with `IS_MAC`, and never break existing methods.
- `Sources/MosaicCore/`: engine-independent logic, unit-tested (`Tests/MosaicCoreTests`).

**The Gecko page is a separate window.** Mosaic's window is transparent over the content
area of a Gecko tab; the chromeless Gecko window sits exactly behind it (see
`ContentRectReporter`, `GeckoHole`, `BrowserModel.setContentRect/updateDocking`). Consequences
for every layout:
- The content area's screen rectangle must keep being reported whenever it changes (layout
  switches, sidebars opening, split view, full screen). Break this and Mozilla pages appear
  in the wrong place.
- Anything Mosaic draws over the content area appears **on top of** the Gecko page (Mosaic's
  window is in front), and takes the clicks there. Overlays (find bar, popovers, palette)
  work. A full-page Mosaic view over a Gecko tab (settings, start page) must make
  `showsGeckoPage` false or draw opaquely, so the page does not show through.
- Chromium pages are an `NSView` inside Mosaic's window (`MCEFBrowserView`); SwiftUI overlays
  draw above them.

## Ownership

Each area owns its folder outright. **Do not edit files you do not own**, except the shared
hub files under the rules below.

| Area | Owns | Settings pages |
|---|---|---|
| **shell** | `Sources/Mosaic/Shell/**`, `Sources/Mosaic/Views/**` (may delete/replace), `Sources/Mosaic/Settings/**` except `SettingsPaneView.swift`, window setup and menu *structure* in `AppDelegate.swift` | Mosaics, General, Accessibility |
| **tabs** | `Sources/Mosaic/Features/Tabs/**`, `Sources/MosaicCore/Tabs/**`, `Model/TabOrganization.swift`; primary owner of tab behaviour in `BrowserModel.swift`/`BrowserTab.swift` | Tabs & Workspaces, Performance |
| **library** | `Features/Library/**`, `Sources/MosaicCore/Library/**` | Search |
| **engine** | `Features/Engine/**`; primary owner of `Sources/CEFBridge/**`, `Resources/gecko/agent.sys.mjs`, `Engines/GeckoEngine.swift` | — |
| **downloads** | `Features/Downloads/**`, `Sources/MosaicCore/Downloads/**` | Downloads |
| **privacy** | `Features/Privacy/**`, `Sources/MosaicCore/Blocker/**`, `Model/ContentBlocker.swift`, `Views/BlockerPopover.swift`, `Resources/blocker/**`, `Sources/MosaicCore/Privacy/**` | Privacy & Security |
| **passwords** | `Features/Passwords/**`, `Sources/MosaicCore/Passwords/**` | Passwords & Autofill |
| **productivity** | `Features/Productivity/**`, `Sources/MosaicCore/Productivity/**` | Shortcuts & Gestures |
| **platform** | `Features/Platform/**`, `Model/ExtensionManager.swift`, `Views/ExtensionsSheet.swift`, `Sources/MosaicCore/Gecko/GeckoRuntime.swift` (runtime install), `Sources/MosaicCore/Platform/**` | Engine, Extensions, Languages, About & Updates |
| **integrator** | `SettingsPaneView.swift`, `To Do.md`, `docs/**`, `Package.swift`, `Scripts/**`, `README.md` | — |

Tests go in `Tests/MosaicCoreTests/<Area>Tests.swift` (one file or folder per area).

### Shared hub files

These are touched by several areas. Rules:
- **Additive and minimal.** Add what you need; do not reformat, reorder, rename or "tidy"
  existing code. Put substantial logic in your own files (Swift extensions such as
  `BrowserModel+Downloads.swift` in your folder, separate `.mm` files for C++ where the
  build allows) and keep the hub edit to a hook.
- `BrowserModel.swift`: service objects go under `// MARK: Feature services`, one line each.
- `BrowserTab.swift`, `AppDelegate.swift` (menu items), `Automation.swift` (commands and
  `dump` fields for your feature), `CEFBridge.mm`/`.h`, `agent.sys.mjs`, `GeckoEngine.swift`:
  additive edits in your own section, with a comment naming your area.
- The integrator merges the branches and resolves the conflicts that additive edits still
  cause, so keep hunks small and self-explanatory.

### Contract scaffolding (already in the tree)

Placeholders exist so everyone compiles against the same names from the start. Owners
replace them; **none may survive into the release**.
- Settings pages: `*SettingsPane` stubs in each owner's folder, showing
  `SettingsPanePending`. `SettingsPaneView` maps categories to them.
- Mount points the shell places: `BookmarksBarView` (library), `DownloadsToolbarButton`
  (downloads), `FindBar` (engine), `CommandPaletteOverlay` and `StartPageContent`
  (productivity). The shell must place each where it belongs in every Mosaic.
- `Library` (library): `suggestions(for:)`, `recordVisit(url:title:)`, `isBookmarked(_:)`,
  `toggleBookmark(url:title:)`. The shell uses only these.
- `BrowserTab.isPinned / isMuted / workspaceID / groupID`, `BrowserModel.workspaces /
  selectedWorkspaceID / groups`, `Workspace`, `TabGroup` (tabs owns behaviour; shell draws).
- `BrowserModel.settingsCategory`, `openSettings(_:)`, `closeSettings()` (shell renders).
Owners may add members. Changing or removing a contract member needs the integrator.

## Building, testing, running

```sh
Scripts/build.sh release      # build/Mosaic.app (uses Xcode's toolchain automatically)
Scripts/test.sh               # unit tests (Swift Testing)
```

SwiftUI macros are available now (`@State`, `@Observable`, `#Preview`), and so are Liquid
Glass APIs (`glassEffect`, `GlassEffectContainer`; macOS 26+ SDK). Existing code using
`State(initialValue:)` was a workaround for the old toolchain; you may modernise code you own.

**Run the app only on an isolated profile, in automation mode:**

```sh
export MOSAIC_DATA_DIR="$TMPDIR/mosaic-<area>-$$"     # your own throw-away profile
export MOSAIC_AUTOMATION_ID="<area>"                   # your own automation channel
build/Mosaic.app/Contents/MacOS/Mosaic --mosaic-automation &
swift Scripts/drive.swift dump /tmp/state.json         # see Automation.swift for commands
swift Scripts/drive.swift quit
```

`MOSAIC_DATA_DIR` gives the instance its own data folder and preferences and lets it run
beside other copies; it reuses the installed Gecko runtime read-only. In automation mode
Mosaic never takes keyboard focus. Window screenshots: `screencapture -x -o -l <windowNumber>`
(the number is in `dump`). The Gecko page is a separate window behind Mosaic's, so a
window capture of Mosaic shows that area empty; capture the screen region instead.
`MOSAIC_AUTOMATION_ID` must be set for both the app and `drive.swift`; without it your
commands would reach every other engineer's running copy.

## Ground rules for this machine

The owner is using this Mac while you work.
- Work only inside your worktree. Never modify the integration checkout (`mosaic/`, next to the
  `mosaic-<area>` worktrees), other worktrees, `~/Library/Application Support/Mosaic`, the user's
  preferences, or `/Applications/Mosaic.app` (the owner's other build; leave it running).
- Read-only references you may consult: the owner's earlier Mosaic project (a separate checkout,
  SwiftUI; good design ideas, especially `Mac/Mosaic/Mosaic/Components` and `Shared/`), and the
  Eminence clone in the session's scratchpad.
- No git commands that change anything (the integrator commits). No installing software
  (Homebrew, npm globals). Downloads into your worktree or a temp folder are fine.
- Never kill a process you did not start. Quit every Mosaic and Firefox you start.
- Do not steal focus or open dialogs on the owner's screen beyond your automation runs.

## Quality bar

- Builds with no warnings in code you touched; `Scripts/test.sh` passes.
- Core logic (parsing, stores, rules, models) has unit tests.
- Every feature you call finished was exercised in the running app, in both engines where
  it applies, by script on an isolated profile. Say exactly what you ran.
- No crashes: launch, use your feature, switch Mosaic/engine if relevant, quit.
- Accessibility labels on controls; keyboard reachable; respects reduced motion/transparency.
- Honest limits: if an engine cannot do something, the UI says so.

## Final report (all the integrator sees)

1. What you built, file by file at a high level.
2. Which `To Do.md` items are finished (with how each was verified) and which are not.
3. Test results (counts) and what you ran in the app.
4. Every hub-file edit you made (file + what + why), so merges can be checked.
5. What you need from other areas or the integrator (exact names/signatures).
6. Known limitations and risks.
