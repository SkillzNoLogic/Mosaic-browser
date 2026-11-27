# Mosaic — To Do (macOS)

The working list for the macOS app. Finished items are crossed off (`- [x] ~~…~~`) and
say how they were verified. "Script" means driven in the running app on an isolated
profile through the automation channel (both engines where it applies), as described in
`docs/AGENTS.md`. Items stay open until they work in the running app, not just in code.

Owners for this pass: **shell**, **tabs**, **library**, **engine**, **downloads**,
**privacy**, **passwords**, **productivity**, **platform**, **canvas**, **integrator**.

Windows is paused; its requirements are saved in `docs/windows-later.md`.

Legend: `[ ]` open · `[~]` partly done (what is missing is stated) · `[x]` done ·
*deferred* = parked on purpose, with the reason.

---

## 0. UI pass (ergonomics first)

- [x] ~~Settings live inside the main window (no second window); every category reachable from one place, searchable~~ — script: all 14 categories opened and closed; search filters categories and labels
- [x] ~~Complete re-layout modelled on how current browsers arrange their chrome, per Mosaic preset~~ — composed shell; screenshots in `docs/screenshots/shell-*.png`
- [~] No crashes in normal use: launch, tabs, both engines, settings, quit — every scripted run of the merged app quit cleanly with no crash report; needs a hands-on session on an unlocked screen [all]
- [~] Keyboard-first: shortcuts and tooltips on every toolbar action, command palette covers the model's actions; no full-screen menu item yet [shell, productivity]
- [x] ~~Toolbar density and hit targets suitable for humans~~ — toolbar buttons ≥ 32×32 pt, hit area spans the 44 pt row at regular density
- [x] ~~Accessibility labels on every control~~ — labels on toolbar, tabs, address field, settings; VoiceOver actions on Speed Dial tiles
- [x] ~~Reduced motion and reduced transparency respected~~ — system settings plus overrides in Settings › Accessibility

## 1. Mosaics (appearance presets)

- [x] ~~Mosaics settings category replaces Appearance/Customisation~~
- [x] ~~Preset: Zen / Arc~~ — vertical sidebar (shown / collapsible / auto-hide), pinned grid, workspaces, compact address bar
- [x] ~~Preset: Chrome~~ — tab strip in the title-bar row, omnibox row, bookmarks bar
- [x] ~~Preset: Opera / Opera GX~~ — refined tabs, sidebar rail, Speed Dial; GX dark/neon variant
- [x] ~~Preset: Safari (macOS only)~~ — unified toolbar with compact tabs, or separate tab bar
- [x] ~~Switching presets is instant: no restart, no page reload, no data loss~~ — script: all four presets with a Gecko and a Chromium tab open; same URLs, same Gecko tab, Chromium view kept, Gecko re-docked exactly to the new content area
- [x] ~~Preview cards; preview before applying; restore a preset to defaults~~
- [x] ~~Accent colour, light/dark/system, density, corner radius~~
- [x] ~~**Liquid Glass toggle**~~ — `glassEffect` on chrome surfaces (macOS 26+), solid when off or when Reduce Transparency is on
- [x] ~~**Drag-and-drop Mosaic elements canvas**~~ — Settings › Mosaics › Customise Layout: drag between regions, palette of unplaced components, refused drops explain why, keyboard editing, undo/redo, live without reloads; script-verified, real mouse drags not yet tried by hand
- [x] ~~**Gradient Studio** (from Eminence)~~ — ported: free-form mesh in OKLab, linear/radial/angular, colour editor, grain, presets; for the window, the new-tab page and workspaces
- [x] ~~Save multiple named configurations; export/import/validate layouts~~ — validated JSON import/export
- [x] ~~Custom background and new-tab styling~~ — Gradient Studio backgrounds for the window, new-tab page and each workspace
- *deferred* — community presets, icon packs, Dia/Vivaldi/Brave/Firefox-inspired presets

## 2. Engines

- [x] ~~Two real engines: Chromium (CEF 158, in-process) and Gecko (Firefox ESR, docked companion)~~ — v0.1
- [x] ~~Per-tab engine choice; switch a tab between engines~~ — v0.1
- [x] ~~Engine settings page: Chromium and Gecko cards, versions, status, what each supports~~
- [x] ~~Install / update / remove the private Gecko runtime from inside the app~~ — script: real download from Mozilla, code signature and Team ID 43AQ936H96 verified, waiting Mozilla tab loaded without relaunch; damaged, tampered and non-Mozilla apps rejected; cancel leaves nothing behind
- [x] ~~Per-site engine rules~~ — script: address bar and new tabs, exact vs subdomain rules, "always open this site in…"
- [x] ~~Safe engine switching: keep the original page until the new engine has loaded it~~ — script: both directions with a slow page, and deliberate failures in both directions
- [x] ~~Default engine for new tabs in settings~~
- [x] ~~Start in Chromium when no Gecko runtime is installed, and offer the install~~

## 3. Browsing and navigation

- [x] ~~Address bar: URL or search; back/forward/reload/stop~~ — v0.1
- [x] ~~Search engine picker~~ — v0.1
- [x] ~~Home button and custom homepage~~
- [x] ~~Custom search engines (with keywords); search suggestions with a privacy switch (off by default)~~ — script: `w mosaic browser` → Wikipedia; remote suggestions only when switched on
- [x] ~~Find in page (both engines)~~ — ⌘F / ⌘G, match count
- [~] Zoom per site, text scaling — per-site zoom shared between engines done; text-only zoom not built [engine]
- [~] Full-screen browsing and page full-screen API — page full screen implemented for Chromium, not verified by hand; Gecko page full screen and Mosaic window full screen not done [engine, shell]
- [~] Private browsing — private tabs in Chromium (in-memory profile, nothing on disk; verified); Gecko private tabs not supported yet and the UI says so [privacy]
- [ ] Multiple windows — not attempted: one docked Gecko window would need a redesign [tabs]
- [x] ~~Picture-in-Picture where supported~~
- [x] ~~Print and save as PDF~~ — both engines
- [x] ~~Developer tools~~ — both engines
- [~] Permission prompts (camera, microphone, location, notifications, clipboard) shown by Mosaic — Mosaic's own prompts, remembered per site; clipboard prompts and Gecko microphone streams not verified [engine]
- [~] JavaScript dialogs, context menus and file pickers verified in both engines — dialogs done in both; Gecko file picker not verified [engine]
- [x] ~~Languages for web content applied to both engines~~ — script: `navigator.languages` and Accept-Language in both engines

## 4. Tabs, workspaces, multitasking

- [x] ~~Tabs with engine dot, close, new tab~~ — v0.1
- [~] Pin, duplicate, mute — pin, duplicate and mute done in both engines; lock not done [tabs]
- [x] ~~Recently closed tabs; reopen (⌘⇧T)~~ — script: tabs, whole groups and deleted workspaces come back in place; private tabs never recorded
- [x] ~~Tab search (⌘⇧A)~~
- [x] ~~Tab groups (collapsible, named, coloured)~~
- [x] ~~Workspaces (named, separate tab sets, switcher)~~ — script: create, switch (each keeps its selection; Gecko docking follows), delete, restore, move tabs
- [~] Drag-and-drop tab reordering — model done and wired to the shell's strips; not yet exercised by hand [tabs, shell]
- [x] ~~Duplicate-tab detection~~ — "already open" notice with Switch to Tab
- [x] ~~Session save and restore on relaunch; crash recovery~~ — script: quit/relaunch and `kill -9` with tabs in both engines, groups and workspaces; private tabs never saved
- [ ] Split-screen — not attempted (only one Gecko page can be visible at a time) [shell]
- [~] Tab previews on hover — tooltip only [shell]
- [x] ~~Sleep/unload inactive tabs (both engines), with exceptions; manual unload; memory pressure~~ — Chromium tabs lose back/forward history when unloaded; tabs with active downloads not yet exempt
- *deferred* — automatic grouping suggestions (AI-adjacent)

## 5. Bookmarks, history, reading

- [x] ~~Bookmarks: folders, tags, bookmarks bar, manager, search~~ — script
- [x] ~~Import/export bookmarks (Netscape HTML; also Chrome's JSON)~~ — round trip unit-tested; 19-bookmark file imported without duplicates
- [x] ~~History: full, searchable, delete by visit/site/range~~ — one visit per navigation in both engines, selected or background
- [x] ~~Reading list~~
- [x] ~~Reader mode with font/size/width/spacing/background controls (both engines)~~ — Firefox's reader view in Gecko, Readability 0.6.0 in Chromium
- [x] ~~Custom new-tab page / Speed Dial~~ — pinned sites plus top sites; optional privacy-preserving favicons
- [x] ~~Screenshots (visible area; full page)~~ — both engines
- *deferred* — PDF annotation, offline page saving, page-change monitoring

## 6. Downloads

- [x] ~~Download manager for both engines: progress, pause/resume/cancel/retry, history, search~~ — script against a local throttled server; Gecko resume uses range requests
- [x] ~~Download folder, ask-where-to-save, organisation rules~~
- [x] ~~Duplicate, misleading-extension and dangerous-file warnings; quarantine attribute~~ — `xattr` checked on every file
- [~] Notifications when downloads finish — implemented; real system notification not shown during scripted runs
- [x] ~~**Free Download Manager integration**~~ — off / ask / always; hands over URL, referrer and User-Agent through FDM's own helper; verified with one test file

## 7. Privacy and security

- [x] ~~Built-in ad/tracker blocker (uBlock Origin's engine) in both engines, global and per-site switches~~
- [x] ~~Cosmetic filtering (hide ad elements)~~ — script on a test page in both engines; procedural filters and scriptlets not supported (stated)
- [x] ~~Custom filters; filter-list choice~~ — My filters with validation; 15 optional stricter lists
- [x] ~~Blocker dashboard: what was blocked and why, per page~~
- [x] ~~Site permissions manager~~ — shares the engine's SitePermissionStore
- [x] ~~Cookies and site data: per-site clear, clear on exit, third-party cookie blocking~~
- [x] ~~Clear browsing data~~ — engines plus Mosaic's history and downloads lists, with time ranges
- [x] ~~HTTPS-only mode and security indicators~~ — padlock shows secure / not secure / local
- [x] ~~Tracking-parameter stripping~~ — Firefox's query-stripping list plus `utm_*`
- [x] ~~DNS-over-HTTPS~~ — both engines, no restart
- [x] ~~Pop-up blocking~~ — with a notice to allow the pop-up
- *deferred* — containers, fingerprinting protection, network-activity inventory, local download scanning signatures

## 8. Passwords and autofill

- [x] ~~Password manager stored in the macOS Keychain~~ — Mosaic's own security domain; reveal behind Touch ID / login password
- [x] ~~Save-password prompt and autofill in both engines~~ — script: save, update, never-save, cross-origin frames refused, private tabs never offered
- [x] ~~Password generator~~
- [x] ~~Import (CSV) / export~~

## 9. Extensions

- [x] ~~Install Chrome extensions (.crx / Web Store), converted to run on Gecko~~ — v0.1
- [x] ~~Extensions settings category inside Settings~~ — replaces the old separate window; permissions view
- [~] Toolbar extension buttons and popups — buttons done; popups open as a tab, not a panel (stated in the UI)
- *deferred* — extensions in Chromium tabs (CEF Alloy has no extension support)

## 10. Productivity

- [x] ~~Command palette (⌘K): actions, tabs, bookmarks, history, settings~~
- [x] ~~Customisable keyboard shortcuts~~ — changes apply to the menus live, survive relaunch, forwarded from Gecko pages
- [x] ~~Searchable settings~~
- *deferred* — mouse gestures, notes, automation API, translation (needs a service)

## 11. Settings categories (all inside the main window)

- [x] ~~Mosaics · General · Accessibility~~ [shell] · ~~Tabs & Workspaces · Performance~~ [tabs] · ~~Search~~ [library]
- [x] ~~Privacy & Security~~ [privacy] · ~~Passwords & Autofill~~ [passwords] · ~~Downloads~~ [downloads] · ~~Engine · Extensions · Languages · About & Updates~~ [platform] · ~~Shortcuts & Gestures~~ [productivity]

## 12. Release

- [x] ~~Release build zipped and attached to a GitHub release~~ — v0.2.0, `Scripts/release.sh`
- [x] ~~First launch from the zip works on a clean profile (engine install offered in-app)~~ — script: unzipped copy, signature verified, starts in Chromium with no Gecko runtime, loads a page, quits cleanly
- [x] ~~Automatic updates from Settings › About & Updates~~ — Ed25519-signed release zips, background download, install at quit or Restart to Update, GitHub token for the private repository; script: a 0.8.0 copy updated itself to 0.9.0 from a local feed and relaunched; tampered and mislabelled downloads refused; token check against the real repository
- [x] ~~First real update through GitHub~~ — script: a copy with the updater (0.2.9) updated itself to the published 0.3.0 from the public repository, no token, and relaunched; installer now waits for Chromium helpers of the old app before swapping (0.3.1)
- [x] ~~A licence for Mosaic itself~~ — GNU GPL v3 or later (`LICENSE`), required by the bundled uBlock Origin engine; shown in Settings › About & Updates and shipped in the app

## Deferred on purpose

- *CrossWork* — on hold at the owner's request.
- *Optional AI* (global switch, local models) — a switch with no AI features behind it would be a dead control.
- *Account and sync* — needs a backend (Firebase or other) and an encryption design.
- *Proxy browser (Interstellar-style)* — needs hosted proxy infrastructure.
- *Windows* — see `docs/windows-later.md`.
