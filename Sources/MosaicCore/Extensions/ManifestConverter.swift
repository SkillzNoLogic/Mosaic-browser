import Foundation

/// Something the user should know about how a Chrome extension will behave on Gecko.
public struct CompatibilityNote: Codable, Hashable, Sendable {
    public enum Severity: String, Codable, Sendable {
        /// Adapted automatically; nothing to do.
        case info
        /// A feature of the extension will be missing or behave differently.
        case warning
        /// The extension depends on something Gecko does not have; expect breakage.
        case blocker
    }

    public let severity: Severity
    public let message: String

    public init(_ severity: Severity, _ message: String) {
        self.severity = severity
        self.message = message
    }
}

/// Rewrites a Chrome `manifest.json` into one Gecko will load.
///
/// Gecko and Chromium share the WebExtensions API surface, so most of a manifest
/// passes through untouched. This converter handles the places where they differ:
/// the add-on ID, MV3 service workers (Gecko runs them as event pages), a few
/// keys with different spellings, and Chrome-only capabilities, which are
/// reported as `CompatibilityNote`s rather than silently dropped.
public enum ManifestConverter {
    public struct Result {
        public var manifest: [String: Any]
        public var notes: [CompatibilityNote]
    }

    /// File name of the compatibility shim injected ahead of background scripts.
    public static let shimFileName = "__mosaic_chrome_shim.js"

    /// Gecko add-on ID used for a converted Chrome extension.
    public static func geckoID(forChromeID id: String) -> String { "\(id)@crx.mosaic" }

    /// - Parameters:
    ///   - manifest: the parsed Chrome manifest.
    ///   - chromeID: the extension's Chrome ID.
    ///   - readFile: returns the text of a file inside the extension (path relative
    ///     to its root), used to resolve `importScripts()` in service workers.
    public static func convert(
        _ manifest: [String: Any],
        chromeID: String,
        readFile: (String) -> String? = { _ in nil }
    ) -> Result {
        var out = manifest
        var notes: [CompatibilityNote] = []
        let version = (manifest["manifest_version"] as? Int) ?? 2

        // --- Things that are not extensions Gecko can host at all ----------------
        if manifest["app"] != nil {
            notes.append(.init(.blocker, "This is a Chrome App, not an extension. Chrome Apps cannot run on Gecko."))
        }
        if manifest["theme"] != nil {
            notes.append(.init(.blocker, "This is a Chrome theme. Chrome and Gecko themes use different formats."))
        }
        if manifest["nacl_modules"] != nil || manifest["plugins"] != nil {
            notes.append(.init(.blocker, "Uses native plug-in code (NaCl/NPAPI), which Gecko does not support."))
        }

        // --- Identity ---------------------------------------------------------
        var settings = (out["browser_specific_settings"] as? [String: Any])
            ?? (out["applications"] as? [String: Any]) ?? [:]
        var gecko = (settings["gecko"] as? [String: Any]) ?? [:]
        if (gecko["id"] as? String)?.isEmpty ?? true {
            gecko["id"] = geckoID(forChromeID: chromeID)
        }
        // Chrome extensions never declare a Gecko version floor that means anything here.
        gecko.removeValue(forKey: "update_url")
        settings["gecko"] = gecko
        out["browser_specific_settings"] = settings
        out.removeValue(forKey: "applications")

        // --- Chrome-only bookkeeping keys ---------------------------------------
        for key in ["update_url", "key", "minimum_chrome_version", "differential_fingerprint"] {
            out.removeValue(forKey: key)
        }

        // --- Background ---------------------------------------------------------
        if var background = out["background"] as? [String: Any] {
            if let worker = background["service_worker"] as? String {
                var scripts = (background["scripts"] as? [String]) ?? []
                if scripts.isEmpty {
                    let isModule = (background["type"] as? String) == "module"
                    if !isModule, let source = readFile(worker) {
                        scripts += importedScripts(in: source, relativeTo: worker)
                    }
                    scripts.append(worker)
                }
                background["scripts"] = [shimFileName] + scripts.filter { $0 != shimFileName }
                background.removeValue(forKey: "service_worker")
                background.removeValue(forKey: "persistent")
                notes.append(.init(.info, "Background service worker runs as a Gecko event page."))
            } else if var scripts = background["scripts"] as? [String] {
                scripts.removeAll { $0 == shimFileName }
                background["scripts"] = [shimFileName] + scripts
                if version >= 3 { background.removeValue(forKey: "persistent") }
            }
            out["background"] = background
        }

        // --- Keys with a different spelling -------------------------------------
        if let optionsPage = out["options_page"] as? String {
            if out["options_ui"] == nil {
                out["options_ui"] = ["page": optionsPage, "open_in_tab": true]
            }
            out.removeValue(forKey: "options_page")
        }
        if (out["incognito"] as? String) == "split" {
            out["incognito"] = "spanning"
            notes.append(.init(.warning, "Split incognito mode is not available on Gecko; private windows share the extension's normal context."))
        }
        if let author = out["author"], !(author is String) {
            if let email = (author as? [String: Any])?["email"] as? String {
                out["author"] = email
            } else {
                out.removeValue(forKey: "author")
            }
        }
        if version >= 3, out["browser_action"] != nil, out["action"] == nil {
            out["action"] = out.removeValue(forKey: "browser_action")
        }

        // --- Capabilities Gecko lacks -------------------------------------------
        for key in ["permissions", "optional_permissions"] {
            guard let listed = out[key] as? [Any] else { continue }
            var kept: [Any] = []
            for entry in listed {
                guard let name = entry as? String else { kept.append(entry); continue }
                if let reason = unsupportedPermissions[name] ?? unsupportedPermissionPrefix(name) {
                    notes.append(.init(reason.severity, "Permission “\(name)”: \(reason.message)"))
                } else {
                    kept.append(entry)
                    if let caveat = caveatPermissions[name] {
                        notes.append(.init(.warning, "Permission “\(name)”: \(caveat)"))
                    }
                }
            }
            out[key] = kept
        }

        if out["side_panel"] != nil {
            out.removeValue(forKey: "side_panel")
            notes.append(.init(.warning, "Chrome side panel is not available on Gecko; the panel UI will not appear."))
        }
        if out["externally_connectable"] != nil {
            notes.append(.init(.warning, "Web pages cannot message this extension on Gecko (externally_connectable is not supported)."))
        }
        if out["sandbox"] != nil {
            notes.append(.init(.warning, "Sandboxed extension pages are not supported on Gecko."))
        }
        if out["oauth2"] != nil {
            notes.append(.init(.warning, "Google sign-in through chrome.identity.getAuthToken is Chrome-only; sign-in may not work."))
        }
        if out["chrome_settings_overrides"] != nil {
            notes.append(.init(.info, "Search/homepage overrides are ignored inside Mosaic."))
        }
        for key in ["file_browser_handlers", "file_system_provider_capabilities", "input_components", "tts_engine"] where out[key] != nil {
            notes.append(.init(.warning, "“\(key)” is a ChromeOS/Chrome-only feature and will be ignored."))
        }

        return Result(manifest: out, notes: dedupe(notes))
    }

    /// Chrome APIs that have no Gecko counterpart, mapped to what the user should expect.
    static let unsupportedPermissions: [String: (severity: CompatibilityNote.Severity, message: String)] = [
        "debugger": (.blocker, "the Chrome DevTools debugger API does not exist on Gecko."),
        "offscreen": (.blocker, "offscreen documents do not exist on Gecko."),
        "sidePanel": (.warning, "the side panel API does not exist on Gecko."),
        "tabCapture": (.blocker, "tab capture does not exist on Gecko."),
        "desktopCapture": (.blocker, "desktop capture does not exist on Gecko."),
        "pageCapture": (.warning, "saving pages as MHTML does not exist on Gecko."),
        "declarativeContent": (.warning, "declarativeContent rules do not exist on Gecko."),
        "background": (.info, "Chrome's run-in-background mode does not apply."),
        "contentSettings": (.warning, "per-site content settings cannot be changed by extensions on Gecko."),
        "fontSettings": (.warning, "font settings cannot be changed by extensions on Gecko."),
        "gcm": (.warning, "Google Cloud Messaging is Chrome-only."),
        "processes": (.warning, "the processes API is Chrome-only."),
        "power": (.warning, "the power API is Chrome-only."),
        "readingList": (.warning, "the reading list API is Chrome-only."),
        "tts": (.warning, "text-to-speech is Chrome-only."),
        "ttsEngine": (.warning, "text-to-speech engines are Chrome-only."),
        "favicon": (.warning, "the favicon API is Chrome-only; site icons inside the extension may be missing."),
        "printing": (.warning, "the printing API is ChromeOS-only."),
        "printerProvider": (.warning, "the printer provider API is Chrome-only."),
        "documentScan": (.warning, "document scanning is ChromeOS-only."),
        "loginState": (.warning, "the loginState API is ChromeOS-only."),
        "platformKeys": (.warning, "the platformKeys API is ChromeOS-only."),
        "certificateProvider": (.warning, "the certificate provider API is ChromeOS-only."),
        "fileSystemProvider": (.warning, "the file system provider API is ChromeOS-only."),
        "vpnProvider": (.warning, "the VPN provider API is ChromeOS-only."),
        "wallpaper": (.warning, "the wallpaper API is ChromeOS-only."),
        "audio": (.warning, "the audio API is ChromeOS-only."),
        "webAuthenticationProxy": (.warning, "the WebAuthn proxy API is Chrome-only."),
    ]

    /// Permissions Gecko accepts but implements only partly.
    static let caveatPermissions: [String: String] = [
        "identity": "only the launchWebAuthFlow sign-in flow works on Gecko.",
        "nativeMessaging": "needs the companion desktop app to be registered for Firefox as well as Chrome.",
    ]

    private static func unsupportedPermissionPrefix(
        _ name: String
    ) -> (severity: CompatibilityNote.Severity, message: String)? {
        for prefix in ["enterprise.", "system.", "accessibilityFeatures."] where name.hasPrefix(prefix) {
            return (.warning, "this Chrome-only API does not exist on Gecko.")
        }
        return nil
    }

    /// Chrome-only API namespaces to look for in an extension's scripts, since
    /// many are used without a matching manifest permission.
    public static let chromeOnlyAPIs: [(needle: String, note: CompatibilityNote)] = [
        ("chrome.sidePanel", .init(.warning, "Uses chrome.sidePanel, which does not exist on Gecko.")),
        ("chrome.offscreen", .init(.blocker, "Uses chrome.offscreen, which does not exist on Gecko.")),
        ("chrome.debugger", .init(.blocker, "Uses chrome.debugger, which does not exist on Gecko.")),
        ("chrome.tabCapture", .init(.blocker, "Uses chrome.tabCapture, which does not exist on Gecko.")),
        ("chrome.desktopCapture", .init(.blocker, "Uses chrome.desktopCapture, which does not exist on Gecko.")),
        ("chrome.identity.getAuthToken", .init(.warning, "Uses chrome.identity.getAuthToken (Google sign-in), which is Chrome-only.")),
        ("chrome.declarativeContent", .init(.warning, "Uses chrome.declarativeContent, which does not exist on Gecko.")),
    ]

    /// Scans script text for Chrome-only APIs.
    public static func apiNotes(inScript source: String) -> [CompatibilityNote] {
        chromeOnlyAPIs.filter { source.contains($0.needle) }.map(\.note)
    }

    /// Finds `importScripts("a.js", "b.js")` calls in a service worker and returns
    /// the imported paths relative to the extension root, in call order. Gecko
    /// event pages have no `importScripts`, so these are loaded as ordinary
    /// background scripts ahead of the worker itself.
    static func importedScripts(in source: String, relativeTo workerPath: String) -> [String] {
        guard let call = try? NSRegularExpression(pattern: #"importScripts\s*\(([^)]*)\)"#),
              let literal = try? NSRegularExpression(pattern: #"["'`]([^"'`]+)["'`]"#)
        else { return [] }

        let workerDirectory = (workerPath as NSString).deletingLastPathComponent
        var result: [String] = []
        let text = source as NSString
        for match in call.matches(in: source, range: NSRange(location: 0, length: text.length)) {
            let arguments = text.substring(with: match.range(at: 1))
            let argumentText = arguments as NSString
            for item in literal.matches(in: arguments, range: NSRange(location: 0, length: argumentText.length)) {
                let path = argumentText.substring(with: item.range(at: 1))
                guard !path.contains("://"), !path.contains("${") else { continue }
                let joined = path.hasPrefix("/")
                    ? path : (workerDirectory as NSString).appendingPathComponent(path)
                guard let resolved = normalize(joined) else { continue }
                if !result.contains(resolved) { result.append(resolved) }
            }
        }
        return result
    }

    /// Collapses `.` and `..` in a path relative to the extension root. Returns
    /// nil for a path that would climb out of the extension.
    static func normalize(_ path: String) -> String? {
        var parts: [Substring] = []
        for part in path.split(separator: "/", omittingEmptySubsequences: true) {
            switch part {
            case ".": continue
            case "..":
                guard !parts.isEmpty else { return nil }
                parts.removeLast()
            default: parts.append(part)
            }
        }
        return parts.isEmpty ? nil : parts.joined(separator: "/")
    }

    private static func dedupe(_ notes: [CompatibilityNote]) -> [CompatibilityNote] {
        var seen = Set<CompatibilityNote>()
        return notes.filter { seen.insert($0).inserted }
    }

    /// Loaded first in the background context of every converted extension.
    ///
    /// It papers over the differences between a Chrome service worker and a Gecko
    /// event page, and stubs Chrome-only API namespaces so that a top-level call
    /// such as `chrome.sidePanel.setPanelBehavior(...)` degrades to a no-op instead
    /// of throwing and taking the whole background script down with it.
    public static let shimSource = #"""
    // Generated by Mosaic. Chrome → Gecko compatibility shim.
    (() => {
      "use strict";
      const g = globalThis;

      // Service-worker globals that do not exist in an event page.
      if (typeof g.importScripts !== "function") {
        // Imports are hoisted into manifest.background.scripts by Mosaic.
        g.importScripts = () => {};
      }
      if (typeof g.skipWaiting !== "function") g.skipWaiting = async () => {};
      if (!g.clients) {
        g.clients = { claim: async () => {}, matchAll: async () => [], get: async () => undefined };
      }
      if (!g.registration) g.registration = { scope: g.location ? g.location.origin + "/" : "" };

      // Deliver the worker lifecycle events once, since nothing else will.
      const lifecycle = new Set(["install", "activate"]);
      const nativeAdd = g.addEventListener.bind(g);
      g.addEventListener = (type, listener, options) => {
        if (lifecycle.has(type) && typeof listener === "function") {
          Promise.resolve().then(() => {
            try { listener({ type, waitUntil() {}, target: g }); } catch (error) { console.error(error); }
          });
          return;
        }
        nativeAdd(type, listener, options);
      };

      // Chrome-only API namespaces → inert stubs.
      const api = g.chrome ?? g.browser;
      if (!api) return;
      const event = { addListener() {}, removeListener() {}, hasListener() { return false; } };
      const stub = (path) => new Proxy(function () {}, {
        get(_target, property) {
          if (property === "then" || typeof property === "symbol") return undefined;
          if (/^on[A-Z]/.test(property)) return event;
          return stub(`${path}.${property}`);
        },
        apply() {
          console.warn(`[Mosaic] ${path}() is not available on Gecko; call ignored.`);
          return Promise.resolve(undefined);
        },
        construct() { return {}; },
      });
      const missing = [
        "sidePanel", "offscreen", "debugger", "tabCapture", "desktopCapture", "pageCapture",
        "declarativeContent", "contentSettings", "fontSettings", "gcm", "instanceID", "processes",
        "power", "readingList", "tts", "ttsEngine", "system", "enterprise", "loginState",
        "platformKeys", "certificateProvider", "fileSystemProvider", "vpnProvider", "wallpaper",
        "audio", "printing", "printerProvider", "documentScan", "webAuthenticationProxy",
      ];
      for (const name of missing) {
        try {
          if (api[name] === undefined) api[name] = stub(`chrome.${name}`);
        } catch (_) { /* namespace object is sealed; nothing to do */ }
      }
    })();
    """#
}
