# Mosaic — To Do (macOS)

The working list for the macOS app. Finished items are crossed off (`- [x] ~~…~~`) and
say how they were verified. Items stay open until they work in the running app, not
just in code.

Owners for the current pass are in brackets (see `docs/AGENTS.md`):
**shell** (UI shell, Mosaics, Settings), **tabs**, **library**, **engine** (page features),
**downloads**, **privacy**, **passwords**, **productivity**, **platform** (engines, extensions,
about), **canvas** (layout editor, Gradient Studio).

Windows is paused; its requirements are saved in `docs/windows-later.md`.

Legend: `[ ]` open · `[x]` done · *deferred* = parked on purpose, with the reason.

---

## 0. UI pass (ergonomics first)

- [ ] Settings live inside the main window (no second window); every category reachable from one place, searchable [shell]
- [ ] Complete re-layout modelled on how current browsers arrange their chrome, per Mosaic preset [shell]
- [ ] No crashes in normal use: launch, tabs, both engines, settings, quit (checked by script on an isolated profile) [all]
- [ ] Keyboard-first: every action has a shortcut or command-palette entry; focus order is sensible [shell, productivity]
- [ ] Toolbar density and hit targets suitable for humans (44pt-class targets for primary actions, clear labels/tooltips) [shell]
- [ ] Accessibility labels on every control; VoiceOver can drive tabs, address bar, settings [shell]
- [ ] Reduced motion and reduced transparency respected [shell]

## 1. Mosaics (appearance presets)

- [ ] Mosaics settings category replaces Appearance/Customisation [shell]
- [ ] Preset: Zen / Arc — vertical tab sidebar (collapsible/auto-hide), pinned & favourites, workspaces, compact address bar [shell]
- [ ] Preset: Chrome — horizontal tabs, omnibox, standard toolbar, bookmarks bar [shell]
- [ ] Preset: Opera / Opera GX — refined horizontal tabs, optional sidebar, Speed Dial, GX dark/neon styling [shell]
- [ ] Preset: Safari (macOS only) — unified toolbar/address, compact tabs, native feel [shell]
- [ ] Switching presets is instant: no restart, no page reload, no data loss [shell]
- [ ] Preview cards with large layout previews; preview before applying; restore a preset to defaults [shell]
- [ ] Accent colour, light/dark/system, density, corner radius [shell]
- [ ] **Liquid Glass toggle** (glass materials on/off, with solid fallback) [shell]
- [ ] **Drag-and-drop Mosaic elements canvas**: rearrange tab strip, address bar, sidebar, toolbar buttons, bookmarks bar by dragging [canvas]
- [ ] **Gradient Studio** (from Eminence) for window/new-tab/workspace backgrounds [canvas]
- [ ] Save multiple named configurations; export/import/validate layouts [canvas]
- [ ] Custom background and new-tab styling [canvas, productivity]
- *deferred* — community presets, icon packs, Dia/Vivaldi/Brave/Firefox-inspired presets (after the four core presets are solid)

## 2. Engines

- [x] ~~Two real engines: Chromium (CEF 158, in-process) and Gecko (Firefox ESR, docked companion)~~ — v0.1, live
- [x] ~~Per-tab engine choice; switch a tab between engines (reloads it there)~~ — v0.1, live
- [ ] Engine settings page: Chromium and Gecko cards, versions, active engine, restart needs, compatibility notes [platform]
- [ ] Install / update the private Gecko runtime from inside the app (needed by the release build) [platform]
- [ ] Per-site engine rules (route domains to an engine automatically) [platform]
- [ ] Safe engine switching: keep the original tab until the new one has loaded [platform]
- [ ] Default engine for new tabs in settings [platform]

## 3. Browsing and navigation

- [x] ~~Address bar: URL or search; back/forward/reload/stop~~ — v0.1, live
- [x] ~~Search engine picker (Google, DuckDuckGo, Bing, Brave, Startpage, Ecosia, Kagi)~~ — v0.1, live
- [ ] Home button and custom homepage [shell]
- [ ] Custom search engines; search suggestions with a privacy switch [library]
- [ ] Find in page (both engines) [engine]
- [ ] Zoom per site, text scaling [engine]
- [ ] Full-screen browsing and page full-screen API [engine, shell]
- [ ] Private browsing windows/tabs (separate storage in both engines) [privacy]
- [ ] Multiple windows [tabs]
- [ ] Picture-in-Picture where supported [engine]
- [ ] Print and save as PDF [engine]
- [ ] Developer tools [engine]
- [ ] Permission prompts (camera, microphone, location, notifications, clipboard) shown by Mosaic for both engines [engine, privacy]
- [ ] JavaScript dialogs, context menus and file pickers work in both engines (verify) [engine]

## 4. Tabs, workspaces, multitasking

- [x] ~~Tabs with engine dot, close, new tab, ⌘1–9-style switching via ⌘⇧[/]~~ — v0.1
- [ ] Pin, lock, duplicate, mute tabs [tabs, engine for mute]
- [ ] Recently closed tabs and windows; reopen (⌘⇧T) [tabs]
- [ ] Tab search [tabs]
- [ ] Tab groups (collapsible, named, coloured) [tabs]
- [ ] Workspaces (named, separate tab sets, switcher) [tabs]
- [ ] Drag-and-drop tab reordering; move tabs between windows and workspaces [tabs, shell]
- [ ] Duplicate-tab detection [tabs]
- [ ] Session save and restore on relaunch; crash recovery [tabs]
- [ ] Split-screen with adjustable panes [tabs, shell]
- [ ] Tab previews on hover [shell]
- [ ] Sleep/unload inactive tabs (both engines), with exceptions for audio/calls/downloads; manual unload [tabs]
- *deferred* — automatic grouping suggestions (AI-adjacent; see §11)

## 5. Bookmarks, history, reading

- [ ] Bookmarks: folders, tags, bookmarks bar, manager, search [library]
- [ ] Import/export bookmarks (Netscape HTML) [library]
- [ ] History: full, searchable, delete by range/site [library]
- [ ] Reading list / read later [library]
- [ ] Reader mode with font/width/spacing/background controls (both engines) [productivity]
- [ ] Custom new-tab page / Speed Dial [productivity]
- [ ] Screenshots (visible area; full page where possible) [engine]
- *deferred* — PDF annotation, offline page saving, page-change monitoring

## 6. Downloads

- [ ] Download manager for both engines: progress, pause/resume/cancel/retry, history, search [downloads]
- [ ] Download folder choice, ask-where-to-save, organisation rules [downloads]
- [ ] Duplicate and misleading-extension warnings; dangerous-file warnings; quarantine attribute [downloads]
- [ ] Notifications when downloads finish [downloads]
- [ ] **Free Download Manager integration**: hand downloads to FDM (always, or per download) when installed [downloads]

## 7. Privacy and security

- [x] ~~Built-in ad/tracker blocker (uBlock Origin's engine, network level) in both engines, global and per-site switches~~ — live, both engines
- [ ] Cosmetic filtering (hide ad elements) [privacy]
- [ ] Custom filters; filter-list choice (stricter lists) [privacy]
- [ ] Blocker dashboard: what was blocked and why, per page [privacy]
- [ ] Site permissions manager (camera, mic, location, notifications, pop-ups) [privacy]
- [ ] Cookies and site data: per-site clear, clear on exit, third-party cookie blocking [privacy]
- [ ] Clear browsing data (history, cookies, cache, downloads) [privacy]
- [ ] HTTPS-only mode and security indicators [privacy]
- [ ] Tracking-parameter stripping [privacy]
- [ ] DNS-over-HTTPS [privacy]
- [ ] Pop-up blocking [privacy, engine]
- *deferred* — containers, fingerprinting protection, network-activity inventory, local download scanning signatures

## 8. Passwords and autofill

- [ ] Password manager stored in the macOS Keychain [passwords]
- [ ] Save-password prompt and autofill in both engines [passwords]
- [ ] Password generator [passwords]
- [ ] Import (CSV) / export [passwords]

## 9. Extensions

- [x] ~~Install Chrome extensions (.crx / Web Store), converted to run on Gecko~~ — v0.1, live
- [ ] Extensions settings category (manage, enable/disable, remove, permissions, compatibility notes) inside Settings [platform]
- [ ] Toolbar extension buttons and popups [platform]
- *deferred* — extensions in Chromium tabs (CEF Alloy has no extension support)

## 10. Productivity

- [ ] Command palette (⌘K): actions, tabs, bookmarks, history, settings [productivity]
- [ ] Customisable keyboard shortcuts [productivity]
- [ ] Searchable settings [shell]
- *deferred* — mouse gestures, notes, automation API, translation (needs a service)

## 11. Settings categories (all inside the main window)

- [ ] Mosaics [shell] · Engine [platform] · General [shell] · Tabs & Workspaces [tabs] · Search [library]
- [ ] Privacy & Security [privacy] · Passwords & Autofill [passwords] · Downloads [downloads] · Extensions [platform]
- [ ] Shortcuts & Gestures [productivity] · Languages [platform] · Accessibility [shell] · Performance [tabs] · About & Updates [platform]

## 12. Release

- [ ] Release build zipped and attached to a GitHub release [integrator]
- [ ] First launch from the zip works on a clean profile (engine install offered in-app) [platform, integrator]

## Deferred on purpose

- *CrossWork* — on hold at the owner's request.
- *Optional AI* (global switch, local models) — a switch with no AI features behind it would be a dead control.
- *Account and sync* — needs a backend (Firebase or other) and an encryption design.
- *Proxy browser (Interstellar-style)* — needs hosted proxy infrastructure.
- *Windows* — see `docs/windows-later.md`.
