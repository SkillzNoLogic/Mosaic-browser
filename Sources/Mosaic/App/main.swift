import AppKit
import CEFBridge

// Mosaic runs on an AppKit lifecycle (with SwiftUI views inside) because
// Chromium has to own the application class and the event loop.

// Must be the first touch of NSApp: it fixes the application's class.
let application = MosaicApplication.shared

// One instance only: both engines keep a profile that cannot be shared.
// An isolated test profile (MOSAIC_DATA_DIR) has its own engine profiles, so it may run
// beside the user's copy.
if !AppPaths.isIsolatedProfile, let bundleID = Bundle.main.bundleIdentifier {
    let others = NSRunningApplication.runningApplications(withBundleIdentifier: bundleID)
        .filter { $0.processIdentifier != getpid() }
    if let running = others.first {
        running.activate()
        exit(0)
    }
}

application.setActivationPolicy(.regular)

let chromiumStarted = MosaicCEF.start(
    withArgc: CommandLine.argc,
    argv: CommandLine.unsafeArgv,
    dataDirectory: AppPaths.chromiumData.path)
if !chromiumStarted {
    NSLog("Mosaic: Chromium engine unavailable (bundled: %d); continuing with Gecko only.",
          MosaicCEF.isBundled ? 1 : 0)
}

// Top-level code runs on the main thread; say so for the compiler.
MainActor.assumeIsolated {
    let delegate = AppDelegate()
    application.delegate = delegate
    // NSApplication holds its delegate weakly; the quit handler keeps it alive.
    MosaicCEF.willQuitHandler = { MainActor.assumeIsolated { delegate.prepareToQuit() } }
    delegate.setUp(chromiumAvailable: chromiumStarted)
}

MosaicCEF.runEventLoop()

// Reached only when Chromium ran the loop; otherwise NSApplication exits itself.
MosaicCEF.shutdown()
exit(0)
