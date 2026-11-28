# Mosaic for Windows: performance

"x64 and ARM64; low memory; fast startup; measured, not assumed" (`docs/windows-later.md`).
This page says how Mosaic is measured, what was measured, and what fails the build.

<!-- NUMBERS -->

## How it is measured

Everything runs on GitHub's hosted runners, on every push to `win-next` and `win2/perf` and on
request (`.github/workflows/windows-perf.yml`):

| | x64 | ARM64 |
| --- | --- | --- |
| Runner | `windows-latest` (Windows Server 2025) | `windows-11-arm` (Windows 11) |
| Build | `scripts/build.ps1 -Runtime win-x64` | `scripts/build.ps1 -Runtime win-arm64` |
| Gecko | Firefox ESR x64 from `bootstrap.ps1` | Firefox ESR ARM64 from `bootstrap.ps1` |

Both builds are native (no emulation; the harness notes it if the app's PE machine differs from
the machine's). The harness is `windows/scripts/perf/run-perf.ps1` (Windows PowerShell 5.1, no
dependencies); run it on any PC after `build.ps1` and `bootstrap.ps1`:

```powershell
.\windows\scripts\perf\run-perf.ps1                      # everything, ~15 min
.\windows\scripts\perf\run-perf.ps1 -Scenario startup -WarmIterations 10
.\windows\scripts\perf\check-budgets.ps1                 # against windows\perf-budgets.json
```

Every launch gets its own data folder (`MOSAIC_DATA_DIR`), `--mosaic-automation` (so the
harness can drive and quit it) and `MOSAIC_STARTUP_TRACE=<file>`, which makes the app write
its `StartupTrace` marks to that file as they happen. Test pages come from a local web server
in the harness (`/page/<n>`: ~60 KB of text, ~600 elements, an inline SVG, a 200-row table built
by script once; no timers or network), so no number depends on the Internet.

### Start-up

| Scenario | What it is |
| --- | --- |
| `firstRun` | empty data folder: the first launch on a PC (profiles are created) |
| `cold` | existing data folder, **Windows' file cache emptied first** (modified list flushed, standby list purged through `NtSetSystemInformation`, which needs an elevated process; CI is): as after a reboot |
| `warm` | existing data folder, files cached: an everyday launch |
| `warmChromium` | as `warm`, with Chromium as the engine for new tabs |

For each launch:

- **`windowShownMs`**, measured from outside the app: from the process creation time
  Windows records (`GetProcessTimes`) to the moment a top-level window of the process is
  visible, not owned, non-empty and **no longer cloaked by DWM** (a WinUI window stays cloaked
  until its first frame is composed), polled every millisecond with
  `GetSystemTimePreciseAsFileTime`.
- **StartupTrace marks**, measured inside the app from the same origin (the process creation
  time), so the runtime's own start-up is included: `main`, `appLaunched`, `windowCreated`,
  `windowActivated`, `firstFrame`, `modelReady`, `geckoReady`, `chromiumReady`,
  `automationReady`, `firstNavigationStarted`, `firstNavigationCompleted`.
- **`firstPageMs`**: process creation to `firstNavigationCompleted` when the app opened the
  address passed on its command line; when it does not (yet), the harness asks for the page
  over automation as soon as the pipe answers and reports only **`pageLoadMs`**
  (`firstNavigationCompleted − firstNavigationStarted`, both in-app).
- **`exitMs`**: "quit" to process exit.

Each metric is reported with n, median, p90 (nearest rank), min, max, mean and the samples.

### Memory and CPU

One launch per engine per iteration: load one test page, wait 10 s, measure; measure CPU for
30 s of doing nothing; open 9 more test pages (10 tabs), wait 10 s, measure; send the tabs
area's `unloadBackground` (sleeping tabs), wait 10 s, measure.

"Memory" is summed over **every process of the run**: Mosaic, its `msedgewebview2.exe` tree,
the private Firefox and its content processes (Gecko's tree is rooted at the PID the app reports
and at any engine process whose command line names the run's data folder, as Firefox's launcher
can cut it off from Mosaic's tree). Per group (`mosaic`, `webview2`, `gecko`, `other`) and in
total:

- **private working set** (`PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize`): RAM the run
  holds for itself; Task Manager's "Memory" column. This is the number budgets use.
- **commit** (`PrivateUsage`): private memory promised, in RAM or the page file.

Each memory figure is the median of three samples two seconds apart. Idle CPU is the kernel +
user time of all the run's processes over the interval, as a percentage of one core.

### In the app: `StartupTrace`

`Mosaic.Core.Performance.StartupTrace.Mark(name)` costs a timestamp and a short lock; only the
first mark of a name counts. The process start time is looked up only when the marks are read.
The marks are in the automation `dump` as `performance` (with process facts: managed heap, GC
counts, JIT-compiled methods and JIT time, working set, threads, handles; and runtime facts:
TieredPGO, InvariantGlobalization, server GC). The perf area places the marks it can without
touching other areas' files: `main` (Mosaic.App's module initializer, which runs just before
`Program.Main`), and from the `FeatureMounts.Startup` hook: `modelReady`, `windowActivated`,
`firstFrame` (first `CompositionTarget.Rendering`, then unsubscribed), `firstNavigation*` and
`geckoReady` (`StartupProbe`, from the model's events, dropped once marked). The rest need a line
in the winui area's files (see "Call sites").

## Budgets

`windows/perf-budgets.json`, per architecture. Each key is a metric of `results.json`;
`statistic` is `median` (default), `p90`, `min`, `max` or `mean`; the build fails when the
statistic exceeds `max`. `metric#p90` puts a second budget on the same metric. A metric a run
did not produce is "not measured" and fails only with `"required": true`.

**How the numbers are chosen.** From the CI baseline (median of the runs named in
`measuredIn`): time budgets are the baseline + 30 % (rounded up to 50 ms), at least
baseline + 150 ms, because hosted runners vary by ~10–20 % run to run; memory budgets
baseline + 20 %; idle CPU an absolute ceiling (a browser doing nothing must stay near zero).

**How to change a budget.**

1. A regression you accept, or an improvement you want to lock in: run the workflow
   (Actions › Windows performance › Run workflow, or push), download `perf-x64` and
   `perf-arm64`, and read `results.json` → `metrics`.
2. Edit `windows/perf-budgets.json` with the rule above, set `baseline` and `measuredIn` (the
   run id), and say why in the commit message.
3. New metric: add a key for both architectures. Removing a budget needs the same care as
   raising one.

Run `check-budgets.ps1 -Results <results.json>` locally to see the table CI prints.

<!-- PUBLISH -->

<!-- CALLSITES -->
