# Windows — saved for later

Windows work is paused while the macOS app gets its feature and UI pass. This file keeps
the Windows requirements from the product brief, and where the Windows code stands, so
nothing is lost when work resumes.

## What exists today

- **Branch `windows`** (pushed): a C#/.NET 10 + WPF port of Mosaic v0.1. It builds,
  passes 783 tests on a real Windows runner (GitHub Actions), and its smoke test drives
  the app on Windows: Gecko pages (embedded as a real child window), Chromium tabs
  (WebView2), search engines, Chrome extensions converted for Gecko, and the built-in
  uBlock Origin blocker in both engines. See `windows/README.md` on that branch.
- **`~/mosiac/Win`** (the owner's separate Xcode-era project, not in this repo): a C++20 /
  WinUI 3 implementation written on a Mac and never compiled, with its own `// VERIFY:`
  risk list.
- The Gecko agent (`Resources/gecko/agent.sys.mjs`) is shared byte-for-byte between the
  macOS app and the `windows` branch. Keep it that way.

## Decision needed before resuming

The brief asks for **C++ with WinUI 3** on Windows. The working port is **C# with WPF**.
Options: keep the C# port and move its UI to WinUI 3 (C#), or rewrite in C++/WinUI 3
using the C# port as the behavioural reference (its tests describe the behaviour).

## Windows requirements from the brief

- **Language and UI:** C++ (C++/WinRT), WinUI 3 with the Windows App SDK.
- **Materials:** Acrylic (and Mica where appropriate), Windows 11 rounded corners and
  shadows, with fallbacks when transparency is off.
- **Icons:** Fluent System Icons.
- **Windowing:** native title bar and caption buttons, Snap Layouts, resize/maximise/minimise,
  virtual desktops.
- **Animations:** GPU-accelerated via Windows composition APIs.
- **System integration:** Credential Manager / DPAPI for secrets, native context menus,
  notifications, keyboard shortcuts, drag and drop, shell integration (default browser).
- **Performance:** x64 and ARM64; low memory; fast startup; measured, not assumed.
- **Platform-specific Mosaic:** Microsoft Edge (Windows only): Edge-style tab and toolbar
  layout, horizontal and vertical tab modes, Edge-like address bar, Acrylic styling, Fluent
  controls, Edge-style sidebar, rounded tabs and menus, workspace organisation, native
  window behaviour.
- **Shared with macOS (parity required):** Zen/Arc, Chrome, Opera/Opera GX Mosaics; the
  Engine category with Chromium and Gecko; every settings category; tabs, workspaces,
  bookmarks, history, downloads, privacy, passwords, extensions and productivity features;
  the same data formats so profiles and configurations move between platforms.
- **Do not port:** the Safari Mosaic (macOS only).

## Windows-specific notes already learned

- Gecko on Windows is embedded with `SetParent`; Firefox's launcher process must be
  handled (`--wait-for-browser`), and the profile lock must be awaited after stopping.
- Newer Firefox (161) draws a 1px `.browserContainer` border; the shared profile CSS hides it.
- The default window must fit small screens (1024×768 runners, 1366×768 laptops).
- WebView2's `WebResourceRequested` (three-argument filter) handles blocking for
  Chromium tabs; ClearScript V8 runs uBlock Origin's engine.

## Parity work to do when Windows resumes

Everything ticked in `To Do.md` after the v0.1 port is Windows work owed: the new UI shell
and Mosaics, in-window Settings, tabs/workspaces/sessions, library, downloads (and Free
Download Manager on Windows), privacy, passwords, productivity, engine settings with an
in-app Gecko installer, the layout canvas and Gradient Studio.
