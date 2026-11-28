import Foundation
import Testing
@testable import MosaicCore

@Suite struct CodeSignatureTests {
    /// Real output of `codesign --display --verbose=2` for Firefox ESR 153.8.0.
    static let firefoxDisplay = """
    Executable=/Volumes/Firefox/Firefox.app/Contents/MacOS/firefox
    Identifier=org.mozilla.firefox
    Format=app bundle with Mach-O universal (x86_64 arm64)
    CodeDirectory v=20500 size=927 flags=0x10000(runtime) hashes=18+7 location=embedded
    Signature size=8988
    Authority=Developer ID Application: Mozilla Corporation (43AQ936H96)
    Authority=Developer ID Certification Authority
    Authority=Apple Root CA
    Timestamp=18 Nov 2026 at 8:21:42 am
    Notarization Ticket=stapled
    Info.plist entries=36
    TeamIdentifier=43AQ936H96
    Runtime Version=26.5.0
    Sealed Resources version=2 rules=13 files=71
    Internal requirements count=1 size=180
    """

    static let appleDisplay = """
    Executable=/System/Applications/Calculator.app/Contents/MacOS/Calculator
    Identifier=com.apple.calculator
    Format=app bundle with Mach-O universal (x86_64 arm64e)
    Authority=Software Signing
    Authority=Apple Code Signing Certification Authority
    Authority=Apple Root CA
    TeamIdentifier=not set
    """

    let mozilla = SignerExpectation.mozillaFirefox

    @Test func parsesDisplayOutput() {
        let info = CodeSignatureInfo.parse(codesignDisplay: Self.firefoxDisplay)
        #expect(info.identifier == "org.mozilla.firefox")
        #expect(info.teamIdentifier == "43AQ936H96")
        #expect(info.authorities.first == "Developer ID Application: Mozilla Corporation (43AQ936H96)")
        #expect(info.authorities.count == 3)
        #expect(info.isNotarized)
        #expect(info.includesARM64)
        #expect(CodeSignatureInfo.parse(codesignDisplay: Self.appleDisplay).teamIdentifier == nil)
    }

    @Test func requirementPinsTheTeamAndDeveloperID() {
        let requirement = mozilla.requirement
        #expect(requirement.contains("anchor apple generic"))
        #expect(requirement.contains("certificate leaf[subject.OU] = \"43AQ936H96\""))
        #expect(requirement.contains("identifier \"org.mozilla.firefox\""))
        // Developer ID intermediate and leaf marker OIDs.
        #expect(requirement.contains("1.2.840.113635.100.6.2.6"))
        #expect(requirement.contains("1.2.840.113635.100.6.1.13"))
        let runs = mozilla.codesignArguments(appPath: "/tmp/Firefox.app")
        #expect(runs[0] == ["--verify", "--deep", "--strict", "/tmp/Firefox.app"])
        #expect(runs[1].contains("-R=\(requirement)"))
    }

    @Test func trustsMozillaSignedFirefox() {
        let info = CodeSignatureInfo.parse(codesignDisplay: Self.firefoxDisplay)
        #expect(mozilla.evaluate(integrityStatus: 0, integrityOutput: "", requirementStatus: 0, display: info)
            == .trusted(info))
    }

    @Test func rejectsTamperedApp() {
        let info = CodeSignatureInfo.parse(codesignDisplay: Self.firefoxDisplay)
        let verdict = mozilla.evaluate(
            integrityStatus: 1,
            integrityOutput: "/tmp/Firefox.app: a sealed resource is missing or invalid\nfile modified: XUL",
            requirementStatus: 0, display: info)
        #expect(verdict == .invalid("file modified: XUL"))
        let pathFirst = mozilla.evaluate(
            integrityStatus: 1, integrityOutput: "/tmp/x/Firefox.app: a sealed resource is missing or invalid",
            requirementStatus: 0, display: info)
        #expect(pathFirst == .invalid("a sealed resource is missing or invalid"))
    }

    @Test func rejectsOtherSigners() {
        let apple = CodeSignatureInfo.parse(codesignDisplay: Self.appleDisplay)
        #expect(mozilla.evaluate(integrityStatus: 0, integrityOutput: "", requirementStatus: 3, display: apple)
            == .wrongSigner(found: "Software Signing"))
        // The requirement passing is not enough if the details disagree (and vice versa).
        var spoofed = CodeSignatureInfo.parse(codesignDisplay: Self.firefoxDisplay)
        spoofed.teamIdentifier = "ABCDE12345"
        if case .wrongSigner = mozilla.evaluate(integrityStatus: 0, integrityOutput: "", requirementStatus: 0, display: spoofed) {
        } else { Issue.record("a different team must be rejected") }
        let firefox = CodeSignatureInfo.parse(codesignDisplay: Self.firefoxDisplay)
        if case .wrongSigner = mozilla.evaluate(integrityStatus: 0, integrityOutput: "", requirementStatus: 3, display: firefox) {
        } else { Issue.record("a failed requirement must be rejected") }
    }
}

@Suite struct RuntimeInstallMachineTests {
    typealias M = RuntimeInstallMachine

    @Test func freshInstallRunsThrough() {
        var phase = RuntimeInstallPhase.idle
        phase = M.next(phase, on: .start)
        #expect(phase == .downloading(received: 0, expected: nil))
        phase = M.next(phase, on: .progress(received: 50, expected: 100))
        #expect(phase.fraction == 0.5)
        #expect(phase.isCancellable)
        phase = M.next(phase, on: .downloaded)
        #expect(phase == .verifying)
        phase = M.next(phase, on: .verified(engineRunning: false))
        #expect(phase == .installing)
        #expect(!phase.isCancellable)
        phase = M.next(phase, on: .installed(version: "153.8.0"))
        #expect(phase == .finished(version: "153.8.0"))
        #expect(!phase.isActive)
    }

    @Test func updateStopsTheEngineBeforeInstalling() {
        var phase = RuntimeInstallPhase.verifying
        phase = M.next(phase, on: .verified(engineRunning: true))
        #expect(phase == .stoppingEngine)
        #expect(!phase.isCancellable)
        phase = M.next(phase, on: .engineStopped)
        #expect(phase == .installing)
    }

    @Test func cancelOnlyBeforeCopying() {
        #expect(M.next(.downloading(received: 1, expected: 2), on: .cancel) == .cancelled)
        #expect(M.next(.verifying, on: .cancel) == .cancelled)
        #expect(M.next(.installing, on: .cancel) == .installing)
        #expect(M.next(.stoppingEngine, on: .cancel) == .stoppingEngine)
        #expect(M.next(.idle, on: .cancel) == .idle)
    }

    @Test func lateEventsAfterCancelAreIgnored() {
        let cancelled = RuntimeInstallPhase.cancelled
        #expect(M.next(cancelled, on: .progress(received: 9, expected: 10)) == .cancelled)
        #expect(M.next(cancelled, on: .downloaded) == .cancelled)
        #expect(M.next(cancelled, on: .fail(.offline)) == .cancelled)
        #expect(M.next(cancelled, on: .installed(version: "1")) == .cancelled)
    }

    @Test func failuresAndRestart() {
        let failed = M.next(.downloading(received: 0, expected: nil), on: .fail(.offline))
        #expect(failed == .failed(.offline))
        #expect(M.next(failed, on: .start) == .downloading(received: 0, expected: nil))
        #expect(M.next(failed, on: .reset) == .idle)
        // A second start while running does not restart the download.
        #expect(M.next(.verifying, on: .start) == .verifying)
        #expect(M.next(.installing, on: .reset) == .installing)
    }

    @Test func messagesArePlainLanguage() {
        let failures: [RuntimeInstallFailure] = [
            .offline, .network("timed out"), .server(status: 503), .untrustedSource("evil.example"),
            .notEnoughSpace(neededMB: 1000), .damagedDownload, .noAppInDiskImage, .signatureInvalid("x"),
            .wrongSigner(found: nil), .wrongSigner(found: "Someone"), .notAppleSilicon, .notGeckoBrowser,
            .engineBusy, .copyFailed("disk full"), .removeFailed("busy"),
        ]
        for failure in failures {
            #expect(failure.message.count > 30)
            #expect(failure.message.hasSuffix(".") || failure.message.hasSuffix(")"))
        }
        #expect(RuntimeInstallFailure.from(urlErrorCode: -1009, description: "") == .offline)
        #expect(RuntimeInstallFailure.from(urlErrorCode: -1001, description: "timed out") == .network("timed out"))
        #expect(RuntimeInstallPhase.downloading(received: 12_400_000, expected: 140_000_000).summary
            == "Downloading Firefox ESR… 12 MB of 140 MB")
    }

    @Test func spaceCheck() {
        #expect(RuntimeInstallRequirements.hasEnoughSpace(available: nil))
        #expect(!RuntimeInstallRequirements.hasEnoughSpace(available: 200_000_000))
        #expect(RuntimeInstallRequirements.hasEnoughSpace(available: 5_000_000_000))
    }

    @Test func privateOnlySwitchIgnoresBorrowedRuntimes() throws {
        let support = try Fixtures.temporaryDirectory()
        let env = ["MOSAIC_GECKO_PRIVATE_ONLY": "1", "MOSAIC_DATA_DIR": support.path]
        // Nothing installed in the isolated folder: nothing found, even if /Applications has Firefox.
        #expect(GeckoRuntime.locate(applicationSupport: support, environment: env) == nil)
        // The user's main folder is never consulted under the switch.
        let other = try Fixtures.temporaryDirectory()
        #expect(GeckoRuntime.locate(applicationSupport: other, environment: env) == nil)
    }
}
