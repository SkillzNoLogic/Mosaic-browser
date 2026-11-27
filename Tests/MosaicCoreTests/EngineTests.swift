import Foundation
import Testing
@testable import MosaicCore

private func temporaryFile(_ name: String) -> URL {
    FileManager.default.temporaryDirectory
        .appendingPathComponent("mosaic-engine-tests-\(UUID().uuidString)", isDirectory: true)
        .appendingPathComponent(name)
}

@Suite struct SitePermissionStoreTests {
    @Test func normalisesSitesToOrigins() {
        #expect(SitePermissionStore.site(for: "https://Example.com/path?q=1") == "https://example.com")
        #expect(SitePermissionStore.site(for: "https://example.com:443/") == "https://example.com")
        #expect(SitePermissionStore.site(for: "http://localhost:8000/a.html") == "http://localhost:8000")
        #expect(SitePermissionStore.site(for: "http://example.com:80") == "http://example.com")
        #expect(SitePermissionStore.site(for: "example.org") == "https://example.org")
        #expect(SitePermissionStore.site(for: "file:///Users/me/page.html") == "file://")
        #expect(SitePermissionStore.site(for: "about:blank") == nil)
        #expect(SitePermissionStore.site(for: "data:text/html,hi") == nil)
        #expect(SitePermissionStore.site(for: "") == nil)
    }

    @Test func remembersAndForgetsDecisions() {
        let store = SitePermissionStore(fileURL: temporaryFile("permissions.json"))
        #expect(store.decision(for: .camera, site: "https://meet.example") == nil)
        store.set(.allow, for: .camera, site: "https://meet.example/room/42")
        store.set(.block, for: .location, site: "https://meet.example")
        #expect(store.decision(for: .camera, site: "https://meet.example") == .allow)
        #expect(store.decision(for: .location, site: "https://meet.example/x") == .block)
        // Another origin (scheme or port) is another site.
        #expect(store.decision(for: .camera, site: "http://meet.example") == nil)
        store.set(nil, for: .camera, site: "https://meet.example")
        #expect(store.decision(for: .camera, site: "https://meet.example") == nil)
        #expect(store.entries() == [Entry(site: "https://meet.example", permission: .location, decision: .block)])
    }

    @Test func persistsAcrossInstances() {
        let url = temporaryFile("permissions.json")
        do {
            let store = SitePermissionStore(fileURL: url)
            store.set(.allow, for: .notifications, site: "https://b.example")
            store.set(.block, for: .popups, site: "https://a.example")
            store.set(.allow, for: .camera, site: "https://a.example")
        }
        let reopened = SitePermissionStore(fileURL: url)
        #expect(reopened.decision(for: .notifications, site: "https://b.example") == .allow)
        // Sorted by site, then in the order permissions are declared.
        #expect(reopened.entries().map { "\($0.site) \($0.permission.rawValue) \($0.decision.rawValue)" } == [
            "https://a.example camera allow",
            "https://a.example popups block",
            "https://b.example notifications allow",
        ])
    }

    @Test func ignoresUnreadableFilesAndNonWebSites() throws {
        let url = temporaryFile("permissions.json")
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("not json".utf8).write(to: url)
        let store = SitePermissionStore(fileURL: url)
        #expect(store.entries().isEmpty)
        store.set(.allow, for: .camera, site: "about:blank")
        #expect(store.entries().isEmpty)
    }

    @Test func postsChangeNotifications() {
        let store = SitePermissionStore(fileURL: temporaryFile("permissions.json"))
        var received: [String] = []
        let observer = NotificationCenter.default.addObserver(
            forName: SitePermissionStore.didChangeNotification, object: store, queue: nil
        ) { note in
            let entry = note.userInfo?["entry"] as? SitePermissionStore.Entry
            received.append("\(note.userInfo?["site"] ?? "") \(entry?.decision.rawValue ?? "forgotten")")
        }
        defer { NotificationCenter.default.removeObserver(observer) }
        store.set(.allow, for: .clipboard, site: "https://x.example")
        store.set(.allow, for: .clipboard, site: "https://x.example") // unchanged: no notification
        store.removeAll(forSite: "https://x.example")
        #expect(received == ["https://x.example allow", "https://x.example forgotten"])
    }

    @Test func displayNames() {
        #expect(SitePermissionStore.displayName(forSite: "https://example.com") == "example.com")
        #expect(SitePermissionStore.displayName(forSite: "http://localhost:8000") == "http://localhost:8000")
        #expect(SitePermissionStore.displayName(forSite: "file://") == "Local files")
    }

    private typealias Entry = SitePermissionStore.Entry
}

@Suite struct PageZoomTests {
    @Test func stepsInAndOut() {
        #expect(PageZoom.zoomedIn(from: 1) == 1.1)
        #expect(PageZoom.zoomedIn(from: 1.05) == 1.1)
        #expect(PageZoom.zoomedOut(from: 1) == 0.9)
        #expect(PageZoom.zoomedOut(from: 0.95) == 0.9)
        #expect(PageZoom.zoomedIn(from: 5) == 5)
        #expect(PageZoom.zoomedOut(from: 0.33) == 0.33)
        // A factor set elsewhere (Gecko's own ⌘+ steps 10 %) snaps to the next step.
        #expect(PageZoom.zoomedIn(from: 1.2) == 1.25)
    }

    @Test func chromiumLevelsRoundTrip() {
        #expect(abs(PageZoom.chromiumLevel(forFactor: 1)) < 1e-9)
        #expect(abs(PageZoom.chromiumLevel(forFactor: 1.2) - 1) < 1e-9)
        for factor in PageZoom.steps {
            let back = PageZoom.factor(forChromiumLevel: PageZoom.chromiumLevel(forFactor: factor))
            #expect(abs(back - factor) < 1e-9)
        }
    }

    @Test func zoomSitesIgnoreWWWAndScheme() {
        #expect(PageZoom.site(for: "https://www.Example.com/a") == "example.com")
        #expect(PageZoom.site(for: "http://example.com:8080/") == "example.com")
        #expect(PageZoom.site(for: "file:///tmp/a.html") == nil)
        #expect(PageZoom.site(for: "about:blank") == nil)
    }

    @Test func storeRemembersPerSiteAndForgetsDefault() {
        let url = temporaryFile("zoom.json")
        let store = SiteZoomStore(fileURL: url)
        #expect(store.factor(for: "https://example.com") == 1)
        #expect(store.setFactor(1.25, for: "https://www.example.com/page"))
        #expect(store.factor(for: "http://example.com/other") == 1.25)
        #expect(!store.setFactor(2, for: "about:blank"))
        #expect(SiteZoomStore(fileURL: url).factor(for: "https://example.com") == 1.25)
        store.setFactor(1, for: "https://example.com")
        #expect(store.sites.isEmpty)
        #expect(SiteZoomStore(fileURL: url).factor(for: "https://example.com") == 1)
        store.setFactor(99, for: "https://big.example")
        #expect(store.factor(for: "https://big.example") == PageZoom.maximum)
    }

    @Test func percentLabels() {
        #expect(PageZoom.percentLabel(1.1) == "110 %")
        #expect(PageZoom.percentLabel(0.67) == "67 %")
    }
}

@Suite struct FindStatusTests {
    @Test func labels() {
        #expect(FindStatus(current: 0, total: 0).label == "No matches")
        #expect(FindStatus(current: 3, total: 12).label == "3 of 12")
        #expect(FindStatus(current: 0, total: 4).label == "4 matches")
        #expect(FindStatus(current: 2, total: 1000, limit: 1000).label == "2 of 1000+")
        #expect(FindStatus(current: 3, total: 12).accessibilityLabel == "Match 3 of 12")
        #expect(!FindStatus(current: 0, total: 0).hasMatches)
    }
}

@Suite struct PermissionPromptingTests {
    private func store() -> SitePermissionStore {
        SitePermissionStore(fileURL: temporaryFile("permissions.json"))
    }

    @Test func sentences() {
        let both = PermissionRequestInfo(origin: "https://meet.example", kinds: [.camera, .microphone])
        #expect(both.sentence == "meet.example wants to use your camera and microphone")
        let location = PermissionRequestInfo(origin: "http://localhost:8000", kinds: [.location])
        #expect(location.sentence == "http://localhost:8000 wants to know your location")
        let mixed = PermissionRequestInfo(origin: "https://a.example", kinds: [.notifications, .other], otherNames: ["midi"])
        #expect(mixed.sentence == "a.example wants to show notifications and control your MIDI devices")
        let anonymous = PermissionRequestInfo(origin: "null", kinds: [.clipboard])
        #expect(anonymous.sentence == "This page wants to see text and images you copy")
    }

    @Test func decidesFromRememberedChoices() {
        let store = store()
        let request = PermissionRequestInfo(origin: "https://meet.example", kinds: [.camera, .microphone])
        #expect(request.automaticDecision(in: store) == nil)
        store.set(.allow, for: .camera, site: "https://meet.example")
        #expect(request.automaticDecision(in: store) == nil)  // microphone still undecided
        store.set(.allow, for: .microphone, site: "https://meet.example")
        #expect(request.automaticDecision(in: store) == .allow)
        store.set(.block, for: .camera, site: "https://meet.example")
        #expect(request.automaticDecision(in: store) == .block)
    }

    @Test func screenSharingAndUnknownKindsAreAlwaysAsked() {
        let store = store()
        store.set(.allow, for: .camera, site: "https://meet.example")
        let screen = PermissionRequestInfo(origin: "https://meet.example", kinds: [.screen])
        #expect(!screen.isRememberable)
        #expect(screen.automaticDecision(in: store) == nil)
        let opaque = PermissionRequestInfo(origin: "null", kinds: [.camera])
        #expect(!opaque.isRememberable)
    }
}
