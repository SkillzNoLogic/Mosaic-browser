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
- [x] ~~Layout model shared with macOS (regions × components, appearance), same JSON~~ — unit: `Shell/ShellGoldenTests` compare byte for byte with golden files generated from the macOS Swift sources (`Golden/generate.swift`): every preset (pretty and compact), GX, a customised layout (unicode, escapes, workspace backgrounds), tab-placement changes, forgiving decoding of messy/wrong-typed/future files, the `mosaics.json` store file; Swift number formatting and key order. CI (Windows x64, run 46136623487): all shell tests pass.
- [~] Mosaic: Zen / Arc (vertical sidebar, pinned grid, workspaces) — layout, plan and view built (sidebar with navigation, compact address field, tab list, bottom row; thin caption strip; collapsed strip; auto-hide peek). Pinned grid and workspaces come from the tabs area (`FeatureMounts.Components["workspaces"]`, placeholder meanwhile). Not yet seen on Windows: needs winui M1 (the skeleton window does not show `FeatureMounts.Shell`).
- [~] Mosaic: Chrome (tab strip in the title bar, omnibox row, bookmarks bar) — model unit-tested; view built (shaped tabs merging into the toolbar surface, pill omnibox with star, bookmarks-bar slot `Components["bookmarksBar"]`). Not yet seen on Windows (M1).
- [~] Mosaic: Opera / Opera GX — model unit-tested (GX on/off); view built (rounded tabs, rail, inset card, glow). Not yet seen on Windows (M1).
- [~] Mosaic: **Microsoft Edge** (Windows only) — preset and both tab modes unit-tested (`MosaicLayoutTests.Edge*`, `ShellPlanTests.EdgeVertical*`): workspaces + tab actions + rounded tabs in the title bar, toolbar with rounded address bar, Edge sidebar on the right (`trailingRail`, a Windows-only region macOS drops), vertical mode keeps the toolbar and collapses to a tab-icon column, Mica Alt behind tabbed title rows. Not yet seen on Windows (M1).
- [x] ~~No Safari Mosaic on Windows (macOS only), and layouts imported from macOS that use it fall back cleanly~~ — unit: a Safari layout reads as the Edge preset keeping density, radius, accent, light/dark, material, backgrounds, bookmarks bar and vertical tabs; a macOS `mosaics.json` with Safari active opens as Edge and its Safari entry is written back untouched; `mosaic safari` applies Edge. CI run 46136623487.
- [~] Switching Mosaics is instant: no restart, no page reload — by design (the page area is never re-parented: only grid column widths and margins change); `dump` reports `shell.pageRect` and tab state to check it. Not yet run on Windows (M1).
- [~] Settings inside the main window, all 14 categories, searchable; Mosaics, General and Accessibility pages — built (category list in three groups, search over titles and labels with matched labels shown, Enter opens the first match, Esc/Done close, pages from `FeatureMounts.SettingsPages` or a placeholder, page hidden via `CoverPage("shell.settings")`); search unit-tested (`SettingsSearchTests`), automation `openSettings`/`settingsSearch`/`closeSettings` unit-tested. Not yet seen on Windows (M1).
- [~] Accent colour, light/dark/system, density, corner radius, materials toggle (Mica/Acrylic ↔ solid) — Mosaics page and automation (`mosaicSet`) built; the toggle drives the window's material (winui's `MainWindow.Material` when present, else the shell's own backdrop) with solid fallbacks for Windows 10, transparency effects off, contrast themes. Not yet seen on Windows (M1).
- [~] Keyboard access everywhere; UI Automation names on every control; high contrast — every control named, landmarks on toolbar/tabs/settings/page, arrow keys in the settings list, accelerators (Ctrl+, Ctrl+Shift+, Ctrl+Shift+L Alt+D F6 Alt+Home Alt+F, Esc), contrast-theme colours; `dump` lists `shell.unnamedControls` for CI. Not yet checked on Windows (M1).

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
