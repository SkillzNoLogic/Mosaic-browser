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
- [ ] Command palette (Ctrl+K)
- [ ] Customisable shortcuts, applied live, forwarded from Gecko pages
- [ ] Reader view (both engines) with its controls
- [ ] Speed Dial / new-tab page

## platform — engines, extensions, updates
Core in `src/Mosaic.Core/Platform/**` (+ `Extensions/ExtensionPermissions.cs`), tests in `tests/Mosaic.Core.Tests/Platform/**`,
WinUI in `src/Mosaic.App/Features/Platform/**`. Unit = 103 platform tests (97 on macOS, all 103 on the Windows x64 runner).
The app itself has not run yet: the winui M1 milestone (the window that hosts the engines and the model) is not in win-next,
so every WinUI page is **compiled only**, and `FeatureMounts.Startup` (which calls `ActivatePlatform`) has never fired.
- [~] Engine settings: Chromium (WebView2 runtime version via `GetAvailableBrowserVersionString`) and Gecko cards, versions, status, capabilities; start in Chromium when no Gecko runtime exists, with an install offer (overlay over Mozilla tabs). *Unit:* `StartsInChromiumWhenThereIsNoGeckoRuntime`, `AChosenEngineIsKept…`. *Missing:* page compiled only, no screenshot until M1.
- [~] In-app Gecko runtime install/update/remove with Authenticode verification (port of bootstrap.ps1: arch-specific ESR, HTTPS-only redirects on mozilla.org/.net, WinVerifyTrust + signer O=Mozilla Corporation on installer **and** extracted firefox.exe, PE machine check, `/ExtractDir`, policies.json, progress, cancel, nothing left behind, product-details update check, waiting Mozilla tabs reload without relaunch). *Unit:* 13 installer tests with fakes; *CI x64 (runs 46136605898, 46136939771):* real WinVerifyTrust tests pass (Microsoft-signed coreclr.dll valid with signer name; altered copy and unsigned file rejected); *CI x64 (run 46136939771):* `InstallsTheRealFirefoxEsrFromMozilla` downloads Firefox ESR from Mozilla, checks both signatures, unpacks, installs, and finds itself up to date against product-details (run 46136605898 had exposed a MAX_PATH failure in deep folders, fixed). *Missing:* through the app UI/automation (`runtimeInstall`) after M1; ARM64 never run.
- [~] Per-site engine rules (exact vs subdomains, most specific wins, macOS JSON in `platform.json`), "always open this site in…", default engine, safe engine switching both ways with failure/timeout/abandon/cancel. *Unit:* `EngineRulesTests`, `PlatformModelTests`, `SafeEngineSwitchTests` (fake engines). *Missing:* real engines on Windows (needs M1 and `pageStatus` in a real Gecko); Chromium navigation errors are not reported by `IChromiumBrowser` (see report).
- [~] Languages for web content: list editing, Gecko `intl.accept_languages` through the agent at each start and on change; WebView2 through `PlatformChromium.LanguageOptions()` (`Language` + `--accept-lang`) at environment creation. *Unit:* `LanguagePreferencesTests`, `LanguagesGoToGeckoWhenItStartsAndWhenTheyChange`. *Missing:* the winui area must call `PlatformChromium.LanguageOptions()`; never observed in a real engine.
- [~] Extensions settings page (install by link/ID/file, enable, popup/options as a Mozilla tab, permissions view, compatibility notes, remove) and toolbar buttons (component "extensions"). *Unit:* `ExtensionPermissionsTests`, `ExtensionToolbarButtonsOpenThePopupAsAMozillaTab`. *Missing:* page compiled only.
- [~] About & Updates: version, licences (Mosaic GPL v3+ from the packaged LICENSE, WebView2, Firefox MPL, uBlock Origin GPL, Readability Apache, BouncyCastle/ClearScript/WinAppSDK MIT, filter lists), privacy statement of connections, **automatic updates** with the macOS Ed25519 key (BouncyCastle; RFC 8032 vector and a CryptoKit-made signature verified), `Mosaic-<v>-windows-x64|arm64.zip` + `.sig`, download in background, install on quit or Restart to Update through a PowerShell helper that waits for Mosaic and its child processes and keeps the old folder until the swap succeeds. *Unit:* `MosaicUpdaterTests` end to end against a local `MOSAIC_UPDATE_API` feed with a test-key-signed zip (wrong key, tampered zip, wrong version/arch rejected). *CI x64 (runs 46136605898, 46136939771):* the helper waited for "Mosaic" and a child process, swapped and relaunched; stopped a straggler after 10 s; restored the old folder when the new one could not move in. *Missing:* the full app update (needs M1 and a signed release); ARM64.

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
