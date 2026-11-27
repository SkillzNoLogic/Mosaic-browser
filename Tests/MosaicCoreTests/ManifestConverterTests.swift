import Foundation
import Testing
@testable import MosaicCore

@Suite struct ManifestConverterTests {
    let id = "eimadpbcbfnmbkopoojfekhnkhdbieeh"

    private func convert(_ manifest: [String: Any], files: [String: String] = [:]) -> ManifestConverter.Result {
        ManifestConverter.convert(manifest, chromeID: id) { files[$0] }
    }

    private func geckoID(_ manifest: [String: Any]) -> String? {
        ((manifest["browser_specific_settings"] as? [String: Any])?["gecko"] as? [String: Any])?["id"] as? String
    }

    @Test func assignsAStableGeckoIDAndDropsChromeBookkeeping() {
        let result = convert([
            "manifest_version": 3, "name": "X", "version": "1.0",
            "update_url": "https://clients2.google.com/service/update2/crx",
            "key": "abc", "minimum_chrome_version": "120",
        ])
        #expect(geckoID(result.manifest) == "\(id)@crx.mosaic")
        #expect(result.manifest["update_url"] == nil)
        #expect(result.manifest["key"] == nil)
        #expect(result.manifest["minimum_chrome_version"] == nil)
        #expect(result.manifest["name"] as? String == "X")
    }

    @Test func keepsAnExistingGeckoID() {
        let result = convert([
            "manifest_version": 2,
            "browser_specific_settings": ["gecko": ["id": "real@example.org", "strict_min_version": "109.0"]],
        ])
        #expect(geckoID(result.manifest) == "real@example.org")
    }

    @Test func serviceWorkerBecomesEventPageWithShimFirst() {
        let result = convert(["manifest_version": 3, "background": ["service_worker": "bg/sw.js"]])
        let background = result.manifest["background"] as? [String: Any]
        #expect(background?["scripts"] as? [String] == [ManifestConverter.shimFileName, "bg/sw.js"])
        #expect(background?["service_worker"] == nil)
        #expect(result.notes.contains { $0.severity == .info && $0.message.contains("event page") })
    }

    @Test func importScriptsAreHoistedAheadOfTheWorker() {
        let worker = """
        importScripts("lib/a.js", 'lib/b.js');
        try { importScripts(`../shared/c.js`) } catch (e) {}
        importScripts("/root.js", "https://cdn.example/x.js", "lib/a.js");
        self.addEventListener("install", () => {});
        """
        let result = convert(
            ["manifest_version": 3, "background": ["service_worker": "bg/sw.js"]],
            files: ["bg/sw.js": worker])
        let scripts = (result.manifest["background"] as? [String: Any])?["scripts"] as? [String]
        #expect(scripts == [
            ManifestConverter.shimFileName, "bg/lib/a.js", "bg/lib/b.js", "shared/c.js", "root.js", "bg/sw.js",
        ])
    }

    @Test func importScriptsCannotEscapeTheExtension() {
        let found = ManifestConverter.importedScripts(
            in: #"importScripts("../../outside.js", "a/../../../outside.js", "ok.js", "./x/../y.js")"#,
            relativeTo: "bg/sw.js")
        #expect(found == ["bg/ok.js", "bg/y.js"])
    }

    @Test func normalizesPaths() {
        #expect(ManifestConverter.normalize("a/./b/../c.js") == "a/c.js")
        #expect(ManifestConverter.normalize("/root.js") == "root.js")
        #expect(ManifestConverter.normalize("a/../..") == nil)
        #expect(ManifestConverter.normalize("../x.js") == nil)
        #expect(ManifestConverter.normalize("") == nil)
    }

    @Test func moduleWorkersAreNotScannedForImportScripts() {
        let result = convert(
            ["manifest_version": 3, "background": ["service_worker": "sw.js", "type": "module"]],
            files: ["sw.js": #"importScripts("a.js")"#])
        let background = result.manifest["background"] as? [String: Any]
        #expect(background?["scripts"] as? [String] == [ManifestConverter.shimFileName, "sw.js"])
        #expect(background?["type"] as? String == "module")
    }

    @Test func existingBackgroundScriptsGetTheShimOnce() {
        let first = convert(["manifest_version": 2, "background": ["scripts": ["a.js"], "persistent": true]])
        let background = first.manifest["background"] as? [String: Any]
        #expect(background?["scripts"] as? [String] == [ManifestConverter.shimFileName, "a.js"])
        #expect(background?["persistent"] as? Bool == true)

        // Converting an already converted manifest must not stack shims.
        let second = convert(first.manifest)
        #expect((second.manifest["background"] as? [String: Any])?["scripts"] as? [String]
                == [ManifestConverter.shimFileName, "a.js"])
    }

    @Test func rewritesKeysGeckoSpellsDifferently() {
        let result = convert([
            "manifest_version": 3,
            "options_page": "options.html",
            "incognito": "split",
            "author": ["email": "dev@example.org"],
            "side_panel": ["default_path": "panel.html"],
        ])
        let options = result.manifest["options_ui"] as? [String: Any]
        #expect(options?["page"] as? String == "options.html")
        #expect(options?["open_in_tab"] as? Bool == true)
        #expect(result.manifest["options_page"] == nil)
        #expect(result.manifest["incognito"] as? String == "spanning")
        #expect(result.manifest["author"] as? String == "dev@example.org")
        #expect(result.manifest["side_panel"] == nil)
        #expect(result.notes.contains { $0.severity == .warning && $0.message.contains("side panel") })
    }

    @Test func chromeOnlyPermissionsAreRemovedAndReported() {
        let result = convert([
            "manifest_version": 3,
            "permissions": ["storage", "debugger", "sidePanel", "tabs", "system.cpu", "identity"],
            "optional_permissions": ["offscreen", "bookmarks"],
            "host_permissions": ["<all_urls>"],
        ])
        #expect(result.manifest["permissions"] as? [String] == ["storage", "tabs", "identity"])
        #expect(result.manifest["optional_permissions"] as? [String] == ["bookmarks"])
        #expect(result.manifest["host_permissions"] as? [String] == ["<all_urls>"])
        #expect(result.notes.contains { $0.severity == .blocker && $0.message.contains("debugger") })
        #expect(result.notes.contains { $0.severity == .blocker && $0.message.contains("offscreen") })
        #expect(result.notes.contains { $0.severity == .warning && $0.message.contains("system.cpu") })
        #expect(result.notes.contains { $0.severity == .warning && $0.message.contains("identity") })
    }

    @Test func flagsThingsThatAreNotExtensions() {
        #expect(convert(["app": ["launch": [:]]]).notes.contains { $0.severity == .blocker })
        #expect(convert(["theme": ["colors": [:]]]).notes.contains { $0.severity == .blocker })
    }

    @Test func aPlainCompatibleManifestProducesNoNotes() {
        let result = convert([
            "manifest_version": 2, "name": "Plain", "version": "1.0",
            "permissions": ["storage", "tabs", "https://example.com/*"],
            "content_scripts": [["matches": ["<all_urls>"], "js": ["cs.js"]]],
            "browser_action": ["default_popup": "popup.html"],
        ])
        #expect(result.notes.isEmpty)
        #expect(result.manifest["browser_action"] != nil)
    }

    @Test func scriptScanFindsChromeOnlyAPIs() {
        let notes = ManifestConverter.apiNotes(inScript: "chrome.sidePanel.open({}); chrome.storage.local.get()")
        #expect(notes.count == 1)
        #expect(notes.first?.message.contains("sidePanel") == true)
        #expect(ManifestConverter.apiNotes(inScript: "chrome.tabs.query({})").isEmpty)
    }

    @Test func shimIsSelfContainedScript() {
        let shim = ManifestConverter.shimSource
        #expect(shim.contains("importScripts"))
        #expect(shim.contains("sidePanel"))
        // The source is embedded with indentation stripped; a stray Swift
        // interpolation would show up as a literal backslash-paren.
        #expect(!shim.contains("\\("))
    }
}
