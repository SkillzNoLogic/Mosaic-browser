# Mosaic for Windows — To Do

The Windows feature pass: everything the macOS app does (see the repository's `To Do.md`
and `README.md`), on Windows 10/11, x64 and ARM64, in **C# with a WinUI 3** interface,
plus the Windows-only items from `docs/windows-later.md`.

Finished items are crossed off (`- [x] ~~…~~`) with how they were verified. "CI" means
observed on a GitHub Actions Windows runner (x64, and ARM64 where it says so) through the
automation pipe or a screenshot artifact; "unit" means unit tests on any OS; "compiled"
alone does not count as done. Each area edits only its own section.

Legend: `[ ]` open · `[~]` partly done (what is missing is stated) · `[x]` done ·
*deferred* = parked on purpose, with the reason.

## winui — the app, window and engines
- [ ] WinUI 3 app replaces the WPF app; UI written in C# code (no XAML files), builds on macOS, runs on Windows
- [ ] Parity with the v0.1 WPF app: tabs, both engines, engine switching, search engines, extensions, blocker, automation pipe and smoke test
- [ ] Gecko embedded as a child window of the WinUI window; hidden whenever Mosaic draws over the page (settings, internal pages, overlays that would be covered)
- [ ] Chromium tabs on WinUI's WebView2 control, one shared environment
- [ ] Native title bar area with caption buttons, Snap Layouts on the maximise button, drag regions
- [ ] Mica (window) and Acrylic (transient surfaces) with solid fallbacks (transparency off, Windows 10, battery saver)
- [ ] Per-monitor DPI, resize/maximise/minimise, restores size and position, fits 1024×768 and 1366×768
- [ ] Single instance; opening a URL from outside goes to the running window
- [ ] Self-contained publish for win-x64 and win-arm64; smoke test on both CI architectures

## shell — Mosaics and the composed window
- [ ] Layout model shared with macOS (regions × components, appearance), same JSON
- [ ] Mosaic: Zen / Arc (vertical sidebar, pinned grid, workspaces)
- [ ] Mosaic: Chrome (tab strip in the title bar, omnibox row, bookmarks bar)
- [ ] Mosaic: Opera / Opera GX
- [ ] Mosaic: **Microsoft Edge** (Windows only): Edge-style tabs and toolbar, horizontal and vertical tab modes, Edge-like address bar, Edge-style sidebar, rounded tabs and menus
- [ ] No Safari Mosaic on Windows (macOS only), and layouts imported from macOS that use it fall back cleanly
- [ ] Switching Mosaics is instant: no restart, no page reload
- [ ] Settings inside the main window, all 14 categories, searchable; Mosaics, General and Accessibility pages
- [ ] Accent colour, light/dark/system, density, corner radius, materials toggle (Mica/Acrylic ↔ solid)
- [ ] Keyboard access everywhere; UI Automation names on every control; high contrast

## tabs — tabs, workspaces, sessions
- [ ] Pin, duplicate, mute; recently closed (Ctrl+Shift+T); tab search (Ctrl+Shift+A)
- [ ] Tab groups; workspaces (create, switch, delete, restore, move tabs)
- [ ] Drag-and-drop reordering; duplicate-tab detection
- [ ] Session save and restore; crash recovery; private tabs never saved
- [ ] Sleeping tabs with exceptions and memory pressure; Tabs & Workspaces and Performance settings

## library — bookmarks, history, search
- [ ] Bookmarks (folders, tags, bar, manager, search); import/export Netscape HTML and Chrome JSON
- [ ] History (searchable, delete by visit/site/range) in both engines
- [ ] Reading list
- [ ] Custom search engines with keywords; search suggestions (off by default); Search settings

## engine — page features
- [ ] Find in page; per-site zoom; print and save as PDF; developer tools (both engines)
- [ ] Screenshots (visible and full page); mute; Picture in Picture; page full screen
- [ ] Mosaic's own permission prompts (camera, microphone, location, notifications, clipboard), remembered per site
- [ ] Pop-up blocking with a notice; JavaScript dialogs; context menus; file pickers

## downloads
- [ ] Download manager for both engines: progress, pause/resume/cancel/retry, history, search
- [ ] Download folder, ask where to save, organisation rules
- [ ] Mark of the Web (Zone.Identifier) on every file; dangerous and misleading file warnings
- [ ] Toast notification and taskbar progress
- [ ] Free Download Manager hand-off on Windows (off / ask / always)
- [ ] Downloads settings

## privacy
- [ ] Private tabs (in-memory profile; nothing on disk)
- [ ] Blocker: cosmetic filtering, My filters with validation, optional lists, per-page dashboard
- [ ] HTTPS-only and the security indicator; tracking-parameter stripping; DNS-over-HTTPS
- [ ] Cookies and site data; clear browsing data with time ranges; third-party cookies; site permissions page
- [ ] Privacy & Security settings

## passwords
- [ ] Passwords stored with Windows Credential Manager / DPAPI; reveal behind Windows Hello
- [ ] Save prompt and autofill in both engines; never in private tabs; cross-origin frames refused
- [ ] Generator; CSV import/export; Passwords & Autofill settings

## productivity
- [~] Command palette (Ctrl+K): Core done and unit-tested (actions, other tabs, settings categories, library search providers, address/search fallbacks, recents, `PaletteRegistry` for other areas, catalogue commands of other areas offered automatically; automation `palette`/`paletteQuery`/`palettePerform`). WinUI overlay (window-sized popup on Acrylic, CoverPage while open) compiles. *Missing:* observed on a Windows runner — blocked until the winui build/M1 lands (CI 46135939810 fails in `Mosaic.App.csproj`'s self-contained setup, like every `win2/*` branch); bookmarks/history and settings appear once library and shell set `PaletteRegistry.RegisterSearch` / `ProductivityIntegration.OpenSettings`
- [~] Customisable shortcuts, applied live, forwarded from Gecko pages: catalogue with Windows defaults (Ctrl+T/W/L, F5 + Ctrl+R, Alt+Left/Right, Ctrl+Tab + Ctrl+Page Down, Ctrl+1…9, F9 reader, F11 full screen, Ctrl+K), conflicts, Windows-reserved and text-editing combos, AltGr rule, persisted in the macOS JSON shape; live `KeyboardAccelerator`s on the window root; Gecko forwarding table pushed on every Gecko start and change (unit-tested, plus a Gecko integration test that runs on the Windows runner once the build passes); agent change for non-Latin layouts (WINNT-guarded). Shortcuts & Gestures settings page compiles. *Missing:* CI observation; winui's own key handler (`Shortcuts.Match`) must give way to these accelerators; real key presses in a WebView2 page need a human check
- [~] Reader view (both engines) with its controls: Gecko through the agent's reader actor with `reader.*` prefs kept in step both ways; Chromium through Readability 0.6.0 (`Resources/reader` shipped as Content in `reader\`) via `IPageScriptHost` on WebView2; availability probing, toolbar component "reader" with font/size/width/spacing/background controls; automation `reader`/`readerSettings` (unit-tested with fakes; scripts checked in V8; Gecko reader integration test written). *Missing:* CI observation
- [~] Speed Dial / new-tab page: pinned sites (add/edit/remove/reorder by drag or menu), top sites hidden/restored, privacy-preserving favicons (site itself, no cookies or referrer, re-encoded as PNG with Windows imaging), `mosaic://newtab` and component "speedDial", same `speed-dial.json` as macOS; automation `speedDial` (unit-tested). *Missing:* CI observation; top sites need the library's `ProductivityIntegration.TopSites`

## platform — engines, extensions, updates
- [ ] Engine settings: Chromium (WebView2) and Gecko cards, versions, status
- [ ] In-app Gecko runtime install/update/remove with Authenticode verification
- [ ] Per-site engine rules; default engine; safe engine switching (keep the page until the other engine has loaded it)
- [ ] Languages for web content in both engines
- [ ] Extensions settings page
- [ ] About & Updates: version, licences (GPL v3 or later), **automatic updates** with Mosaic's Ed25519 release signature (same key and rules as macOS)

## canvas — layout editor and backgrounds
- [ ] Drag-and-drop layout canvas with undo/redo and keyboard editing
- [ ] Gradient Studio (Eminence port) for window, new-tab page and workspace backgrounds
- [ ] Named configurations; export/import/validate; files move between macOS and Windows

## system — Windows integration
- [ ] Fluent icons everywhere (Segoe Fluent Icons; Segoe MDL2 Assets on Windows 10)
- [ ] Set as default browser (registration, URL and file activation)
- [ ] Notifications (app notifications), jump list, taskbar integration
- [ ] Native context menus; drag and drop of links, text and files; virtual desktops behave

## perf — performance (measured, not assumed)
- [ ] Startup time and memory measured on CI, x64 and ARM64, with budgets that fail the build when exceeded
- [ ] Startup path kept lean (lazy services, no blocking work on the UI thread); results in `windows/docs/performance.md`
- [ ] Idle CPU and memory with N tabs measured; sleeping tabs reduce memory

## qa — end-to-end checks
- [ ] Scenario scripts for every area through the automation pipe, run on CI
- [ ] Screenshot gallery artifact: every Mosaic and every settings page, light and dark
- [ ] Crash and hang detection (Windows Error Reporting dumps collected as artifacts)
- [ ] Accessibility scan of the UI Automation tree

## Deferred on purpose
- *CrossWork* — on hold at the owner's request (as on macOS).
- *Optional AI, sync, proxy browser* — deferred on macOS too.
