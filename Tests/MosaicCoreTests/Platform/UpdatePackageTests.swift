import CryptoKit
import Foundation
import Testing
@testable import MosaicCore

@Suite struct UpdatePackageTests {
    let key = Curve25519.Signing.PrivateKey()
    var publicKey: String { key.publicKey.rawRepresentation.base64EncodedString() }

    @Test func acceptsOnlyTheRightSignature() throws {
        let data = Data("Mosaic update".utf8)
        let signature = try key.signature(for: data).base64EncodedString()
        #expect(UpdatePackage.verify(data, signature: signature + "\n", publicKey: publicKey))
        #expect(!UpdatePackage.verify(Data("Mosaic updatf".utf8), signature: signature, publicKey: publicKey))
        let other = Curve25519.Signing.PrivateKey().publicKey.rawRepresentation.base64EncodedString()
        #expect(!UpdatePackage.verify(data, signature: signature, publicKey: other))
        #expect(!UpdatePackage.verify(data, signature: "not base64!", publicKey: publicKey))
        #expect(!UpdatePackage.verify(data, signature: "", publicKey: publicKey))
    }

    @Test func compiledInKeyIsValid() throws {
        let raw = try #require(Data(base64Encoded: UpdatePackage.signingPublicKey))
        #expect((try? Curve25519.Signing.PublicKey(rawRepresentation: raw)) != nil)
    }

    @Test func findsTheZipAndSignature() throws {
        let json = """
            {"tag_name":"v0.3.0","name":"Mosaic 0.3.0","assets":[
              {"name":"Mosaic-0.3.0-macos-arm64.dmg","size":1,"browser_download_url":"https://github.com/a/b/x.dmg","url":"https://api.github.com/repos/a/b/releases/assets/1"},
              {"name":"Mosaic-0.3.0-macos-arm64.zip","size":2,"browser_download_url":"https://github.com/a/b/x.zip","url":"https://api.github.com/repos/a/b/releases/assets/2"},
              {"name":"Mosaic-0.3.0-macos-arm64.zip.sig","size":3,"browser_download_url":"https://github.com/a/b/x.zip.sig","url":"https://api.github.com/repos/a/b/releases/assets/3"}]}
            """
        let release = try #require(ReleaseCheck.parseRelease(Data(json.utf8)))
        #expect(release.assets.count == 3)
        let assets = try #require(ReleaseCheck.updateAssets(in: release.assets, version: release.version))
        #expect(assets.zip.size == 2)
        #expect(assets.signature.apiURL?.absoluteString == "https://api.github.com/repos/a/b/releases/assets/3")
        // A release without a signature (made before updates were signed) is not installable.
        #expect(ReleaseCheck.updateAssets(in: Array(release.assets.prefix(2)), version: release.version) == nil)
    }

    @Test func locationProblems() {
        #expect(UpdatePackage.locationProblem(appPath: "/Applications/Mosaic.app", volumeIsReadOnly: false, parentIsWritable: true) == nil)
        #expect(UpdatePackage.locationProblem(
            appPath: "/private/var/folders/x/AppTranslocation/ABC/d/Mosaic.app", volumeIsReadOnly: true, parentIsWritable: false) == .translocated)
        #expect(UpdatePackage.locationProblem(appPath: "/Volumes/Mosaic 0.3.0/Mosaic.app", volumeIsReadOnly: true, parentIsWritable: false) == .readOnlyVolume)
        #expect(UpdatePackage.locationProblem(appPath: "/Applications/Mosaic.app", volumeIsReadOnly: false, parentIsWritable: false)
            == .folderNotWritable("/Applications"))
    }

    @Test func feedOverrideIsLimited() {
        #expect(ReleaseCheck.feedURL(environment: [:]) == ReleaseCheck.latestReleaseURL)
        #expect(ReleaseCheck.feedURL(environment: ["MOSAIC_UPDATE_API": "http://127.0.0.1:8765/latest"]).absoluteString == "http://127.0.0.1:8765/latest")
        #expect(ReleaseCheck.feedURL(environment: ["MOSAIC_UPDATE_API": "http://example.com/latest"]) == ReleaseCheck.latestReleaseURL)
        #expect(ReleaseCheck.feedURL(environment: ["MOSAIC_UPDATE_API": "file:///tmp/latest"]) == ReleaseCheck.latestReleaseURL)
    }

    @Test func installerScriptParses() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("mosaic-install-\(UUID()).sh")
        try UpdatePackage.installerScript.write(to: url, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: url) }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        process.arguments = ["-n", url.path]
        try process.run()
        process.waitUntilExit()
        #expect(process.terminationStatus == 0)
    }

    @Test func installerSwapsTheBundle() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mosaic-swap-\(UUID())")
        let target = root.appendingPathComponent("Apps/Mosaic.app"), staged = root.appendingPathComponent("Updates/Mosaic.app")
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: staged, withIntermediateDirectories: true)
        try Data("old".utf8).write(to: target.appendingPathComponent("v"))
        try Data("new".utf8).write(to: staged.appendingPathComponent("v"))
        let script = root.appendingPathComponent("install.sh")
        try UpdatePackage.installerScript.write(to: script, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: root) }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        // PID 999999 does not exist, so the script does not wait; "stay" means no relaunch.
        process.arguments = [script.path, "999999", target.path, staged.path, "stay", "open"]
        process.standardOutput = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        #expect(process.terminationStatus == 0)
        #expect(try String(contentsOf: target.appendingPathComponent("v"), encoding: .utf8) == "new")
        #expect(!FileManager.default.fileExists(atPath: staged.path))
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: root.appendingPathComponent("Apps").path)
        #expect(leftovers == ["Mosaic.app"])
    }
}
