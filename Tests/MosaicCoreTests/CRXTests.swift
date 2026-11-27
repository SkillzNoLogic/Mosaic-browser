import Foundation
import Testing
@testable import MosaicCore

/// Builders for synthetic packages, shared by the CRX and store tests.
enum Fixtures {
    static let zipMagic: [UInt8] = [0x50, 0x4B, 0x03, 0x04]

    static func le32(_ value: Int) -> [UInt8] {
        [UInt8(value & 0xFF), UInt8((value >> 8) & 0xFF), UInt8((value >> 16) & 0xFF), UInt8((value >> 24) & 0xFF)]
    }

    static func varint(_ value: Int) -> [UInt8] {
        var value = value
        var out: [UInt8] = []
        repeat {
            var byte = UInt8(value & 0x7F)
            value >>= 7
            if value != 0 { byte |= 0x80 }
            out.append(byte)
        } while value != 0
        return out
    }

    static func field(_ number: Int, bytes: [UInt8]) -> [UInt8] {
        varint(number << 3 | 2) + varint(bytes.count) + bytes
    }

    /// A CRX3 file: a header whose signed data names `id`, followed by `zip`.
    static func crx3(id: [UInt8], zip: [UInt8]) -> Data {
        let signedData = field(1, bytes: id)
        // A proof entry first, to make sure unrelated fields are skipped.
        let header = field(2, bytes: field(1, bytes: [1, 2, 3]) + field(2, bytes: [4, 5]))
            + field(10000, bytes: signedData)
        return Data(Array("Cr24".utf8) + le32(3) + le32(header.count) + header + zip)
    }

    static func crx2(key: [UInt8], signature: [UInt8], zip: [UInt8]) -> Data {
        Data(Array("Cr24".utf8) + le32(2) + le32(key.count) + le32(signature.count) + key + signature + zip)
    }

    /// Zips `files` (path → contents) with the system zip tool.
    static func zip(_ files: [String: String]) throws -> Data {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("mosaic-fixture-\(UUID().uuidString)", isDirectory: true)
        let source = root.appendingPathComponent("src", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        for (path, contents) in files {
            let url = source.appendingPathComponent(path)
            try FileManager.default.createDirectory(
                at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(contents.utf8).write(to: url)
        }
        let archive = root.appendingPathComponent("out.zip")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        process.currentDirectoryURL = source
        process.arguments = ["-q", "-r", archive.path, "."]
        try process.run()
        process.waitUntilExit()
        return try Data(contentsOf: archive)
    }

    static func temporaryDirectory() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("mosaic-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}

@Suite struct CRXArchiveTests {
    let payload = Fixtures.zipMagic + [9, 9, 9]

    @Test func parsesCRX3AndReadsIDFromSignedHeader() throws {
        let id: [UInt8] = Array(0..<16)
        let archive = try CRXArchive.parse(Fixtures.crx3(id: id, zip: payload))
        #expect(archive.format == .crx3)
        #expect([UInt8](archive.zipData) == payload)
        // Each byte becomes two letters a–p: 0x00 → "aa", 0x01 → "ab", … 0x0f → "ap".
        #expect(archive.extensionID == "aaabacadaeafagahaiajakalamanaoap")
    }

    @Test func parsesCRX2AndDerivesIDFromPublicKey() throws {
        let key: [UInt8] = Array("not really a key".utf8)
        let archive = try CRXArchive.parse(Fixtures.crx2(key: key, signature: [1, 2, 3, 4], zip: payload))
        #expect(archive.format == .crx2)
        #expect([UInt8](archive.zipData) == payload)
        #expect(archive.extensionID == CRXArchive.extensionID(fromPublicKey: Data(key)))
        #expect(CRXArchive.isValidID(archive.extensionID ?? ""))
    }

    @Test func acceptsBareZip() throws {
        let archive = try CRXArchive.parse(Data(payload))
        #expect(archive.format == .zip)
        #expect(archive.extensionID == nil)
        #expect([UInt8](archive.zipData) == payload)
    }

    @Test func rejectsGarbageAndTruncatedFiles() {
        #expect(throws: CRXArchive.ParseError.notAnExtensionPackage) {
            try CRXArchive.parse(Data("<html>not found</html>".utf8))
        }
        #expect(throws: CRXArchive.ParseError.notAnExtensionPackage) { try CRXArchive.parse(Data()) }
        #expect(throws: CRXArchive.ParseError.truncated) {
            try CRXArchive.parse(Data(Array("Cr24".utf8) + Fixtures.le32(3) + Fixtures.le32(5000) + [1, 2]))
        }
        #expect(throws: CRXArchive.ParseError.unsupportedVersion(7)) {
            try CRXArchive.parse(Data(Array("Cr24".utf8) + Fixtures.le32(7) + Fixtures.le32(0)))
        }
    }

    @Test func crx3WithoutSignedHeaderHasNoID() throws {
        let header = Fixtures.field(2, bytes: [1, 2, 3])
        let data = Data(Array("Cr24".utf8) + Fixtures.le32(3) + Fixtures.le32(header.count) + header + payload)
        #expect(try CRXArchive.parse(data).extensionID == nil)
    }

    @Test func manifestKeyGivesSameIDAsRawKey() {
        let key = Data((0..<64).map { UInt8($0) })
        #expect(CRXArchive.extensionID(fromManifestKey: key.base64EncodedString())
                == CRXArchive.extensionID(fromPublicKey: key))
        #expect(CRXArchive.extensionID(fromManifestKey: "***") == nil)
    }

    @Test func validatesIDs() {
        #expect(CRXArchive.isValidID("eimadpbcbfnmbkopoojfekhnkhdbieeh"))
        #expect(!CRXArchive.isValidID("eimadpbcbfnmbkopoojfekhnkhdbiee"))      // 31 chars
        #expect(!CRXArchive.isValidID("eimadpbcbfnmbkopoojfekhnkhdbieez"))     // z is outside a–p
        #expect(!CRXArchive.isValidID("../../../../../../../../etc/passwd"))
    }
}

@Suite struct ChromeWebStoreTests {
    let id = "eimadpbcbfnmbkopoojfekhnkhdbieeh"

    @Test(arguments: [
        "eimadpbcbfnmbkopoojfekhnkhdbieeh",
        "  eimadpbcbfnmbkopoojfekhnkhdbieeh\n",
        "https://chromewebstore.google.com/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh",
        "https://chromewebstore.google.com/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh?hl=en&pli=1",
        "https://chromewebstore.google.com/detail/eimadpbcbfnmbkopoojfekhnkhdbieeh",
        "https://chromewebstore.google.com/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh/reviews",
        "https://chrome.google.com/webstore/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh",
    ])
    func findsID(input: String) {
        #expect(ChromeWebStore.extensionID(from: input) == id)
    }

    @Test(arguments: [
        "", "dark reader", "https://chromewebstore.google.com/category/extensions",
        "https://evil.example/detail/x/eimadpbcbfnmbkopoojfekhnkhdbieeh",
        "https://chromewebstore.google.com/detail/dark-reader/short",
    ])
    func rejectsNonListings(input: String) {
        #expect(ChromeWebStore.extensionID(from: input) == nil)
    }

    @Test func downloadURLCarriesTheIDAndAsksForCRX() throws {
        let url = ChromeWebStore.downloadURL(forID: id)
        let items = try #require(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems)
        let query = Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value ?? "") })
        #expect(url.host == "clients2.google.com")
        #expect(query["x"] == "id=\(id)&installsource=ondemand&uc")
        #expect(query["acceptformat"] == "crx2,crx3")
        #expect(query["response"] == "redirect")
        // The nested "&" must be escaped or the server would see separate parameters.
        #expect(url.absoluteString.contains("%26installsource"))
    }
}
