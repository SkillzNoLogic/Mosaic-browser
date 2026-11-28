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
- [~] Pin, duplicate, mute; recently closed (Ctrl+Shift+T); tab search (Ctrl+Shift+A) — model done; all tabs unit tests also pass on the Windows x64 runner (CI run 46136196149) (pin ordering and Ctrl+W protection, duplicate, mute to Gecko via agent `setMuted` and to Chromium via `IChromiumAudio`/`WebView2Audio`, tabs/groups/workspaces reopened in place, private tabs never recorded, tab search ranking across workspaces, `tabs.*` events from Gecko, `TabsShortcuts`); WinUI search overlay and menus compile; not yet seen on CI (waits for winui M1 + shell). Lock not done (not done on macOS either)
- [~] Tab groups; workspaces (create, switch, delete, restore, move tabs) — model unit-tested (each workspace keeps its selection; Gecko docking follows because switching selects the tab); workspace switcher component and context menus compile; not yet seen on CI
- [~] Drag-and-drop reordering; duplicate-tab detection — `MoveTab`/`MoveTabBefore`/`MoveGroupBefore` and `TabStrip()` for the shell, `TabDragDrop` helpers, duplicate offer with Switch to Tab (unit-tested); needs the shell's strips and a human for real drags
- [~] Session save and restore; crash recovery; private tabs never saved — same JSON as macOS (a macOS-written file decodes in a unit test), written off the UI thread, restored after the first frame with unselected tabs asleep; quit/relaunch, crash marker, start-page setting and unreadable-file cases unit-tested; not yet run in the app
- [~] Sleeping tabs with exceptions and memory pressure; Tabs & Workspaces and Performance settings — policy, Chromium unload, Gecko `discardTab` (with close fallback), audio/download/pinned exemptions and Windows memory pressure (`CreateMemoryResourceNotification`/`GlobalMemoryStatusEx`) unit-tested; settings pages registered in `FeatureMounts.SettingsPages`; not yet seen on CI

## library — bookmarks, history, search
- [~] Bookmarks (folders, tags, bar, manager, search); import/export Netscape HTML and Chrome JSON — store, folders/tags/search, Netscape HTML and Chrome JSON import + export with round-trip tests: unit (macOS, and Windows x64 CI run 46136209134: SQLite e_sqlite3 native loads; win-arm64 native verified in the publish output, not run). Bar, star, manager page, editor flyout (covers Gecko), Ctrl+D / Ctrl+Shift+O / Ctrl+Shift+B: compiled only — waiting for winui M1 + shell to show them on CI
- [~] History (searchable, delete by visit/site/range) in both engines — one visit per navigation from every tab (Gecko and Chromium, selected or background), typed visits, error pages and private tabs excluded, flush on quit: unit (BrowserModel with fake engines). History page and Ctrl+H: compiled only
- [~] Reading list — store and model: unit; page compiled only
- [~] Custom search engines with keywords; search suggestions (off by default); Search settings — `w term` / `@ddg term` in Navigate, custom default engine restored, remote suggestions only when switched on and never for private tabs (fake HTTP): unit. Search settings page: compiled only

## engine — page features
- [ ] Find in page; per-site zoom; print and save as PDF; developer tools (both engines)
- [ ] Screenshots (visible and full page); mute; Picture in Picture; page full screen
- [ ] Mosaic's own permission prompts (camera, microphone, location, notifications, clipboard), remembered per site
- [ ] Pop-up blocking with a notice; JavaScript dialogs; context menus; file pickers

## downloads
Run 46136495669 = Windows CI x64, unit-test job (`tests/Mosaic.Core.Tests/Downloads`, 84 tests, all passing there and on macOS).
App-side items are compiled and wired through `FeatureMounts`, but cannot run until winui's M1 is in win-next;
`DownloadsEndToEndTests` (real app via the automation pipe, throttled local server) is written and switched off until then.
- [~] Download manager for both engines: progress, pause/resume/cancel/retry, history, search — Core manager unit-tested with fake WebView2 operations and a fake agent; **Gecko verified on CI** (run 46136495669, `GeckoDownloadIntegrationTests`: real Firefox + shared agent, throttled server, start → pause → resume with a `Range` request → complete, a page's attachment adopted, history written in the macOS format). Missing: WebView2 (`WebView2Downloads`) and the `mosaic://downloads` page on CI, waiting on M1
- [~] Download folder, ask where to save, organisation rules — unit (Known Folder Downloads default, rules, Save As question answered by `downloadPromptSave`, Gecko files moved into their folder); system save dialog (FileSavePicker + InitializeWithWindow) never run: needs a human
- [~] Mark of the Web (Zone.Identifier) on every file; dangerous and misleading file warnings — **CI**: `ZoneId=3` + `HostUrl`/`ReferrerUrl` written to the ADS and read back (`MarkOfTheWebTests`), and on Gecko downloads (run 46136495669); Windows dangerous types, double/padded extensions and RTL override unit-tested. Missing: Chromium files on CI (M1)
- [~] Toast notification and taskbar progress — implemented behind one call site (`DownloadSystemHooks`: Windows App SDK app notifications, ITaskbarList3) ready to switch to the system area's `UserNotifications`/`TaskbarProgress`; never run; a real toast needs a signed-in desktop (human)
- [~] Free Download Manager hand-off on Windows (off / ask / always) — detection (native-messaging manifest `org.freedownloadmanager.fdm5.cnh`, Inno uninstall key, `Softdeluxe\Free Download Manager` folders) and the `wenativehost.exe` handshake/`create_downloads` protocol unit-tested against a fake helper over pipes, on CI too; fallback `fdm.exe <url>`; policy off/ask/always + size threshold unit-tested. Never run against a real FDM (needs a PC with FDM installed)
- [~] Downloads settings — page written (all macOS settings, Windows wording), settings persisted and unit-tested; not yet seen on CI (M1)
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
- [~] Fluent icons everywhere (Segoe Fluent Icons; Segoe MDL2 Assets on Windows 10): `Icons` / `IconGlyphs` map all 160 SF Symbol names the macOS code uses plus ~110 semantic names; code points checked against Microsoft's tables; MDL2 substitutes for the 7 Fluent-only glyphs; font detection + `MOSAIC_ICON_FONT=mdl2`. Unit (incl. a scan of `Sources/`). CI x64 (run 46136247394): every mapped code point found in the runner's installed icon font(s) by reading their cmap tables (`WindowsOnlyTests.EveryGlyphExistsInTheInstalledFonts`). *Missing:* the `iconGallery` screenshot in the running app (needs the winui milestone), and adoption by the other areas
- [~] Set as default browser (registration, URL and file activation): HKCU StartMenuInternet/Capabilities, RegisteredApplications, MosaicHTML/MosaicURL (`--single-argument %1`), OpenWithProgids, exact unregister, status, Settings deep link, `DefaultBrowserCard`; `ActivationRequest` parser + forwarding payload. Unit (in-memory registry); CI x64 (run 46136247394): real HKCU register → the shell resolves MosaicURL/MosaicHTML to the open command (AssocQueryString) → unregister leaves nothing (`WindowsOnlyTests.RegistersAndUnregistersInTheRealRegistry`). *Missing:* the in-app `registerDefault`/`activate` path on CI, winui wiring `ActivationRequest` into SingleInstance, choosing Mosaic in Settings (needs a human)
- [~] Notifications (app notifications), jump list, taskbar integration: `UserNotifications` hub + Windows App SDK presenter (in-window fallback), `TaskbarProgress` + ITaskbarList3, jump list (New tab / New private tab). Unit for the hubs; app side compiled only
- [~] Native context menus; drag and drop of links, text and files; virtual desktops behave: `ContextMenus` (Fluent MenuFlyout, windowed, CoverPage), `DragDropSupport`, `VirtualDesktops` check on activation, `SystemAppearanceMonitor` (theme/accent/high contrast). Unit for the models; app side compiled only

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
