import Foundation
import Testing
@testable import MosaicCore

@Suite struct DownloadFileNameTests {
    @Test func keepsOrdinaryNames() {
        #expect(DownloadFileName.sanitize("report.pdf") == "report.pdf")
        #expect(DownloadFileName.sanitize("Ünïcödé résumé 2025.docx") == "Ünïcödé résumé 2025.docx")
        #expect(DownloadFileName.sanitize("👩‍💻 notes.txt") == "👩‍💻 notes.txt")
    }

    @Test func stripsPathsAndTraversal() {
        #expect(DownloadFileName.sanitize("../../etc/passwd") == "passwd")
        #expect(DownloadFileName.sanitize("C:\\Users\\x\\evil.exe") == "evil.exe")
        #expect(DownloadFileName.sanitize("/") == DownloadFileName.fallback)
        #expect(DownloadFileName.sanitize("..") == DownloadFileName.fallback)
    }

    @Test func removesControlAndBidiCharacters() {
        #expect(DownloadFileName.sanitize("in\u{0}voi\u{7}ce\n.pdf") == "invoice.pdf")
        #expect(DownloadFileName.sanitize("invoice\u{202E}fdp.app") == "invoicefdp.app")
    }

    @Test func replacesColonsAndTrimsHiddenAndPadding() {
        #expect(DownloadFileName.sanitize("12:30 meeting.txt") == "12-30 meeting.txt")
        #expect(DownloadFileName.sanitize(".bash_profile") == "bash_profile")
        #expect(DownloadFileName.sanitize("  name.txt. ") == "name.txt")
        #expect(DownloadFileName.sanitize("photo.jpg            .command") == "photo.jpg .command")
        #expect(DownloadFileName.sanitize("") == DownloadFileName.fallback)
    }

    @Test func limitsLengthKeepingExtension() {
        let long = String(repeating: "a", count: 400) + ".pdf"
        let clean = DownloadFileName.sanitize(long)
        #expect(clean.utf8.count <= 255)
        #expect(clean.hasSuffix(".pdf"))
    }

    @Test func splitsCompoundExtensions() {
        #expect(DownloadFileName.split("archive.tar.gz") == ("archive", "tar.gz"))
        #expect(DownloadFileName.split("photo.JPG") == ("photo", "JPG"))
        #expect(DownloadFileName.split("README") == ("README", ""))
        #expect(DownloadFileName.split("version 1.2 notes") == ("version 1.2 notes", ""))
        #expect(DownloadFileName.pathExtension("archive.tar.gz") == "gz")
    }

    @Test func findsAFreeName() {
        let taken: Set<String> = ["a.pdf", "a (1).pdf", "b.tar.gz"]
        #expect(DownloadFileName.unique("a.pdf") { taken.contains($0) } == "a (2).pdf")
        #expect(DownloadFileName.unique("b.tar.gz") { taken.contains($0) } == "b (1).tar.gz")
        #expect(DownloadFileName.unique("c") { taken.contains($0) } == "c")
    }

    @Test func suggestsNamesFromURLs() {
        #expect(DownloadFileName.suggested(forURL: URL(string: "https://x.org/files/My%20File.zip?x=1")!) == "My File.zip")
        #expect(DownloadFileName.suggested(forURL: URL(string: "https://x.org/")!) == "x.org")
    }
}

@Suite struct DownloadSafetyTests {
    @Test func classifiesRiskyTypes() {
        #expect(DownloadSafety.risk(forFileName: "Tool.app") == .application)
        #expect(DownloadSafety.risk(forFileName: "setup.pkg") == .installer)
        #expect(DownloadSafety.risk(forFileName: "Thing-1.0.DMG") == .diskImage)
        #expect(DownloadSafety.risk(forFileName: "run.command") == .script)
        #expect(DownloadSafety.risk(forFileName: "profile.mobileconfig") == .configuration)
        #expect(DownloadSafety.risk(forFileName: "setup.exe") == .windowsProgram)
        #expect(DownloadSafety.risk(forFileName: "photo.jpg") == nil)
        #expect(DownloadSafety.risk(forFileName: "noextension") == nil)
    }

    @Test func spotsDoubleExtensions() {
        #expect(DownloadSafety.misleadingReasons("invoice.pdf.app") == [.doubleExtension(shown: "pdf", real: "app")])
        #expect(DownloadSafety.misleadingReasons("holiday.JPG.command") == [.doubleExtension(shown: "jpg", real: "command")])
        #expect(DownloadSafety.misleadingReasons("report.pdf.exe") == [.doubleExtension(shown: "pdf", real: "exe")])
        // Ordinary multi-dot names are fine.
        #expect(DownloadSafety.misleadingReasons("archive.tar.gz").isEmpty)
        #expect(DownloadSafety.misleadingReasons("app-1.2.3.dmg").isEmpty)
        #expect(DownloadSafety.misleadingReasons("notes.v2.pdf").isEmpty)
    }

    @Test func spotsRightToLeftOverride() {
        // Displays as "invoiceppa.pdf".
        let reasons = DownloadSafety.misleadingReasons("invoice\u{202E}fdp.app")
        #expect(reasons.contains(.bidiOverride))
    }

    @Test func spotsPaddedExtensions() {
        #expect(DownloadSafety.misleadingReasons("photo.jpg            .command").contains(.paddedExtension))
        #expect(DownloadSafety.misleadingReasons("my holiday photo.jpg").isEmpty)
    }

    @Test func assessmentDecidesWarnings() {
        #expect(DownloadSafety.assess(suggestedName: "Tool.dmg", savedName: "Tool.dmg").needsWarning)
        #expect(!DownloadSafety.assess(suggestedName: "photo.png", savedName: "photo.png").needsWarning)
        // A Windows program cannot run on a Mac: noted, not warned about…
        #expect(!DownloadSafety.assess(suggestedName: "setup.exe", savedName: "setup.exe").needsWarning)
        // …unless its name is disguised.
        #expect(DownloadSafety.assess(suggestedName: "cv.pdf.exe", savedName: "cv.pdf.exe").needsWarning)
        // The raw suggested name is checked even though sanitising removed the override.
        let saved = DownloadFileName.sanitize("invoice\u{202E}fdp.zip")
        let assessment = DownloadSafety.assess(suggestedName: "invoice\u{202E}fdp.zip", savedName: saved)
        #expect(assessment.needsWarning)
        #expect(assessment.summary?.contains("reverse") == true)
    }

    @Test func autoOpensOnlySafeFiles() {
        #expect(DownloadSafety.isSafeToOpenAutomatically("paper.pdf"))
        #expect(DownloadSafety.isSafeToOpenAutomatically("song.mp3"))
        #expect(!DownloadSafety.isSafeToOpenAutomatically("archive.zip"))
        #expect(!DownloadSafety.isSafeToOpenAutomatically("Tool.dmg"))
        #expect(!DownloadSafety.isSafeToOpenAutomatically("macros.xlsm"))
        #expect(!DownloadSafety.isSafeToOpenAutomatically("invoice.pdf.app"))
    }
}

@Suite struct DownloadOrganizerTests {
    let downloads = URL(fileURLWithPath: "/tmp/dl", isDirectory: true)

    @Test func offByDefault() {
        let rules = DownloadOrganizationRules()
        #expect(!rules.isEnabled)
        #expect(rules.subfolder(forFileName: "a.jpg") == nil)
        #expect(rules.directory(forFileName: "a.jpg", in: downloads) == downloads)
    }

    @Test func sortsByTypeWhenOn() {
        let rules = DownloadOrganizationRules(isEnabled: true)
        #expect(rules.subfolder(forFileName: "a.JPG") == "Images")
        #expect(rules.subfolder(forFileName: "b.pdf") == "Documents")
        #expect(rules.subfolder(forFileName: "c.tar.gz") == "Archives")
        #expect(rules.subfolder(forFileName: "d.dmg") == "Installers")
        #expect(rules.subfolder(forFileName: "e.unknownext") == nil)
        #expect(rules.directory(forFileName: "song.flac", in: downloads).path == "/tmp/dl/Music")
    }

    @Test func fallsBackToMIMEType() {
        let rules = DownloadOrganizationRules(isEnabled: true)
        #expect(rules.subfolder(forFileName: "download", mimeType: "image/png") == "Images")
        #expect(rules.subfolder(forFileName: "download", mimeType: "video/mp4; codecs=avc1") == "Videos")
        #expect(rules.subfolder(forFileName: "download", mimeType: "application/octet-stream") == nil)
    }

    @Test func respectsCustomAndMissingFolders() {
        var rules = DownloadOrganizationRules(isEnabled: true)
        rules.folders[.images] = "../Pictures/From Web"
        rules.folders[.documents] = nil
        // Folder names are a single safe component.
        #expect(rules.subfolder(forFileName: "a.png") == "From Web")
        #expect(rules.subfolder(forFileName: "b.pdf") == nil)
        rules.folders[.audio] = "   "
        #expect(rules.subfolder(forFileName: "c.mp3") == nil)
    }

    @Test func roundTripsThroughJSON() throws {
        var rules = DownloadOrganizationRules(isEnabled: true)
        rules.folders[.video] = "Films"
        let data = try JSONEncoder().encode(rules)
        #expect(try JSONDecoder().decode(DownloadOrganizationRules.self, from: data) == rules)
    }
}

@Suite struct DownloadHistoryStoreTests {
    func temporaryFile() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("mosaic-dl-tests-\(UUID().uuidString)", isDirectory: true)
            .appendingPathComponent("downloads.json")
    }

    func record(_ name: String, url: String, state: DownloadState = .completed, at date: Date = Date(),
                path: String? = nil) -> DownloadRecord {
        DownloadRecord(url: url, fileName: name, destinationPath: path ?? "/tmp/\(name)", engine: .chromium,
                       totalBytes: 10, receivedBytes: state == .completed ? 10 : 3, state: state, startedAt: date)
    }

    @Test func persistsAndReloads() throws {
        let file = temporaryFile()
        defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
        let store = DownloadHistoryStore(fileURL: file)
        var first = record("a.pdf", url: "https://example.com/a.pdf", at: Date(timeIntervalSince1970: 100))
        first.speed = 999 // live value: not persisted
        store.upsert(first)
        store.upsert(record("b.zip", url: "https://example.org/b.zip", at: Date(timeIntervalSince1970: 200)))
        try store.save()

        let reloaded = DownloadHistoryStore(fileURL: file)
        reloaded.load()
        #expect(reloaded.records.map(\.fileName) == ["b.zip", "a.pdf"])
        #expect(reloaded.records[1].speed == 0)
        #expect(reloaded.records[1].url == "https://example.com/a.pdf")
    }

    @Test func marksRunningDownloadsInterruptedOnLoad() throws {
        let file = temporaryFile()
        defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
        let store = DownloadHistoryStore(fileURL: file)
        store.upsert(record("big.iso", url: "https://x/big.iso", state: .inProgress))
        store.upsert(record("p.bin", url: "https://x/p.bin", state: .paused))
        try store.save()
        let reloaded = DownloadHistoryStore(fileURL: file)
        reloaded.load()
        #expect(reloaded.records.allSatisfy { $0.state == .failed })
        #expect(reloaded.records.allSatisfy { $0.errorMessage == DownloadHistoryStore.interruptedMessage })
    }

    @Test func survivesACorruptFile() throws {
        let file = temporaryFile()
        defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("not json".utf8).write(to: file)
        let store = DownloadHistoryStore(fileURL: file)
        store.load()
        #expect(store.records.isEmpty)
        #expect(FileManager.default.fileExists(atPath: file.appendingPathExtension("corrupt").path))
    }

    @Test func upsertReplacesInPlace() {
        let store = DownloadHistoryStore(fileURL: temporaryFile())
        var a = record("a", url: "https://x/a", state: .inProgress)
        store.upsert(a)
        store.upsert(record("b", url: "https://x/b"))
        a.state = .completed
        store.upsert(a)
        #expect(store.records.count == 2)
        #expect(store.record(a.id)?.state == .completed)
        #expect(store.records.last?.id == a.id)
    }

    @Test func searchesNameURLAndHost() {
        let store = DownloadHistoryStore(fileURL: temporaryFile())
        store.upsert(record("Quarterly Report.pdf", url: "https://files.example.com/q3.pdf"))
        store.upsert(record("café.zip", url: "https://cdn.other.org/cafe.zip"))
        #expect(store.search("report").map(\.fileName) == ["Quarterly Report.pdf"])
        #expect(store.search("EXAMPLE.com").count == 1)
        #expect(store.search("cafe").map(\.fileName) == ["café.zip"])
        #expect(store.search("quarterly pdf").count == 1)
        #expect(store.search("quarterly zip").isEmpty)
        #expect(store.search("  ").count == 2)
    }

    @Test func clearKeepsActiveDownloads() {
        let store = DownloadHistoryStore(fileURL: temporaryFile())
        store.upsert(record("done", url: "https://x/1"))
        store.upsert(record("failed", url: "https://x/2", state: .failed))
        store.upsert(record("running", url: "https://x/3", state: .inProgress))
        store.clearFinished()
        #expect(store.records.map(\.fileName) == ["running"])
    }

    @Test func limitDropsOldestFinished() {
        let store = DownloadHistoryStore(fileURL: temporaryFile(), limit: 2)
        store.upsert(record("old-running", url: "https://x/0", state: .inProgress, at: Date(timeIntervalSince1970: 1)))
        store.upsert(record("mid", url: "https://x/1", at: Date(timeIntervalSince1970: 2)))
        store.upsert(record("new", url: "https://x/2", at: Date(timeIntervalSince1970: 3)))
        #expect(store.records.map(\.fileName) == ["new", "old-running"])
    }

    @Test func findsDuplicatesOnlyWhenTheFileStillExists() {
        let store = DownloadHistoryStore(fileURL: temporaryFile())
        let existing = record("a.zip", url: "https://x.org/a.zip#top", path: "/files/a.zip")
        store.upsert(existing)
        store.upsert(record("b.zip", url: "https://x.org/b.zip", state: .failed))
        #expect(store.completedDownload(of: "https://x.org/a.zip", fileExists: { $0 == "/files/a.zip" })?.id == existing.id)
        #expect(store.completedDownload(of: "https://x.org/a.zip", fileExists: { _ in false }) == nil)
        #expect(store.completedDownload(of: "https://x.org/b.zip", fileExists: { _ in true }) == nil)
        #expect(store.completedDownload(of: "https://x.org/a.zip", excluding: existing.id, fileExists: { _ in true }) == nil)
    }
}

@Suite struct FreeDownloadManagerProtocolTests {
    @Test func policyHonoursModeSchemeAndSize() {
        let url = "https://example.com/big.iso"
        #expect(FDMPolicy(mode: .off).decide(url: url, totalBytes: 1, fdmInstalled: true) == .useEngine)
        #expect(FDMPolicy(mode: .always).decide(url: url, totalBytes: 1, fdmInstalled: false) == .useEngine)
        #expect(FDMPolicy(mode: .always).decide(url: url, totalBytes: 1, fdmInstalled: true) == .sendToFDM)
        #expect(FDMPolicy(mode: .ask).decide(url: url, totalBytes: nil, fdmInstalled: true) == .askUser)
        #expect(FDMPolicy(mode: .always).decide(url: "blob:https://x/123", totalBytes: 1, fdmInstalled: true) == .useEngine)
        #expect(FDMPolicy(mode: .always).decide(url: "data:text/plain,hi", totalBytes: 1, fdmInstalled: true) == .useEngine)
        let threshold = FDMPolicy(mode: .always, minimumBytes: 10_000_000)
        #expect(threshold.decide(url: url, totalBytes: 5_000, fdmInstalled: true) == .useEngine)
        #expect(threshold.decide(url: url, totalBytes: 50_000_000, fdmInstalled: true) == .sendToFDM)
        // Unknown size counts as large.
        #expect(threshold.decide(url: url, totalBytes: -1, fdmInstalled: true) == .sendToFDM)
        #expect(threshold.decide(url: url, totalBytes: nil, fdmInstalled: true) == .sendToFDM)
    }

    @Test func framesAndUnframesMessages() throws {
        let data = try FDMNativeMessage.frame(FDMNativeMessage.handshake(id: "1"))
        let length = data.prefix(4).withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }
        #expect(Int(UInt32(littleEndian: length)) == data.count - 4)
        var buffer = data + (try FDMNativeMessage.frame(["id": "2", "type": "x"]))
        buffer.append(contentsOf: [9, 0]) // start of an incomplete frame
        let messages = FDMNativeMessage.takeMessages(from: &buffer)
        #expect(messages.count == 2)
        #expect(messages[0]["type"] as? String == "handshake")
        #expect(messages[1]["id"] as? String == "2")
        #expect(buffer.count == 2)
    }

    @Test func createDownloadsCarriesOnlyWhatWasAllowed() {
        let message = FDMNativeMessage.createDownloads(id: "7", [
            FDMDownloadRequest(url: "https://x/a.zip", referrer: "https://x/page", userAgent: "UA"),
        ])
        let body = message["create_downloads"] as? [String: Any]
        let entry = (body?["downloads"] as? [[String: String]])?.first
        #expect(message["type"] as? String == "create_downloads")
        #expect(entry?["url"] == "https://x/a.zip")
        #expect(entry?["httpReferer"] == "https://x/page")
        #expect(entry?["userAgent"] == "UA")
        #expect(entry?["httpCookies"] == nil)
    }

    @Test func interpretsResponses() {
        #expect(FDMNativeMessage.outcome(of: ["id": "2", "error": "", "result": ""]) == .accepted)
        #expect(FDMNativeMessage.outcome(of: ["id": "2", "error": "", "result": "0"]) == .cancelledByUser)
        #expect(FDMNativeMessage.outcome(of: ["id": "2", "error": "HTTP Error 404"]) == .failed("HTTP Error 404"))
    }
}
