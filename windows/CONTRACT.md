# Mosaic for Windows: build contract

This is the working agreement for building the Windows version in parallel. The macOS app in
the repository root (`Sources/`, `Resources/`, `Tests/`, `README.md`) is the reference
implementation: when this document is silent, do what the macOS code does.

## What is being built

A Windows 10/11 (x64 and arm64) desktop browser with the same capabilities as Mosaic for macOS:

1. Tabs that each run on **Mozilla (Gecko)** or **Chromium (Blink)**, with a toolbar switch that
   moves the current tab to the other engine and reloads it there. Mozilla is the default.
2. A **search engine picker** (Google, DuckDuckGo, Bing, Brave Search, Startpage, Ecosia, Kagi).
3. **Chrome extensions (.crx) running on the Mozilla engine**: installed from a Chrome Web Store
   link, an extension ID, or a file; converted; loaded into Gecko.

## Decisions already made

| Topic | Decision |
| --- | --- |
| Language / UI | C#, .NET 10 (`net10.0`), WPF for the app (`net10.0-windows`) |
| Chromium engine | WebView2 (Evergreen runtime). One `WebView2` control per Chromium tab, one shared environment with user-data folder `AppPaths.ChromiumData` |
| Gecko engine | A private Firefox ESR run as a companion process with its UI removed, driven by `resources/gecko/agent.sys.mjs` over the Firefox remote debugging protocol. Same design as macOS |
| Gecko transport | Loopback TCP (`--start-debugger-server <port>`); Gecko does not support Unix-socket paths on Windows |
| Gecko in the window | The agent reports Gecko's HWND; `GeckoWindowHost` attaches it to Mosaic's window as a real child window (`SetParent`), with an owned-window fallback mode |
| Data | `%LOCALAPPDATA%\Mosaic` (`AppPaths`), overridable with `MOSAIC_DATA_DIR` |
| Logic placement | Everything that is not UI or Win32 lives in `Mosaic.Core` (plain `net10.0`), so it builds and is unit-tested on any OS |

## Layout and ownership

Each area has exactly one owner. **Edit only what you own.** If you need something changed in a
file you do not own, do not change it: describe the need in your final report.

| Path (under `windows/` unless noted) | Owner |
| --- | --- |
| `src/Mosaic.Core/Search/**`, `src/Mosaic.Core/Extensions/**`, `tests/Mosaic.Core.Tests/Search/**`, `tests/Mosaic.Core.Tests/Extensions/**` | **core** |
| `src/Mosaic.Core/Gecko/**`, `resources/gecko/**`, `tests/Mosaic.Core.Tests/Gecko/**` | **gecko** |
| `src/Mosaic.Core/Browser/**`, `tests/Mosaic.Core.Tests/Browser/**`, `src/Mosaic.App/**` except `Hosting/` and `app.manifest` | **app** |
| `src/Mosaic.App/Hosting/**`, `src/Mosaic.App/app.manifest`, `scripts/**`, `README.md`, repo-root `.github/workflows/windows.yml` | **host** |
| `CONTRACT.md`, `Directory.Build.props`, `Mosaic.slnx`, `*.csproj`, `src/Mosaic.Core/AppPaths.cs`, `src/Mosaic.Core/NotPorted.cs`, `tests/Mosaic.Core.Tests/ContractTests.cs` | integrator (nobody else edits) |

The project files already contain what is expected to be needed (WebView2 package, unsafe code
allowed, `InternalsVisibleTo` for the tests, xunit). If you truly need a package or project
setting, say so in your report rather than editing a `.csproj`.

## The API contract

The public types and members already present in `Mosaic.Core` and `Mosaic.App.Hosting` **are the
contract**. Bodies throw `NotImplementedException` (`NotPorted.Yet()`) or do nothing; owners
replace the bodies. Other areas are being written against these exact signatures at the same
time, so:

- Do not rename, remove, or change the signature of an existing public member.
- You may add members and types freely inside your own area.
- Semantics are given in each member's doc comment plus the macOS source it names.

### Agent methods (`resources/gecko/agent.sys.mjs`)

Called through `IGeckoEngine.CallAsync(method, args…)`; every method returns JSON.

| Method | Returns | Notes |
| --- | --- | --- |
| `setup()` | `true` once the browser window exists and its chrome is removed | polled during start |
| `state()` | `{ready, tabs:[{id,url,title,loading,canGoBack,canGoForward,selected,blocked}], events:[{type}]}` | decoded into `GeckoSnapshot`; `blocked` is the content blocker's count for the tab's page |
| `openTab(url, select)` | new tab id (number) | |
| `closeTab(id)`, `selectTab(id)`, `navigate(id, url)`, `goBack(id)`, `goForward(id)`, `reload(id)`, `stop(id)` | `true` | |
| `focusContent()` | `true` | |
| `installExtension(path, quiet)` | `{id,name,version,active,temporary}` | `path` is the converted (`gecko/`) folder |
| `uninstallExtension(geckoId)` | bool | |
| `listExtensions()` | array | |
| `openExtensionPage(geckoId, "popup" \| "options")` | new tab id, or `null` | |
| `version()` | `{agent, gecko, app, appVersion, pid, windowHandle}` | `pid` and `windowHandle` are new for Windows |
| `setRect(x, y, w, h)` | `true` | used by the owned-window fallback only |
| `configureBlocker(bundlePath, suffixListPath, listPaths[], enabled, allowlist[])` | `{filters, version}` | loads uBlock Origin's engine into Gecko; called after each start (before extensions load) and after the lists change |
| `setBlockerOptions(enabled, allowlist[])` | `true` | the content blocker's switches |

Keyboard shortcuts forwarded from inside Gecko as snapshot events use **Ctrl** on Windows
(Ctrl+L `focusAddress`, Ctrl+T / Ctrl+N `newTab`, Ctrl+W `closeTab`, Ctrl+Shift+E `toggleEngine`, Ctrl+Shift+B `toggleBlocker`).

### Automation (for end-to-end checks)

Enabled only when the app is started with `--mosaic-automation`. The app then listens on the
named pipe `\\.\pipe\mosaic-automation`. A client connects, writes **one line** of UTF-8 JSON,
reads **one line** of JSON back, and disconnects.

Request: `{"do": "<command>", "value": "<string, optional>", "out": "<path, optional>"}`
Reply: `{"ok": true}` or `{"ok": false, "error": "…"}`; `dump` replies `{"ok": true, "state": {…}}`.

Commands (same names and meaning as `Sources/Mosaic/App/Automation.swift`): `navigate`, `newTab`,
`closeTab`, `selectTab`, `engine` (`gecko`/`chromium`), `toggleEngine`, `searchEngine`, `back`,
`forward`, `reload`, `installExtension`, `installFile`, `removeExtension`, `extensionPage`
(`<chromeId>:popup|options`), `showExtensions`, `blocker` (`on`/`off`), `blockerSite` (`on`/`off`,
for the site of the page in front; refused when that is not a web page), `showBlocker`,
`updateLists`, `geckoEval` (agent method name; result written to `out`), `dump` (state also
written to the path in `value` when given), `screenshot` (PNG of the Mosaic window written to the
path in `value`), `quit`.

`dump` state keys: `tabs[]` (`engine`, `url`, `title`, `loading`, `canGoBack`, `canGoForward`,
`geckoId`, `hasChromiumView`, `blocked`, `failure`, `selected`), `engine`, `searchEngine`,
`addressText`, `chromiumAvailable`, `geckoStatus`, `geckoProcessId`, `geckoAttached`, `notice`,
`extensions[]` (`id`, `geckoId`, `name`, `version`, `enabled`, `loaded`, `notes[]`), `blocker`
(`status`: `unavailable`/`loading`/`ready`/`failed`, `enabled`, `allowlist[]`, `engineVersion`,
`updating`, `filters`, `error`), `extensionError`, `extensionsBusy`.

In automation mode the app must never take keyboard focus on its own.

## Building and testing on this machine

This Mac has the .NET 11 SDK (`dotnet`), which builds everything here, including the WPF app
(compile only; it cannot run). Set `DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`.

```sh
dotnet build windows/Mosaic.slnx          # everything, including the Windows app
dotnet test  windows/tests/Mosaic.Core.Tests
```

Nothing Windows-specific can be executed here. Be explicit in your report about what was
compiled, what was unit-tested, and what has never run. Where a Windows API detail matters and
you are not certain of it, check Microsoft's documentation instead of relying on memory.

## Ground rules for this machine

The owner of this Mac is using it while you work.

- Work only inside the worktree directory you were given. Never touch
  `/Users/aarav/browser/mosaic` (the macOS checkout) or the other worktrees.
- Do not run git commands that change anything (no commit, branch, stash, checkout, push).
- Never open windows, steal focus, or launch GUI apps. Firefox may be run **only** with
  `--headless`, a throwaway profile under your scratch directory, and `--no-remote`.
- Never read, write or delete `~/Library/Application Support/Mosaic` (the macOS app's live
  data), other than executing the Firefox binary inside `Engines/Firefox.app` read-only.
- Never kill a process you did not start. The macOS Mosaic and its Firefox may be running.
- Do not install software.
