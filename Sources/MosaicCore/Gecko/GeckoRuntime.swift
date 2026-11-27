import Foundation

/// Locates a Gecko build Mosaic can run as its Mozilla engine.
public struct GeckoRuntime: Equatable, Sendable {
    public let appURL: URL
    public let executableURL: URL
    public let name: String
    public let version: String
    /// True for the private copy installed by `Scripts/bootstrap.sh`.
    public let isManaged: Bool

    /// The private runtime Mosaic prefers: `~/Library/Application Support/Mosaic/Engines/Firefox.app`.
    public static func managedAppURL(applicationSupport: URL) -> URL {
        applicationSupport.appendingPathComponent("Engines/Firefox.app", isDirectory: true)
    }

    /// Preference order: Mosaic's own copy, then any Gecko browser already on the Mac.
    public static func candidates(applicationSupport: URL) -> [URL] {
        var urls = [managedAppURL(applicationSupport: applicationSupport)]
        let names = [
            "Firefox.app", "Firefox Developer Edition.app", "Firefox Nightly.app",
            "LibreWolf.app", "Zen.app", "Zen Browser.app",
        ]
        let home = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications")
        for folder in [URL(fileURLWithPath: "/Applications"), home] {
            urls += names.map { folder.appendingPathComponent($0, isDirectory: true) }
        }
        return urls
    }

    public static func locate(applicationSupport: URL) -> GeckoRuntime? {
        let managed = managedAppURL(applicationSupport: applicationSupport)
        for url in candidates(applicationSupport: applicationSupport) {
            if let runtime = GeckoRuntime(appURL: url, isManaged: url == managed) { return runtime }
        }
        return nil
    }

    public init?(appURL: URL, isManaged: Bool = false) {
        guard let bundle = Bundle(url: appURL), let executable = bundle.executableURL,
              FileManager.default.isExecutableFile(atPath: executable.path)
        else { return nil }
        // Every Gecko desktop build ships its engine as XUL.framework/XUL.
        let xul = appURL.appendingPathComponent("Contents/MacOS/XUL")
        guard FileManager.default.fileExists(atPath: xul.path) else { return nil }
        self.appURL = appURL
        self.executableURL = executable
        self.name = (bundle.infoDictionary?["CFBundleName"] as? String) ?? "Gecko"
        self.version = (bundle.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "?"
        self.isManaged = isManaged
    }
}

/// Screen rectangle in Gecko's coordinates: CSS pixels, origin at the top-left
/// corner of the primary display.
public struct GeckoRect: Equatable, Sendable {
    public var x: Int
    public var y: Int
    public var width: Int
    public var height: Int

    public init(x: Int, y: Int, width: Int, height: Int) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }
}

/// The dedicated Gecko profile Mosaic runs its engine with. Keeping it separate
/// means the user's own Firefox profile, if they have one, is never read or changed.
public enum GeckoProfile {
    /// Writes the files that must be in place before Gecko starts.
    public static func prepare(at profile: URL, initialRect: GeckoRect?) throws {
        let fileManager = FileManager.default
        let chrome = profile.appendingPathComponent("chrome", isDirectory: true)
        try fileManager.createDirectory(at: chrome, withIntermediateDirectories: true)
        try Data(userJS.utf8).write(to: profile.appendingPathComponent("user.js"))
        try Data(userChromeCSS.utf8).write(to: chrome.appendingPathComponent("userChrome.css"))

        // Open the window where it will be docked, instead of wherever Gecko fancies.
        if let rect = initialRect {
            let geometry: [String: Any] = [
                "chrome://browser/content/browser.xhtml": [
                    "main-window": [
                        "screenX": String(rect.x), "screenY": String(rect.y),
                        "width": String(rect.width), "height": String(rect.height),
                        "sizemode": "normal",
                    ]
                ]
            ]
            let data = try JSONSerialization.data(withJSONObject: geometry)
            try data.write(to: profile.appendingPathComponent("xulstore.json"))
        }
    }

    /// Preferences applied on every start. Grouped by why Mosaic needs them.
    static let userJS = """
    // Written by Mosaic on every launch; edits here are overwritten.

    // -- Remote control: lets Mosaic load its agent into Gecko over a private socket.
    user_pref("devtools.debugger.remote-enabled", true);
    user_pref("devtools.chrome.enabled", true);
    user_pref("devtools.debugger.prompt-connection", false);

    // -- Mosaic draws the tabs and toolbar, so Gecko's own are hidden (chrome/userChrome.css).
    user_pref("toolkit.legacyUserProfileCustomizations.stylesheets", true);
    user_pref("browser.tabs.inTitlebar", 1);
    user_pref("sidebar.revamp", false);
    user_pref("full-screen-api.ignore-widgets", true);

    // -- One window, tabs only: Mosaic mirrors Gecko's tab list.
    user_pref("browser.link.open_newwindow", 3);
    user_pref("browser.link.open_newwindow.restriction", 0);
    user_pref("browser.tabs.closeWindowWithLastTab", false);
    user_pref("browser.tabs.warnOnClose", false);
    user_pref("browser.warnOnQuit", false);
    user_pref("browser.sessionstore.resume_from_crash", false);
    user_pref("browser.sessionstore.max_resumed_crashes", 0);
    user_pref("browser.startup.page", 0);
    user_pref("browser.startup.homepage", "about:blank");
    user_pref("browser.newtabpage.enabled", false);

    // -- No first-run pages, default-browser prompts or tours.
    user_pref("browser.shell.checkDefaultBrowser", false);
    user_pref("browser.aboutwelcome.enabled", false);
    user_pref("browser.preonboarding.enabled", false);
    user_pref("browser.startup.homepage_override.mstone", "ignore");
    user_pref("startup.homepage_welcome_url", "");
    user_pref("startup.homepage_welcome_url.additional", "");
    user_pref("startup.homepage_override_url", "");
    user_pref("browser.uitour.enabled", false);
    user_pref("datareporting.policy.dataSubmissionEnabled", false);
    user_pref("datareporting.healthreport.uploadEnabled", false);
    user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);
    user_pref("app.update.auto", false);

    // -- Converted Chrome extensions are unsigned by definition.
    user_pref("xpinstall.signatures.required", false);
    user_pref("extensions.autoDisableScopes", 0);
    user_pref("extensions.manifestV3.enabled", true);

    """

    /// Hides every piece of Firefox UI so only web content is left in the window.
    static let userChromeCSS = """
    /* Written by Mosaic on every launch; edits here are overwritten. */
    /* Removed from layout entirely. Merely collapsing it leaves Gecko's native
       window buttons (positioned from the title-bar button box inside it)
       poking into the corner of the page as a few stray pixels. */
    #navigator-toolbox {
      display: none !important;
    }
    #sidebar-box, #sidebar-splitter, #sidebar-main, #sidebar-launcher-splitter,
    #ai-window-box, #ai-window-splitter {
      display: none !important;
    }
    /* Newer Firefox (161; not ESR 153) draws a 1px separator as a top border on
       .browserContainer, which would show as a line along the top of every page. */
    #browser, #appcontent, #tabbrowser-tabbox, #tabbrowser-tabpanels, .browserContainer, .browserStack {
      margin: 0 !important;
      border: none !important;
      border-radius: 0 !important;
      box-shadow: none !important;
    }
    :root {
      --tabpanel-background-color: transparent !important;
    }

    """
}
