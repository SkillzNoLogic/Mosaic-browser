# Mosaic for Windows

The Windows version of [Mosaic](../README.md), the two-engine web browser, written in C# and
WPF for Windows 10 and 11 on x64 and ARM64.

- **Two rendering engines, one window.** Every tab runs on either **Mozilla (Gecko)** or
  **Chromium (Blink)**. A toolbar switch moves the current tab to the other engine and
  reloads it there. Mozilla is the default.
- **Switchable search engines.** Google, DuckDuckGo, Bing, Brave Search, Startpage, Ecosia
  and Kagi.
- **Chrome extensions on the Mozilla engine.** Mosaic takes a Chrome extension's `.crx` (from
  a Chrome Web Store link, an extension ID, or a file), converts it, and runs it on Gecko.

> **Status: compiled and unit-tested, never run.** As of writing, the Windows app has been
> built and its engine-independent logic unit-tested on a Mac. **It has not been started on
> Windows even once.** Nothing below about how the app behaves on screen has been observed;
> it describes what the code is written to do. Read
> [Status and known limitations](#status-and-known-limitations) before spending time on it,
> and expect the first run to need fixes.

Requirements: Windows 10 version 1809 or later, or Windows 11; x64 or ARM64. 32-bit Windows is
not supported.

## Build and run

Only the [.NET 10 SDK](https://dotnet.microsoft.com/download) is needed. Visual Studio is
optional. The scripts run in the Windows PowerShell 5.1 that ships with Windows (and in
PowerShell 7) and need no modules.

```powershell
cd windows
scripts\bootstrap.ps1     # one time: fetches the Gecko engine (~90 MB download), checks for WebView2
scripts\build.ps1         # runs the unit tests, then builds publish\win-x64\ (or win-arm64)
publish\win-x64\Mosaic.exe
```

If PowerShell refuses to run scripts on your machine, start them as
`powershell -ExecutionPolicy Bypass -File scripts\bootstrap.ps1`.

`bootstrap.ps1` sets up the two engines:

| Engine | What | Where it comes from |
| --- | --- | --- |
| Gecko | Firefox ESR for your architecture, signature-verified | Downloaded from mozilla.org into `%LOCALAPPDATA%\Mosaic\Engines\Firefox\` |
| Chromium | The WebView2 Evergreen runtime | Part of Windows 11 and of most Windows 10 installations; the script only checks for it and points you to Microsoft's installer if it is missing |

The Gecko runtime is a private copy with its own profile. It is extracted, not installed:
Firefox's installer is only asked to unpack its files (`/ExtractDir`), so nothing is written
to the registry, no shortcuts, services or scheduled tasks are created, and a Firefox you
already have is neither read nor modified. Both the downloaded installer and the extracted
`firefox.exe` must carry a valid Authenticode signature from Mozilla Corporation, or nothing
is installed. If the private copy is missing, Mosaic falls back to a Gecko browser installed
on the machine (Firefox, Firefox ESR, Developer Edition, Nightly, LibreWolf, Zen), still with
its own separate profile; `MOSAIC_GECKO_EXE` names one explicitly.

`build.ps1` publishes the app self-contained, so the PC that runs it does not need .NET.
Options: `-Runtime win-x64, win-arm64` (either can be built on either machine), `-Zip` to
also produce `publish\Mosaic-<version>-<rid>.zip`, `-SkipTests`, `-Configuration Debug`.

Run only the tests with `dotnet test tests\Mosaic.Core.Tests`. They run on any OS; the Gecko
integration tests among them need a Firefox and are pointed at one with `MOSAIC_GECKO_EXE`.

## Using it

Mosaic for Windows is meant to work like [Mosaic for macOS](../README.md#using-it): type an
address or a search in the address bar, pick the search engine and the tab's engine from the
toolbar, install extensions from the Extensions sheet. Shortcuts use Ctrl where macOS uses ⌘.

While a Mozilla page has the keyboard, key presses go to Gecko, not to Mosaic. Ctrl+L
(address bar), Ctrl+T and Ctrl+N (new tab), Ctrl+W (close tab) and Ctrl+Shift+E (switch
engine) are forwarded to Mosaic from inside Gecko; other shortcuts act on Gecko directly.

## How it works

```
┌──────────────────────────── Mosaic.exe (C# / WPF) ─────────────────────────────────┐
│  Browser model ── tabs, search engine, engine switching      (Mosaic.Core, Mosaic.App)│
│     │                                                                              │
│     ├─ Chromium tab ─► WebView2 control ─► WebView2 runtime (msedgewebview2.exe)   │
│     │                                                                              │
│     └─ Gecko tab ───► GeckoEngine ─► RdpClient ─► loopback TCP ─► agent.sys.mjs    │
│            │                                          (inside the Gecko process)   │
│            └────────► GeckoWindowHost: Gecko's window as a child of Mosaic's       │
│  ExtensionStore: .crx → unpack → ManifestConverter → Gecko (and, experimentally,   │
│                                                         the original → WebView2)   │
└────────────────────────────────────────────────────────────────────────────────────┘
```

The design is the macOS one with three differences, all forced or offered by the platform:

| | macOS | Windows |
| --- | --- | --- |
| Chromium | Chromium Embedded Framework, bundled in the app (~300 MB) | WebView2, the system's shared and self-updating Chromium runtime; nothing bundled |
| Gecko in the window | A second window kept over a transparent hole in Mosaic's window | A real child window inside Mosaic's window (with the macOS approach as a fallback) |
| Talking to Gecko | Remote debugging protocol over a Unix socket only the user can reach | The same protocol over a loopback TCP port (Gecko has no socket-path option on Windows) |

### Chromium

Each Chromium tab is a `WebView2` control. All of them share one environment whose user-data
folder is `%LOCALAPPDATA%\Mosaic\Chromium`, so Mosaic's Chromium profile is separate from
Edge's. The runtime's processes (`msedgewebview2.exe`) belong to Microsoft Edge WebView2, not
to Mosaic, and are updated by Windows.

### Gecko

Mozilla ships no embedding API for desktop Gecko, on any platform. Mosaic's approach is the
same as on macOS:

1. It runs a private Gecko runtime as a companion process, with a dedicated profile.
2. It loads an agent, [`resources/gecko/agent.sys.mjs`](resources/gecko/agent.sys.mjs), into
   Gecko's parent process over the Firefox remote debugging protocol. The agent removes all
   of Firefox's own UI and makes the window borderless.
3. The agent reports the window's handle and the process ID. `GeckoWindowHost`
   (`src/Mosaic.App/Hosting`), a WPF element that occupies the content area, makes that
   window a **child window** of Mosaic's window (`SetParent`). From then on Windows itself
   clips, moves, stacks and hides the page together with Mosaic's window; Mosaic only resizes
   it. Unlike on macOS, the page cannot lag behind while the window is dragged, and no other
   window can get in between.
4. Mosaic polls the agent a few times a second for tab state and mirrors it, as on macOS.

Embedding another process's window is permitted by Windows but is not something either
program was designed for. The consequences Mosaic deals with:

- **Shared input.** Windows joins the input queues of the two programs. Mosaic therefore
  never makes a call that waits for Gecko on a routine path (it posts instead), and its
  window and Gecko's can hand the keyboard focus to each other.
- **Activation.** A child window is never "activated" by Windows, and Gecko only accepts
  keyboard input in a window it believes is active. Mosaic tells it: it posts the activation
  messages Gecko would have received as a top-level window when the page is clicked or
  focused, and the deactivation message when the focus returns to Mosaic's own controls.
- **Lifetime.** Before Mosaic's window is destroyed, Gecko's window is taken out again and
  left hidden, so that closing Mosaic does not destroy a window under a running Gecko. If
  Gecko exits or crashes, Mosaic notices its window disappearing and carries on. A Windows
  job object (`ChildProcessJob`) ends the Gecko processes if Mosaic itself dies.

The fallback, `Mosaic.exe --gecko-host=owned`, is the macOS arrangement: Gecko's window
stays a separate borderless window, owned by Mosaic's window (so it stays above it, is
minimised with it and has no taskbar button), and Mosaic keeps it positioned over the content
area. Mosaic also falls back to it by itself, until Gecko is next restarted, if Windows
refuses the child embedding.

The agent is the only place that touches Firefox internals, which is why Firefox ESR (stable
internals for a year) is the default runtime. The window embedding additionally depends on
how Gecko's Windows widget code reacts to a handful of window messages; that was read from
the Firefox ESR 153 source.

### Chrome extensions

Conversion for Gecko is the same as on macOS (see the [main README](../README.md#chrome-extensions-on-gecko)):
unpack the CRX, convert the manifest, add the runtime shim, report what cannot work.

**Experimental, Windows only:** because WebView2, unlike CEF's embedded browsers, can load
extensions, Mosaic also loads the extension's original, unconverted files into Chromium
tabs. Treat this as a trial: WebView2 supports only part of the extensions API and has no
extension UI of its own.

## Data

Everything Mosaic stores is under `%LOCALAPPDATA%\Mosaic\`:

| Folder | Contents |
| --- | --- |
| `Gecko\Profile\` | Profile of the Mozilla engine |
| `Chromium\` | WebView2 user-data folder (profile of the Chromium engine) |
| `Extensions\` | Installed extensions, original and converted |
| `Engines\Firefox\` | The private Gecko runtime fetched by `bootstrap.ps1` |

Delete the folder to reset everything. Set `MOSAIC_DATA_DIR` to use a different folder (the
tests and the smoke test do); `bootstrap.ps1` honours it too.

## Scripted checks

Started with `--mosaic-automation`, Mosaic listens on the named pipe
`\\.\pipe\mosaic-automation` and can be driven from a shell. In this mode it never takes the
keyboard focus on its own.

```powershell
publish\win-x64\Mosaic.exe --mosaic-automation
scripts\drive.ps1 navigate example.com
scripts\drive.ps1 engine chromium
scripts\drive.ps1 installFile C:\path\to\extension.crx
scripts\drive.ps1 dump C:\Temp\mosaic-state.json
scripts\drive.ps1 screenshot C:\Temp\mosaic.png
scripts\drive.ps1 quit
```

`drive.ps1` prints Mosaic's one-line JSON reply and exits non-zero if the command was
refused. The commands and the protocol are specified in [`CONTRACT.md`](CONTRACT.md).

`scripts\smoke.ps1` is the end-to-end check to run first on a real PC. It starts the
published app with a throw-away data folder and verifies, through `dump`, that a page loads
on the Mozilla engine with Gecko's window attached, that the tab can be switched to Chromium,
that changing the search engine changes where a search goes, that a small Chrome extension
(built by the script itself) installs and loads, and that `quit` leaves no Mosaic or engine
process behind. It takes a screenshot at each step and collects them, the state at each
step, Mosaic's output and any crash entries from the Windows event log in
`artifacts\smoke\`. It exits non-zero on any failure and always stops what it started.

```powershell
scripts\smoke.ps1                          # default embedding
scripts\smoke.ps1 -GeckoHostMode owned     # the fallback
```

The same sequence runs in CI (`.github/workflows/windows.yml`): one job builds, runs the
unit tests and packages `win-x64`; a second job, which is allowed to fail, runs the smoke
test on a Windows runner and uploads its screenshots and logs either way.

**Diagnostics.** The window-hosting code reports everything unusual through
`System.Diagnostics.Trace`, prefixed `[GeckoWindowHost]` or `[ChildProcessJob]`. Unless the
app routes trace output to a file, see it with a debugger or Sysinternals DebugView.

## Status and known limitations

**The Windows app has been compiled and unit-tested, and never run on Windows.** It was
written on a Mac, where the WPF project builds but cannot start. The automated smoke test
exists precisely because of that and has itself never been executed; neither have the
PowerShell scripts or the CI workflow, which were only reviewed by reading. Until someone
has run `scripts\smoke.ps1` on a PC and looked at the result, assume the following can be
wrong, most likely first:

**Showing Gecko inside Mosaic's window (the least certain part)**

- *Keyboard focus in Mozilla pages.* Clicking into a page, typing, and getting back to the
  address bar depend on the activation messages described above being accepted by Gecko the
  way its source suggests. If typing into a page does nothing, or the caret never appears,
  this is where to look. Try `--gecko-host=owned` to confirm.
- *A busy Gecko makes Mosaic sluggish; a frozen one freezes its input.* Because the two
  programs share an input queue, Mosaic's window does not receive mouse or keyboard input
  while Gecko's main thread is not responding. Mosaic does not yet detect and restart a
  frozen engine by itself. When letting go of a Gecko that no longer answers (for instance
  on exit), Mosaic terminates it rather than wait.
- *Monitors with different scaling.* If Mosaic's window is moved to a monitor with another
  scale factor while a Gecko window is embedded, the page keeps the right size but probably
  keeps rendering at the old scale (too small or too large) until Gecko is restarted: Gecko
  only re-reads the scale when Windows tells a *top-level* window that its DPI changed. The
  owned mode does not have this problem.
- *Gecko's own pop-ups* (context menus, `<select>` lists, tooltips, autocomplete) are
  separate windows that Gecko places in screen coordinates, so they are expected to work, and
  Mosaic asks Gecko to close them when its own UI is clicked or its window is dragged. An
  open dropdown may still be closed by the first click that gives the page the focus.
- *Full screen* (video, F11 inside Gecko) is not handled: as a child window Gecko cannot
  cover the screen.
- *Tabbing* from Mosaic's controls into the page is wired up through WPF; Tab does not
  leave the page again. Use Ctrl+L or the mouse.
- *Rendering.* Gecko decides by itself whether a window is hidden behind others and stops
  drawing if it thinks so. It is believed to treat an embedded window as visible; if Mozilla
  pages stay blank or freeze while Chromium pages are fine, that assumption is the suspect.
- *Owned mode* is a fallback, not an equal: while the page has the focus Mosaic's title bar
  is drawn inactive, the page follows window drags a moment behind, and clicks inside an
  already focused page are not reported to Mosaic's UI.

**Engines**

- The WebView2 runtime must be present for Chromium tabs; Mosaic does not install it.
- The private Firefox does not update itself (its updater is switched off so that it never
  registers background tasks). Run `scripts\bootstrap.ps1 -Force` with Mosaic closed to move
  to the current ESR.
- A Gecko left running by a Mosaic that was killed is normally ended by the job object. The
  job only covers processes started after Gecko's first process was added to it, so a
  leftover `firefox.exe` is possible in rare start-up races.

**Extensions**

- Everything in the [macOS list](../README.md#known-limitations) applies: not every Chrome
  extension can work on Gecko, there is no toolbar button or popup bubble, extensions do not
  update themselves.
- Loading extensions into Chromium tabs is experimental (see above).

**Security and privacy (prototype shortcuts, to fix before any release)**

- Gecko runs with remote debugging enabled on a loopback TCP port. Unlike the per-user
  socket on macOS, **any program running on the PC, under any user account, can connect to
  it and drive the browser.**
- The app is not code-signed. Windows SmartScreen will warn about it, and the published
  folder can be modified by anything running as you.
- Nothing has been reviewed for running Mosaic elevated ("as administrator"); do not.

**Missing browser features:** bookmarks, history UI, downloads UI, find in page, private
windows, settings, session restore, multiple windows, password management, default-browser
registration, an installer.

## Project layout

```
windows/
  CONTRACT.md           the working agreement the Windows version was built to
  Mosaic.slnx, Directory.Build.props
  scripts/
    bootstrap.ps1       fetch the Gecko engine, check for WebView2
    build.ps1           test, publish, optionally zip
    drive.ps1           send one automation command to a running Mosaic
    smoke.ps1           end-to-end check of a published build
    MosaicAutomation.psm1   the pipe client both of them use
  resources/gecko/agent.sys.mjs   Mosaic's agent inside Gecko
  src/
    Mosaic.Core/        engine-independent logic, unit tested, builds on any OS
      Search/           search engines, address-bar parsing
      Extensions/       CRX parsing, manifest conversion, extension library
      Gecko/            remote debugging client, runtime lookup, profile, engine
      Browser/          the browser model
    Mosaic.App/         the WPF app
      Hosting/          GeckoWindowHost (Gecko's window inside Mosaic's), ChildProcessJob
  tests/Mosaic.Core.Tests/
.github/workflows/windows.yml     CI: build, test, package, smoke test
```

## Third-party software

Mosaic for Windows uses Microsoft Edge WebView2 (the runtime is part of Windows or installed
separately under Microsoft's terms; the SDK is BSD-licensed) and runs an unmodified Firefox
ESR (MPL 2.0). Firefox is a trademark of the Mozilla Foundation; Mosaic is not affiliated
with Mozilla, Google or Microsoft.
