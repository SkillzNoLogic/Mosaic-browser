import Foundation
import Testing
@testable import MosaicCore

@Suite struct SoftwareVersionTests {
    private func v(_ text: String) -> SoftwareVersion { SoftwareVersion(text)! }

    @Test func parsesMozillaAndGitHubStyles() {
        #expect(v("153.8.0esr").components == [153, 8, 0])
        #expect(v("153.8.0esr").preRelease == nil)
        #expect(v("v0.2.0").components == [0, 2, 0])
        #expect(v("140.0b3").preRelease == "b3")
        #expect(v("1.2.3-beta.1").preRelease == "beta.1")
        #expect(v("153.8.0esr").numberString == "153.8.0")
    }

    @Test func rejectsNonVersions() {
        #expect(SoftwareVersion("") == nil)
        #expect(SoftwareVersion("esr") == nil)
        #expect(SoftwareVersion("1..2") == nil)
        #expect(SoftwareVersion("latest") == nil)
    }

    @Test func comparesNumerically() {
        #expect(v("153.10.0") > v("153.9.0"))
        #expect(v("154.0") > v("153.99.99"))
        #expect(v("153.8") == v("153.8.0"))
        #expect(v("153.8.0esr") == v("153.8.0"))
        #expect(v("153.8.1") > v("153.8.0esr"))
        #expect(v("v0.10.0") > v("0.9.9"))
    }

    @Test func preReleasesSortBeforeTheRelease() {
        #expect(v("140.0b3") < v("140.0"))
        #expect(v("140.0b3") < v("140.0b10"))
        #expect(v("140.0b3") > v("139.0"))
    }

    @Test func readsESRFromProductDetails() {
        let json = #"{"FIREFOX_ESR": "153.8.0esr", "FIREFOX_ESR_NEXT": "", "LATEST_FIREFOX_VERSION": "161.0"}"#
        #expect(FirefoxProductDetails.esrVersion(from: Data(json.utf8)) == v("153.8.0"))
        #expect(FirefoxProductDetails.esrVersion(from: Data(#"{"FIREFOX_ESR": ""}"#.utf8)) == nil)
        #expect(FirefoxProductDetails.esrVersion(from: Data("not json".utf8)) == nil)
    }

    @Test func onlyTrustsMozillaOverHTTPS() {
        #expect(FirefoxProductDetails.isTrustedDownloadURL(FirefoxProductDetails.esrDownloadURL))
        #expect(FirefoxProductDetails.isTrustedDownloadURL(URL(string:
            "https://download-installer.cdn.mozilla.net/pub/firefox/releases/153.8.0esr/mac/en-US/Firefox%20153.8.0esr.dmg")!))
        #expect(!FirefoxProductDetails.isTrustedDownloadURL(URL(string: "http://download.mozilla.org/x.dmg")!))
        #expect(!FirefoxProductDetails.isTrustedDownloadURL(URL(string: "https://mozilla.org.evil.com/x.dmg")!))
        #expect(!FirefoxProductDetails.isTrustedDownloadURL(URL(string: "https://evilmozilla.org/x.dmg")!))
    }
}

@Suite struct ReleaseCheckTests {
    let release = #"""
    {"tag_name": "v0.3.0", "name": "Mosaic 0.3", "html_url": "https://github.com/SkillzNoLogic/Mosaic-browser/releases/tag/v0.3.0",
     "published_at": "2026-11-20T10:00:00Z", "body": "Notes"}
    """#

    @Test func newerReleaseIsAvailable() throws {
        let outcome = ReleaseCheck.outcome(status: 200, body: Data(release.utf8), current: SoftwareVersion("0.1.0"))
        guard case .available(let found) = outcome else { Issue.record("expected available, got \(outcome)"); return }
        #expect(found.version == SoftwareVersion("0.3.0"))
        #expect(found.name == "Mosaic 0.3")
        #expect(found.pageURL?.absoluteString.hasSuffix("v0.3.0") == true)
        #expect(found.publishedAt != nil)
    }

    @Test func sameOrOlderReleaseIsUpToDate() {
        #expect(ReleaseCheck.outcome(status: 200, body: Data(release.utf8), current: SoftwareVersion("0.3.0"))
            == .upToDate(latest: SoftwareVersion("0.3.0")!))
        #expect(ReleaseCheck.outcome(status: 200, body: Data(release.utf8), current: SoftwareVersion("1.0"))
            == .upToDate(latest: SoftwareVersion("0.3.0")!))
    }

    @Test func privateRepositoryNeedsAccess() {
        let body = Data(#"{"message": "Not Found", "status": "404"}"#.utf8)
        let outcome = ReleaseCheck.outcome(status: 404, body: body, current: SoftwareVersion("0.1.0"))
        #expect(outcome == .needsAccess)
        #expect(outcome.message.contains("GitHub token"))
    }

    @Test func otherAnswers() {
        #expect(ReleaseCheck.outcome(status: 403, body: Data(), current: nil) == .rateLimited)
        #expect(ReleaseCheck.outcome(status: 500, body: Data(), current: nil) == .failed("GitHub returned HTTP 500."))
        if case .failed = ReleaseCheck.outcome(status: 200, body: Data("{}".utf8), current: nil) {} else {
            Issue.record("an unreadable reply must fail")
        }
    }

    @Test func dailyScheduleOnlyWhenChosen() {
        let now = Date()
        #expect(!UpdateCheckFrequency.manual.isDue(lastCheck: nil, now: now))
        #expect(UpdateCheckFrequency.daily.isDue(lastCheck: nil, now: now))
        #expect(!UpdateCheckFrequency.daily.isDue(lastCheck: now.addingTimeInterval(-3600), now: now))
        #expect(UpdateCheckFrequency.daily.isDue(lastCheck: now.addingTimeInterval(-25 * 3600), now: now))
        // A clock that went backwards must not suppress checks forever.
        #expect(UpdateCheckFrequency.daily.isDue(lastCheck: now.addingTimeInterval(3600), now: now))
    }
}
