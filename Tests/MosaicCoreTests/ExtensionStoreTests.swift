import Foundation
import Testing
@testable import MosaicCore

@Suite struct ExtensionStoreTests {
    static let manifestV3 = """
    {
      // Chrome allows comments in manifests.
      "manifest_version": 3,
      "name": "__MSG_appName__",
      "description": "__MSG_appDesc__",
      "version": "2.5.1",
      "default_locale": "en",
      "icons": { "16": "icons/16.png", "128": "/icons/128.png", "48": "icons/48.png" },
      "action": { "default_popup": "popup.html" },
      "options_page": "options.html",
      "background": { "service_worker": "sw.js" },
      "permissions": ["storage", "offscreen"]
    }
    """

    static let files: [String: String] = [
        "manifest.json": manifestV3,
        "_locales/en/messages.json": #"{"appName": {"message": "Test Extension"}, "appDesc": {"message": "Does things"}}"#,
        "sw.js": #"importScripts("lib.js"); chrome.sidePanel.setPanelBehavior({});"#,
        "lib.js": "var lib = 1;",
        "popup.html": "<p>popup</p>",
        "options.html": "<p>options</p>",
        "icons/128.png": "png",
        "_metadata/verified_contents.json": "{}",
    ]

    let chromeID = "abcdefghijklmnopabcdefghijklmnop"

    private func makeStore() throws -> (ExtensionStore, URL) {
        let root = try Fixtures.temporaryDirectory()
        return (try ExtensionStore(root: root), root)
    }

    private func crx() throws -> Data {
        let zip = [UInt8](try Fixtures.zip(Self.files))
        let idBytes: [UInt8] = (0..<16).map { UInt8($0 << 4 | ($0 + 1) % 16) }
        return Fixtures.crx3(id: idBytes, zip: zip)
    }

    @Test func installsConvertsAndRecordsAnExtension() throws {
        let (store, root) = try makeStore()
        defer { try? FileManager.default.removeItem(at: root) }

        let record = try store.install(package: try crx(), knownID: chromeID)

        #expect(record.id == chromeID)
        #expect(record.geckoID == "\(chromeID)@crx.mosaic")
        #expect(record.name == "Test Extension")
        #expect(record.summary == "Does things")
        #expect(record.version == "2.5.1")
        #expect(record.manifestVersion == 3)
        #expect(record.enabled)
        #expect(record.hasPopup)
        #expect(record.hasOptions)
        #expect(record.iconPath == "icons/128.png")
        #expect(record.worstSeverity == .blocker)
        #expect(record.notes.contains { $0.message.contains("offscreen") })
        #expect(record.notes.contains { $0.message.contains("chrome.sidePanel") })

        let gecko = store.geckoDirectory(for: chromeID)
        let manifest = try #require(
            try JSONSerialization.jsonObject(with: Data(contentsOf: gecko.appendingPathComponent("manifest.json")))
                as? [String: Any])
        let scripts = (manifest["background"] as? [String: Any])?["scripts"] as? [String]
        #expect(scripts == [ManifestConverter.shimFileName, "lib.js", "sw.js"])
        #expect(manifest["permissions"] as? [String] == ["storage"])

        let fm = FileManager.default
        #expect(fm.fileExists(atPath: gecko.appendingPathComponent(ManifestConverter.shimFileName).path))
        #expect(fm.fileExists(atPath: gecko.appendingPathComponent("popup.html").path))
        #expect(!fm.fileExists(atPath: gecko.appendingPathComponent("_metadata").path))
        #expect(fm.fileExists(atPath: store.sourcePackage(for: chromeID).path))
        // Nothing left behind from unpacking.
        let leftovers = try fm.contentsOfDirectory(atPath: root.path).filter { $0.hasPrefix(".staging") }
        #expect(leftovers.isEmpty)
    }

    @Test func idComesFromThePackageWhenNotSupplied() throws {
        let (store, root) = try makeStore()
        defer { try? FileManager.default.removeItem(at: root) }
        let record = try store.install(package: try crx())
        #expect(CRXArchive.isValidID(record.id))
        #expect(record.id == (try CRXArchive.parse(try crx())).extensionID)
    }

    @Test func libraryPersistsAcrossInstances() throws {
        let (store, root) = try makeStore()
        defer { try? FileManager.default.removeItem(at: root) }
        try store.install(package: try crx(), knownID: chromeID)
        try store.setEnabled(false, id: chromeID)

        let reopened = try ExtensionStore(root: root)
        #expect(reopened.extensions.count == 1)
        #expect(reopened.extensions.first?.name == "Test Extension")
        #expect(reopened.extensions.first?.enabled == false)
    }

    @Test func reinstallReplacesAndKeepsEnabledState() throws {
        let (store, root) = try makeStore()
        defer { try? FileManager.default.removeItem(at: root) }
        try store.install(package: try crx(), knownID: chromeID)
        try store.setEnabled(false, id: chromeID)
        let again = try store.install(package: try crx(), knownID: chromeID)
        #expect(store.extensions.count == 1)
        #expect(again.enabled == false)
    }

    @Test func removeDeletesFilesAndRecord() throws {
        let (store, root) = try makeStore()
        defer { try? FileManager.default.removeItem(at: root) }
        try store.install(package: try crx(), knownID: chromeID)
        try store.remove(id: chromeID)
        #expect(store.extensions.isEmpty)
        #expect(!FileManager.default.fileExists(atPath: store.directory(for: chromeID).path))
        #expect(try ExtensionStore(root: root).extensions.isEmpty)
    }

    @Test func rejectsPackagesWithoutAManifest() throws {
        let (store, root) = try makeStore()
        defer { try? FileManager.default.removeItem(at: root) }
        let zip = try Fixtures.zip(["readme.txt": "hello"])
        #expect(throws: ExtensionStore.StoreError.self) { try store.install(package: zip) }
        #expect(store.extensions.isEmpty)
    }

    @Test func rejectsFilesThatAreNotPackages() throws {
        let (store, root) = try makeStore()
        defer { try? FileManager.default.removeItem(at: root) }
        #expect(throws: CRXArchive.ParseError.notAnExtensionPackage) {
            try store.install(package: Data("<!doctype html>".utf8))
        }
    }

    @Test func aBrokenPackageDoesNotDisturbAnInstalledOne() throws {
        let (store, root) = try makeStore()
        defer { try? FileManager.default.removeItem(at: root) }
        try store.install(package: try crx(), knownID: chromeID)
        let broken = try Fixtures.zip(["manifest.json": "{ this is not json"])
        #expect(throws: ExtensionStore.StoreError.self) { try store.install(package: broken, knownID: chromeID) }
        #expect(store.extensions.first?.name == "Test Extension")
        #expect(FileManager.default.fileExists(
            atPath: store.geckoDirectory(for: chromeID).appendingPathComponent("manifest.json").path))
    }

    @Test func localisationFallsBackToTheRawValue() {
        #expect(ExtensionStore.localized("__MSG_missing__", [:]) == "__MSG_missing__")
        #expect(ExtensionStore.localized("__MSG_AppName__", ["appname": "Hi"]) == "Hi")
        #expect(ExtensionStore.localized("Plain", [:]) == "Plain")
        #expect(ExtensionStore.localized("", [:]) == nil)
    }
}

@Suite struct GeckoProfileTests {
    @Test func writesPreferencesStylesheetAndGeometry() throws {
        let profile = try Fixtures.temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: profile) }

        try GeckoProfile.prepare(at: profile, initialRect: GeckoRect(x: 10, y: 20, width: 800, height: 600))

        let prefs = try String(contentsOf: profile.appendingPathComponent("user.js"), encoding: .utf8)
        #expect(prefs.contains(#"user_pref("devtools.debugger.remote-enabled", true);"#))
        #expect(prefs.contains(#"user_pref("xpinstall.signatures.required", false);"#))
        #expect(prefs.contains(#"user_pref("browser.link.open_newwindow", 3);"#))
        // Every line is a comment, blank, or a complete user_pref call.
        for line in prefs.split(separator: "\n") {
            #expect(line.hasPrefix("//") || (line.hasPrefix("user_pref(") && line.hasSuffix(");")), "\(line)")
        }

        let css = try String(contentsOf: profile.appendingPathComponent("chrome/userChrome.css"), encoding: .utf8)
        #expect(css.contains("#navigator-toolbox"))

        let store = try #require(try JSONSerialization.jsonObject(
            with: Data(contentsOf: profile.appendingPathComponent("xulstore.json"))) as? [String: Any])
        let window = ((store["chrome://browser/content/browser.xhtml"] as? [String: Any])?["main-window"])
            as? [String: String]
        #expect(window == ["screenX": "10", "screenY": "20", "width": "800", "height": "600", "sizemode": "normal"])
    }

    @Test func leavesGeometryAloneWhenUnknown() throws {
        let profile = try Fixtures.temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: profile) }
        try GeckoProfile.prepare(at: profile, initialRect: nil)
        #expect(!FileManager.default.fileExists(atPath: profile.appendingPathComponent("xulstore.json").path))
    }

    @Test func runtimeLookupPrefersTheManagedCopy() throws {
        let support = try Fixtures.temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: support) }
        let candidates = GeckoRuntime.candidates(applicationSupport: support)
        #expect(candidates.first == GeckoRuntime.managedAppURL(applicationSupport: support))
        #expect(candidates.contains { $0.path == "/Applications/Firefox.app" })
        // A folder that merely has the right name is not a runtime.
        try FileManager.default.createDirectory(
            at: GeckoRuntime.managedAppURL(applicationSupport: support), withIntermediateDirectories: true)
        #expect(GeckoRuntime(appURL: GeckoRuntime.managedAppURL(applicationSupport: support)) == nil)
    }
}
