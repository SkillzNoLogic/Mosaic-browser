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
- [x] ~~WinUI 3 app replaces the WPF app; UI written in C# code (no XAML files), builds on macOS, runs on Windows~~ — `dotnet build windows/Mosaic.slnx` on macOS; CI run 46136897428 runs the published app on x64 and ARM64; `src/Mosaic.App.Wpf` deleted
- [x] ~~Parity with the v0.1 WPF app: tabs, both engines, engine switching, search engines, extensions, blocker, automation pipe and smoke test~~ — CI 46136897428, smoke steps 1–7 on windows-latest (x64) and windows-11-arm (ARM64): Gecko page, switch to Chromium, DuckDuckGo search, MV3 extension installed and loaded, blocker blocks/allows/blocks in both engines, quit leaves no process; screenshots in the `smoke-test-output-*` artifacts
- [x] ~~Gecko embedded as a child window of the WinUI window; hidden whenever Mosaic draws over the page (settings, internal pages, overlays that would be covered)~~ — CI 46136897428 (x64, ARM64): `winui.geckoHostMode` = child, page visible in `1-gecko.png`; hidden under a page cover (`1b`, `coverPage`), under the Extensions sheet, and on a `mosaic://` page (`1c`); owned-window fallback run too (`variant-owned`). The container is a layered child window: WinUI's window has no redirection surface and Gecko's page was blank in a plain one (CI 46136222096)
- [x] ~~Chromium tabs on WinUI's WebView2 control, one shared environment~~ — CI 46136897428 step 2 (page loads, `hasChromiumView`), step 5 (blocker via WebResourceRequested: 4 requests blocked, 0 when exempted)
- [~] Native title bar area with caption buttons, Snap Layouts on the maximise button, drag regions — content extends into the title bar with system caption buttons (screenshots), tabs as passthrough regions (`SetInteractiveTitleBarElements`), `window maximize` on CI; Snap Layouts flyout and dragging need a human with a mouse
- [~] Mica (window) and Acrylic (transient surfaces) with solid fallbacks (transparency off, Windows 10, battery saver) — `MainWindow.Material` (Mica / Mica Alt / Acrylic / solid) with fallbacks; `winui.effectiveMaterial` = Mica on CI; Windows 10 and transparency-off fallbacks never run; Acrylic on transient surfaces is the shell's
- [~] Per-monitor DPI, resize/maximise/minimise, restores size and position, fits 1024×768 and 1366×768 — CI 46136897428 step 6: maximise, restore at 1024×700 with the page area following (`windows-6-1024x700.txt`); default size fits the work area; placement saved in settings.json (unit-tested); mixed-DPI monitors never run
- [~] Single instance; opening a URL from outside goes to the running window — mutex per data folder plus a pipe that hands a second launch's addresses to the running window; compiled, not yet run on CI
- [x] ~~Self-contained publish for win-x64 and win-arm64; smoke test on both CI architectures~~ — CI 46136897428: one job publishes both RIDs (Windows App SDK self-contained), smoke on windows-latest and windows-11-arm

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
- [ ] Command palette (Ctrl+K)
- [ ] Customisable shortcuts, applied live, forwarded from Gecko pages
- [ ] Reader view (both engines) with its controls
- [ ] Speed Dial / new-tab page

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
