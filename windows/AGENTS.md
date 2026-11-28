# Mosaic for Windows — feature pass: agent contract

Fourteen agents build the macOS app's features on Windows at the same time, each in its own
git worktree and branch. This file is the agreement. When it is silent, do what the macOS
code does: the macOS app is the reference implementation and is in your worktree
(`Sources/`, `Resources/`, `Tests/`, `README.md`, the root `To Do.md`).
`CONTRACT.md` describes the v0.1 port and its automation pipe; it still applies where this
file does not override it.

## What is being built

Every macOS feature (root `To Do.md`, items crossed off there), on Windows 10/11, x64 and
ARM64, plus the Windows-only requirements in `docs/windows-later.md`. The Windows checklist
is `windows/To Do.md`: **cross off your area's items there, in your own section only, with
how each was verified.**

## Decisions already made

| Topic | Decision |
| --- | --- |
| Language | C#, .NET 10. Engine-independent logic in `Mosaic.Core` (`net10.0`, unit-tested on any OS) |
| UI | **WinUI 3** (Windows App SDK 1.8), unpackaged, self-contained on Windows |
| How UI is written | **In C# code, no `.xaml` files.** The XAML compiler only runs on Windows; code-only UI compiles on this Mac. Build visual trees in code (`new Grid { … }`, `Children.Add`), use `XamlControlsResources` styles, `Microsoft.UI.Xaml.Data.Binding` or plain event handlers |
| Chromium | WinUI's `Microsoft.UI.Xaml.Controls.WebView2`, one shared `CoreWebView2Environment` (user data `AppPaths.ChromiumData`) |
| Gecko | Private Firefox ESR as a companion process driven by the shared agent, its window a child window of Mosaic's (`SetParent`), as in v0.1. A Win32 child window covers WinUI content, so Gecko must be hidden whenever Mosaic draws over the page |
| Gecko agent | **One file set for both platforms: the repository's `Resources/gecko/`** (the Windows copy was removed). Changes must keep macOS working: additive, guarded with `Services.appinfo.OS == "WINNT"` when Windows-only, and listed in your report |
| Data | `%LOCALAPPDATA%\Mosaic` (`AppPaths`), `MOSAIC_DATA_DIR` for isolated runs. **Same file formats as macOS** (layouts, configurations, bookmarks exports, filters, password CSV, session JSON where practical), so profiles and configurations move between platforms |
| Secrets | Windows Credential Manager / DPAPI. Never plain text on disk |
| Icons | Fluent: `Features/System/Icons.cs` (`Icons.Make("back")`); ask the system area for new names rather than hard-coding glyphs |
| Look | Windows 11: Mica on the window, Acrylic on transient surfaces, rounded corners, Fluent controls; solid fallbacks when transparency is off and on Windows 10 |

## Performance requirements (from the brief: "x64 and ARM64; low memory; fast startup; measured, not assumed")

Everyone:
- **Nothing new on the startup path.** Create services lazily on first use; register mounts
  cheaply (a `[ModuleInitializer]` that only stores delegates). No file, network, database or
  engine work before the first window is shown unless the first page needs it.
- **Never block the UI thread** for more than a frame (16 ms): file I/O, SQLite, hashing,
  JSON of real size, process launches and network go to background tasks.
- **No idle work:** no timers faster than once a second while nothing is happening; stop
  timers when their view is hidden.
- **Memory:** no unbounded caches; release views of closed tabs and hidden pages; prefer
  font glyphs to bitmaps.
- Both architectures: no x64-only native dependencies; anything native must ship win-arm64 too.

The **perf** area measures cold start, time to first page and memory (idle, 10 tabs) on CI
runners for x64 and ARM64, sets budgets that fail the build, and reports regressions to the
area responsible (via its final report). Instrument with `StartupTrace` once perf adds it.

## Areas and ownership

Paths are under `windows/` unless noted. **Edit only what you own.** Need something in
another area? Describe it in your final report (the integrator wires it), or ask in a
comment at the top of your own file.

| Area | Owns |
| --- | --- |
| **winui** | `src/Mosaic.App/*.cs` (root), `src/Mosaic.App/Hosting/**`, `src/Mosaic.App/Chromium/**`, `src/Mosaic.App/Automation/**`, `src/Mosaic.App/app.manifest`, `src/Mosaic.Core/Browser/**` (except other areas' `BrowserModel.<Area>.cs`), `src/Mosaic.Core/Gecko/**`, `scripts/*.ps1` (except those owned below), `.github/workflows/windows.yml`, deleting `src/Mosaic.App.Wpf/` once nothing needs it |
| **shell** | `src/Mosaic.Core/Shell/**` (except `SettingsCategory.cs`, integrator), `src/Mosaic.App/Features/Shell/**` |
| **tabs** | `src/Mosaic.Core/Tabs/**`, `src/Mosaic.App/Features/Tabs/**` |
| **library** | `src/Mosaic.Core/Library/**`, `src/Mosaic.Core/Search/**`, `src/Mosaic.App/Features/Library/**` |
| **engine** | `src/Mosaic.Core/Engine/**`, `src/Mosaic.Core/Permissions/**`, `src/Mosaic.App/Features/Engine/**` |
| **downloads** | `src/Mosaic.Core/Downloads/**`, `src/Mosaic.App/Features/Downloads/**` |
| **privacy** | `src/Mosaic.Core/Privacy/**`, `src/Mosaic.Core/Blocker/**`, `src/Mosaic.App/Features/Privacy/**` |
| **passwords** | `src/Mosaic.Core/Passwords/**`, `src/Mosaic.App/Features/Passwords/**` |
| **productivity** | `src/Mosaic.Core/Productivity/**`, `src/Mosaic.App/Features/Productivity/**` |
| **platform** | `src/Mosaic.Core/Platform/**`, `src/Mosaic.Core/Extensions/**`, `src/Mosaic.App/Features/Platform/**` |
| **canvas** | `src/Mosaic.Core/Canvas/**`, `src/Mosaic.App/Features/Canvas/**` |
| **system** | `src/Mosaic.Core/WindowsIntegration/**`, `src/Mosaic.App/Features/System/**` |
| **perf** | `src/Mosaic.Core/Performance/**`, `src/Mosaic.App/Features/Performance/**`, `scripts/perf/**`, `.github/workflows/windows-perf.yml`, `docs/performance.md` |
| **qa** | `scripts/e2e/**`, `.github/workflows/windows-e2e.yml`, `docs/testing.md` |
| integrator | `AGENTS.md`, `CONTRACT.md`, `Directory.Build.props`, `Mosaic.slnx`, `src/Mosaic.Core/AppPaths.cs`, `src/Mosaic.Core/Shell/SettingsCategory.cs`, `src/Mosaic.App/Features/FeatureMounts.cs`, `src/Mosaic.Core/Browser/AutomationRegistry.cs`, `README.md` |

Tests mirror source: `tests/Mosaic.Core.Tests/<Area>/**` belongs to the area.

### Shared files (additive edits allowed, keep them small)

- `src/Mosaic.Core/Browser/BrowserModel.cs` is `partial`. Put your area's state and methods
  in your own `src/Mosaic.Core/<Area>/BrowserModel.<Area>.cs`. If you need a hook inside an
  existing BrowserModel method (e.g. "a tab closed"), add **one line** calling your method,
  with a trailing comment `// <area>: why`. Prefer events the winui area exposes.
- `*.csproj`: you may add a `PackageReference` or `Content` item your area needs (list it
  in your report, with why, and check it has win-arm64 support). Nothing else.
- `Resources/gecko/**` (shared with macOS): additive changes per the rule above.
- `windows/To Do.md`: your own section only.

### Plugging in (no edits to others' files)

- **UI:** `src/Mosaic.App/Features/FeatureMounts.cs`: settings pages, toolbar/sidebar
  components by layout component id, overlays, internal `mosaic://` pages, startup hooks.
  Register from a `[ModuleInitializer]` in `Features/<Area>/<Area>Mounts.cs`.
- **Automation:** `AutomationRegistry.Register(handler)` and `RegisterState("<area>", …)`.
  Use the macOS command names (`Sources/Mosaic/**/*Automation*.swift`).
- **Settings categories:** `Mosaic.Core.Shell.SettingsCategory` (all 14, owners in
  `SettingsCategories.Owner`).

## Branches, building, CI

- Your worktree is `../mosaic-win-<area>` on branch `win2/<area>`, based on `win-next`.
- **Commit to your branch as you go** (small, described commits; end messages with
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`). **Push only your own branch**
  (`git push origin win2/<area>`), never force-push, never touch other branches.
- Build and test here (compile-only for the app):
  ```sh
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  dotnet build windows/Mosaic.slnx
  dotnet test  windows/tests/Mosaic.Core.Tests
  ```
- **Windows CI** (`.github/workflows/windows.yml`) runs on every push to `win2/**`: build,
  unit tests on Windows, publish, smoke test. Watch with
  `gh run list --branch win2/<area> --limit 3`, `gh run watch <id>`,
  `gh run view <id> --log-failed`, `gh run download <id>` (artifacts: logs, screenshots).
  A run takes 10–20 minutes, so batch your changes; do not push more than about every
  20 minutes. Use CI to *see* your feature working on Windows: add automation commands and
  `dump` fields so a script can check it, and screenshots for UI.
- To pick up other areas' merged work: `git fetch origin && git merge origin/win-next`
  (in your worktree only). The integrator merges finished areas into `win-next` and will tell
  you when something you depend on lands.
- Milestones: the **winui** area's first milestone (WinUI app runs on CI with both engines)
  and **shell**'s composed shell are what UI-heavy areas wait for. Until then, do your
  `Mosaic.Core` logic and tests first, then your WinUI views (they compile against
  `FeatureMounts` already), then wire and verify on CI after those land.

## Verification standard

An item is done when it works on a Windows runner (automation `dump`, a scenario script or a
screenshot), not when it compiles. Report precisely: what is unit-tested, what was observed
on CI (x64 / ARM64), what has never run, and what needs a human at a Windows PC (for example
real mouse drags, Windows Hello, notifications on a signed-in desktop).

## Ground rules for this Mac

The owner is using this Mac while you work.
- Work only inside your worktree. Never modify `/Users/aarav/browser/mosaic` (the macOS
  checkout) or other worktrees.
- Never open windows, steal focus or launch GUI apps. Firefox only with `--headless`, a
  throwaway profile under your scratch directory, and `--no-remote`.
- Never read or write `~/Library/Application Support/Mosaic`; never kill a process you did not
  start; do not install software; never commit secrets or tokens.
- Keep helper files in your worktree or in your own subfolder of the scratchpad
  (`scratchpad/win-<area>/`), never in a shared location.

## Final report

1. What you built, by `windows/To Do.md` item, with the verification for each (CI run IDs).
2. Files outside your area you changed (hooks, csproj items, `Resources/gecko`), and why.
3. What other areas must do for your work to be complete.
4. What is unverified, and known limitations.
5. Your last commit hash on `win2/<area>`.
