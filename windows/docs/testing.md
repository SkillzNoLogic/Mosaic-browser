# Mosaic for Windows: end-to-end testing

The end-to-end suite drives the **published** `Mosaic.exe` through its automation pipe
(`windows/CONTRACT.md`, "Automation") on a real Windows machine or CI runner, and checks
every feature area with the same command names as the macOS app
(`Sources/Mosaic/**/*Automation*.swift`). Owner: **qa**.

- Scripts: `windows/scripts/e2e/` (Windows PowerShell 5.1, nothing to install).
- CI: `.github/workflows/windows-e2e.yml` (push to `win-next` and `win2/qa`, or run it by hand).
- Results: JUnit (`junit.xml`), JSON (`results.json`), a Markdown summary (`summary.md`, also
  shown on the run's summary page), logs, states, screenshots, the screenshot gallery, the
  accessibility reports and any crash dumps.

## Running it

```powershell
cd windows
scripts\bootstrap.ps1                    # once: Gecko engine, WebView2 check, content blocker
scripts\build.ps1 -SkipTests             # publish\win-x64\Mosaic.exe (or win-arm64)
scripts\e2e\Enable-CrashDumps.ps1        # optional, as administrator: WER LocalDumps
scripts\e2e\Run-E2E.ps1                  # everything; output in artifacts\e2e
scripts\e2e\Run-E2E.ps1 -Areas tabs,library
scripts\e2e\Run-E2E.ps1 -Tags smoke      # the subset CI runs on ARM64
scripts\e2e\Run-E2E.ps1 -Name 'cosmetic' # scenarios whose name contains the text
scripts\e2e\Run-E2E.ps1 -SelfTest        # the framework against a stand-in app (no build)
```

Exit code: `0` nothing failed, `1` a scenario failed, `2` the suite itself broke.

The run never touches your real profile: each area gets a fresh `MOSAIC_DATA_DIR` under the
output folder. It does show Mosaic's window, and the accessibility Tab walk takes the keyboard
focus, so that part only runs when `CI` is set or `MOSAIC_E2E_KEYBOARD=1`.

## How it works

| Piece | File |
| --- | --- |
| Runner (areas, options, reports, exit code) | `e2e/Run-E2E.ps1` |
| Framework: app lifecycle, pipe client, waits, pending/crash/hang classification, JUnit/JSON/Markdown | `e2e/lib/E2E.psm1` |
| Local web server (C# compiled by `Add-Type`): pages, reports, throttled/resumable downloads, echo of headers, cookies, slow pages, audio | `e2e/lib/TestServer.cs`, pages in `e2e/site/` |
| Native helpers: windows of a process, `IsHungAppWindow`, `MiniDumpWriteDump`, captures from outside, DPI | `e2e/lib/Win32.cs` |
| Accessibility: UI Automation walk, Tab walk | `e2e/lib/Accessibility.psm1` |
| Shared scenario helpers (extension fixture, Mark of the Web, "nothing on disk" search) | `e2e/lib/Fixtures.ps1` |
| Scenarios, one file per area | `e2e/scenarios/<area>.ps1` |
| Fixtures (bookmarks HTML/JSON, password CSV, macOS configuration files) | `e2e/fixtures/` |
| WER LocalDumps on/off | `e2e/Enable-CrashDumps.ps1` |
| Framework self-test (stand-in app that speaks the protocol, hangs and crashes on request) | `e2e/selftest/` |

For each area, in order (winui, shell, tabs, library, engine, downloads, privacy, passwords,
productivity, platform, canvas, system, gallery, accessibility):

1. Mosaic starts with `--mosaic-automation` on its own data folder; the runner waits for the pipe.
2. Each scenario runs. Pages come from the local test server at `http://127.0.0.1:<port>/`; a
   second site is `http://localhost:<port>/` (cross-site frames, third-party cookies). Pages
   report what they observe (an element hidden by cosmetic filtering, a filled password field,
   a dialog's answer) to `/report?token=...`, which scenarios wait for.
3. Mosaic is quit; a teardown result per area checks that it exits and leaves no Mosaic,
   `msedgewebview2.exe` or `firefox.exe` process behind.

### Pending, not failed

Areas are built in parallel, so most scenarios start out unable to run. A scenario is
**pending** (JUnit `skipped`, with the reason) when:

- Mosaic answers a command with `Unknown command "..."` (the area has not registered it), or
- `dump` has no field the scenario needs (`Require-Field`, e.g. `tabsArea`, `library`,
  `downloads`, `page`), or
- the build has no automation pipe at all (every pipe scenario pending; the
  `winui / starts and opens the automation pipe` scenario fails, as that is the winui area's job).

`summary.md` lists the missing commands and dump fields per area. A command that *is* known but
does the wrong thing fails the scenario with evidence. Nothing pending is ever reported as passed.

### Crashes and hangs

- **Crash:** Mosaic exits while a scenario runs (and the scenario did not quit or kill it on
  purpose), or a new dump appears: Windows Error Reporting LocalDumps (`Enable-CrashDumps.ps1`
  for `Mosaic.exe`, `msedgewebview2.exe`, `firefox.exe`), WebView2's Crashpad reports or Gecko
  minidumps inside the data folders. The scenario fails (`kind: crash`), the dumps are copied to
  `dumps\` and uploaded, and the next scenario gets a fresh Mosaic.
- **Hang:** a pipe command times out and Mosaic's window is hung (`IsHungAppWindow`, or no
  answer to `WM_NULL` in 5 s). A dump of the hung process is written (`dumps\hang-*.dmp`), the
  scenario fails (`kind: hang`) and Mosaic is restarted. A `quit` that does not end Mosaic in
  30 s fails the area's teardown as a hang.
- Error events from the Application log (.NET Runtime, Application Error, WER, Application
  Hang) are saved per area as `areas\<area>\events.txt`.

### Evidence

Every failure saves the state (`areas\<area>\state-failure-*.json`), a screenshot and the native
window tree (`windows-failure-*.txt`: which process's windows are where; the way to see whether
Gecko's child window covers Mosaic's UI). Scenario logs (each command sent) are in
`results.json` and in JUnit `system-out`.

### Screenshot gallery

`gallery\index.html` (CI artifact `screenshot-gallery-<arch>`): every Mosaic preset (Zen/Arc,
Chrome, Opera, Opera GX, Microsoft Edge) in light and dark, every settings category in light
and dark, key overlays (command palette, tab search, find bar, blocker and downloads popovers,
extensions, permission prompt, pop-up notice, JavaScript dialog, password save prompt, reader
view, clear browsing data, layout canvas, Gradient Studio, sidebar) and Mosaic's own pages (new
tab / Speed Dial, bookmarks, history, reading list, downloads). Taken with the app's
`screenshot` command; while that is missing, a capture of the window from outside
(`PrintWindow`, `PW_RENDERFULLCONTENT`). Items whose commands are not in the build are listed
as pending in the gallery.

### Accessibility scan

`accessibility\<surface>.md/.json` for the main window, each Mosaic preset and each settings
page. It walks the UI Automation control view (web page content inside WebView2 and Gecko is
skipped) and reports:

- **unnamed** – an interactive control (button, edit, check box, combo box, list/tab/tree/menu
  item, hyperlink, slider...) without a UI Automation `Name`;
- **notFocusable** – an enabled, on-screen interactive control that is not keyboard focusable;
- **notReached** – (Tab walk, CI only) a focusable control that pressing Tab never reached
  (controls normally reached with arrow keys inside their container are not counted).

Any finding fails the scenario; the report lists each control with its type, `AutomationId`,
class, position and path in the tree.

## Writing a scenario

```powershell
Register-Scenario -Area tabs -Name 'pin and unpin a tab' -Tags smoke -Todo 'Pin, duplicate, mute; ...' -Body {
    $null = Open-TestPage '/page?title=One' -Engine gecko -Title 'One'      # navigate and wait
    Invoke-Mosaic 'pin' | Out-Null                                           # "Unknown command" -> pending
    $null = Require-Field (Get-SelectedTab (Get-MosaicState)) 'pinned' -Owner 'tabs'
    $null = Wait-State 'pinned' { param($s) (Get-Field (Get-SelectedTab $s) 'pinned') -eq $true }
    $null = Save-Screenshot 'pinned'
}
```

Useful functions (all in `E2E.psm1` / `Fixtures.ps1`): `Invoke-Mosaic` (`-Out`, `-Extra` for
macOS commands that read more request fields, `-AllowError`), `Get-MosaicState`, `Get-Field`
(dotted paths, arrays element by element: wrap in `@()`), `Require-Field`, `Wait-State`,
`Wait-Until`, `Wait-OutputFile` (for `out` files, which macOS-style commands write later),
`Open-TestPage`, `Set-Engine`, `Reset-Tabs`, `Invoke-OnEngines` (run a check on Gecko and
Chromium and report both), `New-Token` / `Wait-Report` (page reports), `Get-TestRequests` (what
the server received, with headers), `Save-Screenshot`, `Restart-MosaicApp [-Kill]`,
`Skip-Pending`, `Find-TextInFolder`, `Get-ZoneIdentifier`.

Rules: use the macOS command and dump names; keep scripts ASCII (Windows PowerShell 5.1 reads
BOM-less scripts as ANSI); never use `$pid`, `$home`, `$input`, `$args` as variable names; write
"`${name}:`" rather than "`$name:`" in strings.

## Results by area

Updated after each run against `win-next`. "Pending" means the area's commands or dump fields
are not in `win-next` yet.

<!-- results:start -->
*No run against the full app yet: `win-next` is the WinUI skeleton (no automation pipe) until the
winui area's M1 lands.*
<!-- results:end -->
