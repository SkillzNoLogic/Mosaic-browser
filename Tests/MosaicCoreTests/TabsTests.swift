import Foundation
import Testing
@testable import MosaicCore

// Tabs area: ordering rules, sessions, recently closed, duplicate detection, tab search,
// sleeping-tabs policy, settings.

private let wsA = UUID()
private let wsB = UUID()
private let groupX = UUID()
private let groupY = UUID()

private func slot(_ ws: UUID = wsA, pinned: Bool = false, group: UUID? = nil) -> TabSlot {
    TabSlot(id: UUID(), workspaceID: ws, isPinned: pinned, groupID: group)
}

private func isAt(_ result: (index: Int, groupID: UUID?), _ index: Int, group: UUID? = nil) -> Bool {
    result.index == index && result.groupID == group
}

@Suite struct TabOrderingTests {
    @Test func normalizePutsPinnedFirstPerWorkspaceAndKeepsInterleaving() {
        let a1 = slot(), b1 = slot(wsB), a2 = slot(pinned: true), b2 = slot(wsB, pinned: true), a3 = slot()
        let result = TabOrdering.normalized([a1, b1, a2, b2, a3])
        // Workspace A occupied positions 0, 2, 4 and still does; same for B at 1, 3.
        #expect(result.map(\.id) == [a2.id, b2.id, a1.id, b1.id, a3.id])
        #expect(TabOrdering.isNormalized(result))
    }

    @Test func normalizeGathersGroupsAtFirstMemberAndUngroupsPinned() {
        let g1 = slot(group: groupX), plain = slot(), g2 = slot(group: groupX), pinned = slot(pinned: true, group: groupY)
        let result = TabOrdering.normalized([g1, plain, g2, pinned])
        #expect(result.map(\.id) == [pinned.id, g1.id, g2.id, plain.id])
        #expect(result[0].groupID == nil)
    }

    @Test func moveClampsToPinnedSection() {
        let p1 = slot(pinned: true), p2 = slot(pinned: true), t1 = slot(), t2 = slot()
        let slots = [p1, p2, t1, t2]
        // An unpinned tab dropped at the very front lands after the pinned tabs.
        #expect(TabOrdering.move(slots, id: t2.id, to: 0).map(\.id) == [p1.id, p2.id, t2.id, t1.id])
        // A pinned tab dropped at the end stays the last pinned tab.
        #expect(TabOrdering.move(slots, id: p1.id, to: 9).map(\.id) == [p2.id, p1.id, t1.id, t2.id])
    }

    @Test func movingBetweenGroupMembersJoinsTheGroup() {
        let g1 = slot(group: groupX), g2 = slot(group: groupX), loose = slot()
        let result = TabOrdering.move([g1, g2, loose], id: loose.id, to: 1)
        #expect(result.map(\.id) == [g1.id, loose.id, g2.id])
        #expect(result[1].groupID == groupX)
    }

    @Test func movingAwayFromGroupLeavesItButEdgeKeepsIt() {
        let g1 = slot(group: groupX), g2 = slot(group: groupX), a = slot(), b = slot()
        let slots = [g1, g2, a, b]
        let away = TabOrdering.move(slots, id: g1.id, to: 3)
        #expect(away.map(\.id) == [g2.id, a.id, b.id, g1.id])
        #expect(away[3].groupID == nil)
        // Moving g2 in front of g1: next to its own group, so it stays a member.
        let edge = TabOrdering.move(slots, id: g2.id, to: 0)
        #expect(edge.map(\.id) == [g2.id, g1.id, a.id, b.id])
        #expect(edge[0].groupID == groupX)
    }

    @Test func moveOnlyAffectsTheTabsWorkspace() {
        let a1 = slot(), b1 = slot(wsB), a2 = slot()
        let result = TabOrdering.move([a1, b1, a2], id: a2.id, to: 0)
        #expect(result.filter { $0.workspaceID == wsA }.map(\.id) == [a2.id, a1.id])
        #expect(result.filter { $0.workspaceID == wsB }.map(\.id) == [b1.id])
    }

    @Test func moveGroupSkipsPinnedAndOtherGroups() {
        let p = slot(pinned: true), y1 = slot(group: groupY), y2 = slot(group: groupY), x1 = slot(group: groupX), x2 = slot(group: groupX)
        let slots = [p, y1, y2, x1, x2]
        // Index 0 is clamped after the pinned tab.
        #expect(TabOrdering.moveGroup(slots, groupID: groupX, to: 0).map(\.id) == [p.id, x1.id, x2.id, y1.id, y2.id])
        // Index 2 is inside group Y (between y1 and y2): goes after Y.
        #expect(TabOrdering.moveGroup(slots, groupID: groupX, to: 2).map(\.id) == [p.id, y1.id, y2.id, x1.id, x2.id])
    }

    @Test func insertionRules() {
        let p = slot(pinned: true), g1 = slot(group: groupX), g2 = slot(group: groupX), t = slot(), other = slot(wsB)
        let slots = [p, g1, g2, t, other]
        // From a pinned tab: after the pinned tabs.
        #expect(isAt(TabOrdering.insertion(in: slots, workspace: wsA, after: p.id, joiningAnchorGroup: false), 1))
        // A link from a grouped tab: right after it, in the group.
        let link = TabOrdering.insertion(in: slots, workspace: wsA, after: g1.id, joiningAnchorGroup: true)
        #expect(link.index == 2 && link.groupID == groupX)
        // A plain new tab from a grouped tab: after the group, ungrouped.
        #expect(isAt(TabOrdering.insertion(in: slots, workspace: wsA, after: g1.id, joiningAnchorGroup: false), 3))
        // Anchor in another workspace: last tab of the workspace.
        #expect(isAt(TabOrdering.insertion(in: slots, workspace: wsA, after: other.id, joiningAnchorGroup: false), 4))
        // Empty workspace: end of the list.
        #expect(isAt(TabOrdering.insertion(in: slots, workspace: UUID(), after: t.id, joiningAnchorGroup: false), 5))
    }

    @Test func appendIndexForWorkspaceMoves() {
        let p = slot(wsB, pinned: true), b = slot(wsB), a = slot()
        let slots = [p, b, a]
        #expect(TabOrdering.appendIndex(in: slots, workspace: wsB, pinned: false) == 2)
        #expect(TabOrdering.appendIndex(in: slots, workspace: wsB, pinned: true) == 1)
        #expect(TabOrdering.appendIndex(in: slots, workspace: UUID(), pinned: false) == 3)
    }
}

@Suite struct SessionCodecTests {
    private func sample() -> SessionSnapshot {
        let work = SessionWorkspace(name: "Work", symbol: "briefcase")
        let home = SessionWorkspace(name: "Home")
        let group = SessionGroup(name: "Docs", color: "green", isCollapsed: true)
        return SessionSnapshot(
            savedAt: Date(timeIntervalSince1970: 1_800_000_000),
            workspaces: [work, home], selectedWorkspaceID: home.id, groups: [group],
            tabs: [
                SessionTab(engine: "gecko", url: "https://mozilla.org/", title: "Mozilla", isPinned: true,
                           workspaceID: work.id, isSelected: true),
                SessionTab(engine: "chromium", url: "https://example.com/", title: "Example", isMuted: true,
                           workspaceID: work.id, groupID: group.id),
                SessionTab(engine: "gecko", url: "", workspaceID: home.id, isSelected: true),
            ],
            recentlyClosed: [ClosedItem(kind: .tab, tabs: [ClosedTabRecord(engine: "gecko", url: "https://a.b/", title: "A", workspaceID: home.id)])])
    }

    @Test func roundTrip() throws {
        let snapshot = sample()
        let decoded = try SessionCodec.decode(SessionCodec.encode(snapshot))
        #expect(decoded.workspaces == snapshot.workspaces)
        #expect(decoded.tabs == snapshot.tabs)
        #expect(decoded.groups == snapshot.groups)
        #expect(decoded.selectedWorkspaceID == snapshot.selectedWorkspaceID)
        #expect(decoded.recentlyClosed.count == 1)
        #expect(decoded.savedAt == snapshot.savedAt)
        #expect(decoded.pageCount == 2)
    }

    @Test func olderFileWithoutVersionOrNewFieldsStillLoads() throws {
        let ws = UUID()
        let json = """
        {"workspaces":[{"id":"\(ws)","name":"Old"}],
         "tabs":[{"url":"https://old.example/","workspaceID":"\(ws)"},{"url":"https://two.example/","engine":"chromium"}]}
        """
        let decoded = try SessionCodec.decode(Data(json.utf8))
        #expect(decoded.version == SessionSnapshot.currentVersion)
        #expect(decoded.workspaces.map(\.name) == ["Old"])
        #expect(decoded.workspaces[0].symbol == "square.grid.2x2")
        #expect(decoded.tabs.count == 2)
        #expect(decoded.tabs[0].engine == "gecko")
        #expect(decoded.tabs[0].isPinned == false)
        // The tab with no workspace was put in the first one.
        #expect(decoded.tabs[1].workspaceID == ws)
        #expect(decoded.selectedWorkspaceID == ws)
    }

    @Test func newerVersionIsRefused() {
        let json = #"{"version": 99, "tabs": []}"#
        #expect(throws: SessionCodec.DecodeError.newerVersion(99)) { try SessionCodec.decode(Data(json.utf8)) }
    }

    @Test func garbageIsMalformed() {
        #expect(throws: SessionCodec.DecodeError.malformed) { try SessionCodec.decode(Data("not json".utf8)) }
        #expect(throws: SessionCodec.DecodeError.malformed) { try SessionCodec.decode(Data("[1,2]".utf8)) }
    }

    @Test func repairFixesInconsistentReferences() {
        let ws = SessionWorkspace(name: "W")
        let ws2 = SessionWorkspace(name: "V")
        let group = SessionGroup(name: "G")
        let duplicateID = UUID()
        var snapshot = SessionSnapshot(
            workspaces: [ws, ws, ws2], selectedWorkspaceID: UUID(),
            groups: [group, SessionGroup(name: "Empty")],
            tabs: [
                SessionTab(id: duplicateID, engine: "gecko", url: "https://1/", workspaceID: ws.id, groupID: group.id, isSelected: true),
                SessionTab(id: duplicateID, engine: "gecko", url: "https://dup/", workspaceID: ws.id),
                SessionTab(engine: "gecko", url: "https://2/", workspaceID: UUID(), isSelected: true),
                SessionTab(engine: "gecko", url: "https://3/", isPinned: true, workspaceID: ws.id, groupID: group.id),
                SessionTab(engine: "gecko", url: "https://4/", workspaceID: ws2.id, groupID: group.id),
                SessionTab(engine: "gecko", url: "https://5/", workspaceID: ws.id, groupID: UUID()),
            ])
        snapshot.recentlyClosed = [ClosedItem(kind: .group, tabs: [])]
        let repaired = snapshot.repaired()
        #expect(repaired.workspaces.count == 2)
        #expect(repaired.selectedWorkspaceID == ws.id)
        #expect(repaired.tabs.count == 5)
        #expect(repaired.groups.map(\.id) == [group.id])
        // Pinned first, then the group, then the rest.
        #expect(repaired.tabs.map(\.url).filter { $0 != "https://4/" } == ["https://3/", "https://1/", "https://2/", "https://5/"])
        #expect(repaired.tabs.first { $0.url == "https://3/" }?.groupID == nil)
        // The group belongs to workspace W; the member in V was ungrouped.
        #expect(repaired.tabs.first { $0.url == "https://4/" }?.groupID == nil)
        #expect(repaired.tabs.first { $0.url == "https://5/" }?.groupID == nil)
        // Only one selected tab per workspace (the stray tab joined W, which had one).
        #expect(repaired.tabs.filter { $0.isSelected && $0.workspaceID == ws.id }.count == 1)
        #expect(repaired.recentlyClosed.isEmpty)
    }

    @Test func emptySnapshotGetsAWorkspace() {
        let repaired = SessionSnapshot(workspaces: [], selectedWorkspaceID: nil).repaired()
        #expect(repaired.workspaces.count == 1)
        #expect(repaired.selectedWorkspaceID == repaired.workspaces[0].id)
    }
}

@Suite struct SessionStoreTests {
    private func temporaryStore() -> SessionStore {
        SessionStore(directory: FileManager.default.temporaryDirectory
            .appendingPathComponent("mosaic-tabs-tests-\(UUID().uuidString)", isDirectory: true))
    }

    @Test func detectsFirstRunCleanExitAndCrash() throws {
        let store = temporaryStore()
        defer { try? FileManager.default.removeItem(at: store.directory) }
        #expect(store.beginRun(pid: 100, isAlive: { _ in false }) == .firstRun)
        try store.save(SessionSnapshot(workspaces: [SessionWorkspace(name: "W")], selectedWorkspaceID: nil))
        store.endRun()
        #expect(store.beginRun(pid: 101, isAlive: { _ in false }) == .clean)
        // 101 never called endRun and is gone: a crash.
        #expect(store.beginRun(pid: 102, isAlive: { _ in false }) == .crashed)
        // 102's marker is still there but 102 is alive: another copy, not a crash.
        #expect(store.beginRun(pid: 103, isAlive: { $0 == 102 }) == .clean)
    }

    @Test func saveLoadAndPrevious() throws {
        let store = temporaryStore()
        defer { try? FileManager.default.removeItem(at: store.directory) }
        #expect(try store.load() == nil)
        let ws = SessionWorkspace(name: "W")
        let snapshot = SessionSnapshot(workspaces: [ws], selectedWorkspaceID: ws.id,
                                       tabs: [SessionTab(engine: "gecko", url: "https://x/", workspaceID: ws.id)])
        try store.save(snapshot)
        #expect(try store.load()?.tabs.map(\.url) == ["https://x/"])
        try store.savePrevious(snapshot)
        #expect(try store.loadPrevious()?.pageCount == 1)
        store.removePrevious()
        #expect(try store.loadPrevious() == nil)
    }

    @Test func unreadableFileIsSetAside() throws {
        let store = temporaryStore()
        defer { try? FileManager.default.removeItem(at: store.directory) }
        try FileManager.default.createDirectory(at: store.directory, withIntermediateDirectories: true)
        try Data(#"{"version": 7}"#.utf8).write(to: store.sessionURL)
        #expect(throws: SessionCodec.DecodeError.newerVersion(7)) { try store.load() }
        let moved = try #require(store.setAsideUnreadable())
        #expect(FileManager.default.fileExists(atPath: moved.path))
        #expect(try store.load() == nil)
    }
}

@Suite struct RecentlyClosedTests {
    private func item(_ url: String, kind: ClosedItem.Kind = .tab) -> ClosedItem {
        ClosedItem(kind: kind, tabs: [ClosedTabRecord(engine: "gecko", url: url, title: url, workspaceID: nil)])
    }

    @Test func newestFirstAndLimited() {
        var list = RecentlyClosedList()
        for i in 0..<5 { list.push(item("https://\(i)/"), limit: 3) }
        #expect(list.items.map { $0.tabs[0].url } == ["https://4/", "https://3/", "https://2/"])
        #expect(list.popLatest()?.tabs[0].url == "https://4/")
        #expect(list.items.count == 2)
        list.trim(to: 1)
        #expect(list.items.map { $0.tabs[0].url } == ["https://3/"])
    }

    @Test func startPageTabsAreNotRemembered() {
        var list = RecentlyClosedList()
        list.push(item(""), limit: 10)
        #expect(list.items.isEmpty)
        var mixed = item("https://a/", kind: .group)
        mixed.tabs.append(ClosedTabRecord(engine: "gecko", url: "", title: "", workspaceID: nil))
        list.push(mixed, limit: 10)
        #expect(list.items.first?.tabs.count == 1)
    }

    @Test func removeByIDAndTitles() {
        var list = RecentlyClosedList()
        let a = item("https://a.example/path")
        list.push(a, limit: 10)
        list.push(ClosedItem(kind: .group, tabs: a.tabs + a.tabs, group: SessionGroup(name: "Research")), limit: 10)
        #expect(list.items[0].title == "Research (2 tabs)")
        #expect(list.remove(id: a.id)?.id == a.id)
        #expect(list.remove(id: a.id) == nil)
        let untitled = ClosedTabRecord(engine: "gecko", url: "https://host.example/x", title: "", workspaceID: nil)
        #expect(untitled.displayTitle == "host.example")
    }

    @Test func popOnEmptyIsNil() {
        var list = RecentlyClosedList()
        #expect(list.popLatest() == nil)
    }
}

@Suite struct DuplicateDetectionTests {
    @Test func equivalentAddresses() {
        #expect(URLIdentity.samePage("https://www.Example.com/", "http://example.com"))
        #expect(URLIdentity.samePage("https://example.com/a/#section", "https://example.com/a"))
        #expect(URLIdentity.samePage("https://example.com:443/a", "https://example.com/a/"))
    }

    @Test func differentAddresses() {
        #expect(!URLIdentity.samePage("https://example.com/a?x=1", "https://example.com/a?x=2"))
        #expect(!URLIdentity.samePage("https://example.com/a", "https://example.org/a"))
        #expect(!URLIdentity.samePage("https://example.com:8080/", "https://example.com/"))
        #expect(!URLIdentity.samePage("", ""))
        #expect(!URLIdentity.samePage("about:blank", "about:blank"))
        #expect(!URLIdentity.samePage("mosaic://history", "mosaic://history"))
    }

    @Test func firstMatchSkipsExcluded() {
        let a = UUID(), b = UUID()
        let candidates = [(id: a, url: "https://example.com/"), (id: b, url: "https://www.example.com")]
        #expect(DuplicateFinder.firstMatch(for: "https://example.com", among: candidates) == a)
        #expect(DuplicateFinder.firstMatch(for: "https://example.com", among: candidates, excluding: a) == b)
        #expect(DuplicateFinder.firstMatch(for: "https://nothing.example", among: candidates) == nil)
    }
}

@Suite struct TabSearchTests {
    @Test func matchesAllTermsAndRanksTitlePrefixFirst() {
        let tabs = [
            (title: "Swift Forums", url: "https://forums.swift.org/"),
            (title: "Taylor Swift", url: "https://en.wikipedia.org/wiki/Taylor_Swift"),
            (title: "Docs", url: "https://developer.apple.com/swift/"),
            (title: "Mail", url: "https://mail.example.com/"),
        ]
        let ranked = TabSearch.rank(tabs, query: "swift", title: \.title, url: \.url).map(\.title)
        #expect(ranked == ["Swift Forums", "Taylor Swift", "Docs"])
        #expect(TabSearch.rank(tabs, query: "swift wiki", title: \.title, url: \.url).map(\.title) == ["Taylor Swift"])
        #expect(TabSearch.rank(tabs, query: "", title: \.title, url: \.url).count == 4)
    }

    @Test func ignoresCaseAndAccents() {
        #expect(TabSearch.score(query: "CAFE", title: "Le Café", url: "https://x/") != nil)
        #expect(TabSearch.score(query: "mail", title: "Inbox", url: "https://mail.example.com/") != nil)
        #expect(TabSearch.score(query: "zzz", title: "Inbox", url: "https://mail.example.com/") == nil)
    }
}

@Suite struct SleepPolicyTests {
    private let now = Date(timeIntervalSince1970: 10_000)

    private func tab(minutesAgo: Double, _ configure: (inout SleepCandidate) -> Void = { _ in }) -> SleepCandidate {
        var candidate = SleepCandidate(id: UUID(), lastActive: now.addingTimeInterval(-minutesAgo * 60))
        configure(&candidate)
        return candidate
    }

    @Test func unloadsOnlyTabsPastTheThresholdOldestFirst() {
        let settings = SleepSettings(inactiveMinutes: 30)
        let old = tab(minutesAgo: 90), older = tab(minutesAgo: 120), fresh = tab(minutesAgo: 10)
        let result = SleepPolicy.tabsToUnload([old, fresh, older], settings: settings, pressure: .normal, now: now)
        #expect(result == [older.id, old.id])
    }

    @Test func exemptions() {
        let settings = SleepSettings(inactiveMinutes: 5, neverUnloadPinned: true)
        let tabs = [
            tab(minutesAgo: 60) { $0.isSelected = true },
            tab(minutesAgo: 60) { $0.isAsleep = true },
            tab(minutesAgo: 60) { $0.hasLoadedContent = false },
            tab(minutesAgo: 60) { $0.isPinned = true },
            tab(minutesAgo: 60) { $0.isPlayingAudio = true },
            tab(minutesAgo: 60) { $0.hasActiveDownload = true },
        ]
        #expect(SleepPolicy.tabsToUnload(tabs, settings: settings, pressure: .critical, now: now).isEmpty)
        #expect(SleepPolicy.exemption(for: tabs[4], settings: settings) == .playingAudio)
        var allowPinned = settings
        allowPinned.neverUnloadPinned = false
        #expect(SleepPolicy.tabsToUnload(tabs, settings: allowPinned, pressure: .normal, now: now) == [tabs[3].id])
    }

    @Test func memoryPressureShortensTheThreshold() {
        let settings = SleepSettings(inactiveMinutes: 60)
        let twentyMinutes = tab(minutesAgo: 20), twoMinutes = tab(minutesAgo: 2), justNow = tab(minutesAgo: 0)
        let tabs = [twentyMinutes, twoMinutes, justNow]
        #expect(SleepPolicy.tabsToUnload(tabs, settings: settings, pressure: .normal, now: now).isEmpty)
        #expect(SleepPolicy.tabsToUnload(tabs, settings: settings, pressure: .warning, now: now) == [twentyMinutes.id])
        #expect(SleepPolicy.tabsToUnload(tabs, settings: settings, pressure: .critical, now: now).count == 3)
    }

    @Test func switches() {
        let off = SleepSettings(unloadInactiveTabs: false, respondToMemoryPressure: true)
        #expect(SleepPolicy.threshold(settings: off, pressure: .normal) == nil)
        #expect(SleepPolicy.threshold(settings: off, pressure: .critical) == 0)
        let ignorePressure = SleepSettings(unloadInactiveTabs: false, respondToMemoryPressure: false)
        #expect(SleepPolicy.threshold(settings: ignorePressure, pressure: .critical) == nil)
        #expect(SleepPolicy.threshold(settings: SleepSettings(inactiveMinutes: 2), pressure: .warning) == 60)
    }
}

@Suite struct TabSettingsTests {
    @Test func defaultsAndRoundTrip() {
        var settings = TabSettings()
        #expect(settings.startup == .previousSession)
        settings.startup = .startPage
        settings.sleep.inactiveMinutes = 15
        #expect(TabSettings.decode(settings.encoded()) == settings)
    }

    @Test func tolerantDecoding() {
        #expect(TabSettings.decode(nil) == TabSettings())
        #expect(TabSettings.decode(Data("garbage".utf8)) == TabSettings())
        let partial = TabSettings.decode(Data(#"{"startup":"startPage","recentlyClosedLimit":100000,"sleep":{"inactiveMinutes":0}}"#.utf8))
        #expect(partial.startup == .startPage)
        #expect(partial.recentlyClosedLimit == TabSettings.recentlyClosedRange.upperBound)
        #expect(partial.sleep.inactiveMinutes == 1)
        #expect(partial.sleep.unloadInactiveTabs == true)
        #expect(partial.protectPinnedTabs == true)
        let unknownStartup = TabSettings.decode(Data(#"{"startup":"somethingNew"}"#.utf8))
        #expect(unknownStartup.startup == .previousSession)
    }
}
