using Mosaic.Core.Tabs;

namespace Mosaic.Core.Tests.Tabs;

// Ports of Tests/MosaicCoreTests/TabsTests.swift: ordering rules, sessions, recently closed,
// duplicate detection, tab search, sleeping-tabs policy, settings.

public class TabOrderingTests
{
    private static readonly Guid WsA = Guid.NewGuid();
    private static readonly Guid WsB = Guid.NewGuid();
    private static readonly Guid GroupX = Guid.NewGuid();
    private static readonly Guid GroupY = Guid.NewGuid();

    private static TabSlot Slot(Guid? ws = null, bool pinned = false, Guid? group = null) =>
        new(Guid.NewGuid(), ws ?? WsA, pinned, group);

    private static Guid[] Ids(IEnumerable<TabSlot> slots) => slots.Select(s => s.Id).ToArray();

    [Fact]
    public void NormalizePutsPinnedFirstPerWorkspaceAndKeepsInterleaving()
    {
        TabSlot a1 = Slot(), b1 = Slot(WsB), a2 = Slot(pinned: true), b2 = Slot(WsB, pinned: true), a3 = Slot();
        var result = TabOrdering.Normalized([a1, b1, a2, b2, a3]);
        Assert.Equal([a2.Id, b2.Id, a1.Id, b1.Id, a3.Id], Ids(result));
        Assert.True(TabOrdering.IsNormalized(result));
    }

    [Fact]
    public void NormalizeGathersGroupsAtFirstMemberAndUngroupsPinned()
    {
        TabSlot g1 = Slot(group: GroupX), plain = Slot(), g2 = Slot(group: GroupX), pinned = Slot(pinned: true, group: GroupY);
        var result = TabOrdering.Normalized([g1, plain, g2, pinned]);
        Assert.Equal([pinned.Id, g1.Id, g2.Id, plain.Id], Ids(result));
        Assert.Null(result[0].GroupId);
    }

    [Fact]
    public void MoveClampsToPinnedSection()
    {
        TabSlot p1 = Slot(pinned: true), p2 = Slot(pinned: true), t1 = Slot(), t2 = Slot();
        TabSlot[] slots = [p1, p2, t1, t2];
        Assert.Equal([p1.Id, p2.Id, t2.Id, t1.Id], Ids(TabOrdering.Move(slots, t2.Id, 0)));
        Assert.Equal([p2.Id, p1.Id, t1.Id, t2.Id], Ids(TabOrdering.Move(slots, p1.Id, 9)));
    }

    [Fact]
    public void MovingBetweenGroupMembersJoinsTheGroup()
    {
        TabSlot g1 = Slot(group: GroupX), g2 = Slot(group: GroupX), loose = Slot();
        var result = TabOrdering.Move([g1, g2, loose], loose.Id, 1);
        Assert.Equal([g1.Id, loose.Id, g2.Id], Ids(result));
        Assert.Equal(GroupX, result[1].GroupId);
    }

    [Fact]
    public void MovingAwayFromGroupLeavesItButEdgeKeepsIt()
    {
        TabSlot g1 = Slot(group: GroupX), g2 = Slot(group: GroupX), a = Slot(), b = Slot();
        TabSlot[] slots = [g1, g2, a, b];
        var away = TabOrdering.Move(slots, g1.Id, 3);
        Assert.Equal([g2.Id, a.Id, b.Id, g1.Id], Ids(away));
        Assert.Null(away[3].GroupId);
        var edge = TabOrdering.Move(slots, g2.Id, 0);
        Assert.Equal([g2.Id, g1.Id, a.Id, b.Id], Ids(edge));
        Assert.Equal(GroupX, edge[0].GroupId);
    }

    [Fact]
    public void MoveOnlyAffectsTheTabsWorkspace()
    {
        TabSlot a1 = Slot(), b1 = Slot(WsB), a2 = Slot();
        var result = TabOrdering.Move([a1, b1, a2], a2.Id, 0);
        Assert.Equal([a2.Id, a1.Id], Ids(result.Where(s => s.WorkspaceId == WsA)));
        Assert.Equal([b1.Id], Ids(result.Where(s => s.WorkspaceId == WsB)));
    }

    [Fact]
    public void MoveOfAnUnknownTabChangesNothing()
    {
        TabSlot a = Slot(), b = Slot();
        Assert.Equal([a.Id, b.Id], Ids(TabOrdering.Move([a, b], Guid.NewGuid(), 0)));
    }

    [Fact]
    public void MoveGroupSkipsPinnedAndOtherGroups()
    {
        TabSlot p = Slot(pinned: true), y1 = Slot(group: GroupY), y2 = Slot(group: GroupY), x1 = Slot(group: GroupX), x2 = Slot(group: GroupX);
        TabSlot[] slots = [p, y1, y2, x1, x2];
        Assert.Equal([p.Id, x1.Id, x2.Id, y1.Id, y2.Id], Ids(TabOrdering.MoveGroup(slots, GroupX, 0)));
        Assert.Equal([p.Id, y1.Id, y2.Id, x1.Id, x2.Id], Ids(TabOrdering.MoveGroup(slots, GroupX, 2)));
    }

    [Fact]
    public void InsertionRules()
    {
        TabSlot p = Slot(pinned: true), g1 = Slot(group: GroupX), g2 = Slot(group: GroupX), t = Slot(), other = Slot(WsB);
        TabSlot[] slots = [p, g1, g2, t, other];
        Assert.Equal((1, (Guid?)null), TabOrdering.Insertion(slots, WsA, p.Id, false));
        Assert.Equal((2, (Guid?)GroupX), TabOrdering.Insertion(slots, WsA, g1.Id, true));
        Assert.Equal((3, (Guid?)null), TabOrdering.Insertion(slots, WsA, g1.Id, false));
        Assert.Equal((4, (Guid?)null), TabOrdering.Insertion(slots, WsA, other.Id, false));
        Assert.Equal((5, (Guid?)null), TabOrdering.Insertion(slots, Guid.NewGuid(), t.Id, false));
    }

    [Fact]
    public void AppendIndexForWorkspaceMoves()
    {
        TabSlot p = Slot(WsB, pinned: true), b = Slot(WsB), a = Slot();
        TabSlot[] slots = [p, b, a];
        Assert.Equal(2, TabOrdering.AppendIndex(slots, WsB, false));
        Assert.Equal(1, TabOrdering.AppendIndex(slots, WsB, true));
        Assert.Equal(3, TabOrdering.AppendIndex(slots, Guid.NewGuid(), false));
    }
}

public class SessionCodecTests
{
    private static SessionSnapshot Sample()
    {
        var work = new SessionWorkspace(Guid.NewGuid(), "Work", "briefcase");
        var home = new SessionWorkspace(Guid.NewGuid(), "Home");
        var group = new SessionGroup(Guid.NewGuid(), "Docs", "green", IsCollapsed: true);
        return new SessionSnapshot
        {
            SavedAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            Workspaces = [work, home],
            SelectedWorkspaceId = home.Id,
            Groups = [group],
            Tabs =
            [
                new SessionTab { Engine = "gecko", Url = "https://mozilla.org/", Title = "Mozilla", IsPinned = true, WorkspaceId = work.Id, IsSelected = true },
                new SessionTab { Engine = "chromium", Url = "https://example.com/", Title = "Example", IsMuted = true, WorkspaceId = work.Id, GroupId = group.Id },
                new SessionTab { Engine = "gecko", Url = "", WorkspaceId = home.Id, IsSelected = true },
            ],
            RecentlyClosed = [new ClosedItem { Kind = ClosedItemKind.Tab, Tabs = [new ClosedTabRecord("gecko", "https://a.b/", "A", home.Id)] }],
        };
    }

    [Fact]
    public void RoundTrip()
    {
        var snapshot = Sample();
        var decoded = SessionCodec.Decode(SessionCodec.Encode(snapshot));
        Assert.Equal(snapshot.Workspaces, decoded.Workspaces);
        Assert.Equal(snapshot.Tabs, decoded.Tabs);
        Assert.Equal(snapshot.Groups, decoded.Groups);
        Assert.Equal(snapshot.SelectedWorkspaceId, decoded.SelectedWorkspaceId);
        Assert.Single(decoded.RecentlyClosed);
        Assert.Equal(snapshot.RecentlyClosed[0].Tabs, decoded.RecentlyClosed[0].Tabs);
        Assert.Equal(snapshot.SavedAt, decoded.SavedAt);
        Assert.Equal(2, decoded.PageCount);
    }

    [Fact]
    public void WritesTheMacOSShape()
    {
        var json = SessionCodec.Encode(Sample());
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        // Swift's Codable keys, sorted; UUIDs upper case; ISO 8601 dates without fractions.
        Assert.Equal(["groups", "recentlyClosed", "savedAt", "selectedWorkspaceID", "tabs", "version", "workspaces"], root.Select(p => p.Key));
        Assert.Equal("2027-01-15T08:00:00Z", root["savedAt"]!.GetValue<string>());
        var tab = root["tabs"]![0]!.AsObject();
        Assert.Equal(["engine", "id", "isMuted", "isPinned", "isSelected", "title", "url", "workspaceID"], tab.Select(p => p.Key));
        var id = tab["id"]!.GetValue<string>();
        Assert.Equal(id.ToUpperInvariant(), id);
        Assert.Equal(["closedAt", "id", "kind", "tabs"], root["recentlyClosed"]![0]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void ReadsAFileWrittenByMacOS()
    {
        // As SessionCodec.encode writes it on macOS (prettyPrinted, sortedKeys, iso8601).
        const string json = """
        {
          "groups" : [ { "color" : "purple", "id" : "8F0C2C6B-5D27-4C3E-9C49-2E5D4C1C9A10", "isCollapsed" : false, "name" : "Reading" } ],
          "recentlyClosed" : [ {
            "closedAt" : "2026-11-20T09:15:00Z", "group" : { "color" : "red", "id" : "0B1D3F4E-1111-4222-8333-944455566677", "isCollapsed" : true, "name" : "Old" },
            "id" : "C3A1F0B2-7777-4888-9999-AAAABBBBCCCC", "kind" : "group",
            "tabs" : [ { "engine" : "chromium", "groupID" : "0B1D3F4E-1111-4222-8333-944455566677", "index" : 2, "isMuted" : false, "isPinned" : false, "title" : "Gone", "url" : "https://gone.example/", "workspaceID" : "6E7F5A2B-1C3D-4E5F-8A9B-0C1D2E3F4A5B" } ]
          } ],
          "savedAt" : "2026-11-27T18:30:05Z",
          "selectedWorkspaceID" : "6E7F5A2B-1C3D-4E5F-8A9B-0C1D2E3F4A5B",
          "tabs" : [
            { "engine" : "gecko", "id" : "11111111-2222-4333-8444-555555555555", "isMuted" : false, "isPinned" : true, "isSelected" : false, "title" : "Mozilla", "url" : "https://www.mozilla.org/", "workspaceID" : "6E7F5A2B-1C3D-4E5F-8A9B-0C1D2E3F4A5B" },
            { "engine" : "chromium", "groupID" : "8F0C2C6B-5D27-4C3E-9C49-2E5D4C1C9A10", "id" : "22222222-2222-4333-8444-555555555555", "isMuted" : true, "isPinned" : false, "isSelected" : true, "title" : "Docs", "url" : "https://docs.example/", "workspaceID" : "6E7F5A2B-1C3D-4E5F-8A9B-0C1D2E3F4A5B" }
          ],
          "version" : 1,
          "workspaces" : [ { "id" : "6E7F5A2B-1C3D-4E5F-8A9B-0C1D2E3F4A5B", "name" : "Personal", "symbol" : "person" } ]
        }
        """;
        var decoded = SessionCodec.Decode(json);
        Assert.Equal("Personal", Assert.Single(decoded.Workspaces).Name);
        Assert.Equal(["https://www.mozilla.org/", "https://docs.example/"], decoded.Tabs.Select(t => t.Url));
        Assert.True(decoded.Tabs[0].IsPinned);
        Assert.True(decoded.Tabs[1].IsMuted);
        Assert.Equal(Guid.Parse("8F0C2C6B-5D27-4C3E-9C49-2E5D4C1C9A10"), decoded.Tabs[1].GroupId);
        Assert.Equal("purple", Assert.Single(decoded.Groups).Color);
        var closed = Assert.Single(decoded.RecentlyClosed);
        Assert.Equal(ClosedItemKind.Group, closed.Kind);
        Assert.Equal("Old (1 tab)", closed.Title);
        Assert.Equal(2, closed.Tabs[0].Index);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 18, 30, 5, TimeSpan.Zero), decoded.SavedAt);
    }

    [Fact]
    public void OlderFileWithoutVersionOrNewFieldsStillLoads()
    {
        var ws = Guid.NewGuid();
        var json = $$"""
        {"workspaces":[{"id":"{{ws}}","name":"Old"}],
         "tabs":[{"url":"https://old.example/","workspaceID":"{{ws}}"},{"url":"https://two.example/","engine":"chromium"}]}
        """;
        var decoded = SessionCodec.Decode(json);
        Assert.Equal(SessionSnapshot.CurrentVersion, decoded.Version);
        Assert.Equal(["Old"], decoded.Workspaces.Select(w => w.Name));
        Assert.Equal("square.grid.2x2", decoded.Workspaces[0].Symbol);
        Assert.Equal(2, decoded.Tabs.Count);
        Assert.Equal("gecko", decoded.Tabs[0].Engine);
        Assert.False(decoded.Tabs[0].IsPinned);
        Assert.Equal(ws, decoded.Tabs[1].WorkspaceId);
        Assert.Equal(ws, decoded.SelectedWorkspaceId);
    }

    [Fact]
    public void NewerVersionIsRefused()
    {
        var error = Assert.Throws<SessionDecodeException>(() => SessionCodec.Decode("""{"version": 99, "tabs": []}"""));
        Assert.Equal(99, error.NewerVersion);
    }

    [Fact]
    public void GarbageIsMalformed()
    {
        Assert.True(Assert.Throws<SessionDecodeException>(() => SessionCodec.Decode("not json")).IsMalformed);
        Assert.True(Assert.Throws<SessionDecodeException>(() => SessionCodec.Decode("[1,2]")).IsMalformed);
    }

    [Fact]
    public void RepairFixesInconsistentReferences()
    {
        var ws = new SessionWorkspace(Guid.NewGuid(), "W");
        var ws2 = new SessionWorkspace(Guid.NewGuid(), "V");
        var group = new SessionGroup(Guid.NewGuid(), "G");
        var duplicateId = Guid.NewGuid();
        var snapshot = new SessionSnapshot
        {
            Workspaces = [ws, ws, ws2],
            SelectedWorkspaceId = Guid.NewGuid(),
            Groups = [group, new SessionGroup(Guid.NewGuid(), "Empty")],
            Tabs =
            [
                new SessionTab { Id = duplicateId, Url = "https://1/", WorkspaceId = ws.Id, GroupId = group.Id, IsSelected = true },
                new SessionTab { Id = duplicateId, Url = "https://dup/", WorkspaceId = ws.Id },
                new SessionTab { Url = "https://2/", WorkspaceId = Guid.NewGuid(), IsSelected = true },
                new SessionTab { Url = "https://3/", IsPinned = true, WorkspaceId = ws.Id, GroupId = group.Id },
                new SessionTab { Url = "https://4/", WorkspaceId = ws2.Id, GroupId = group.Id },
                new SessionTab { Url = "https://5/", WorkspaceId = ws.Id, GroupId = Guid.NewGuid() },
            ],
            RecentlyClosed = [new ClosedItem { Kind = ClosedItemKind.Group }],
        };
        var repaired = snapshot.Repaired();
        Assert.Equal(2, repaired.Workspaces.Count);
        Assert.Equal(ws.Id, repaired.SelectedWorkspaceId);
        Assert.Equal(5, repaired.Tabs.Count);
        Assert.Equal([group.Id], repaired.Groups.Select(g => g.Id));
        Assert.Equal(["https://3/", "https://1/", "https://2/", "https://5/"], repaired.Tabs.Select(t => t.Url).Where(u => u != "https://4/"));
        Assert.Null(repaired.Tabs.First(t => t.Url == "https://3/").GroupId);
        Assert.Null(repaired.Tabs.First(t => t.Url == "https://4/").GroupId);
        Assert.Null(repaired.Tabs.First(t => t.Url == "https://5/").GroupId);
        Assert.Single(repaired.Tabs, t => t.IsSelected && t.WorkspaceId == ws.Id);
        Assert.Empty(repaired.RecentlyClosed);
    }

    [Fact]
    public void EmptySnapshotGetsAWorkspace()
    {
        var repaired = new SessionSnapshot().Repaired();
        Assert.Single(repaired.Workspaces);
        Assert.Equal(repaired.Workspaces[0].Id, repaired.SelectedWorkspaceId);
    }
}

public sealed class SessionStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mosaic-tabs-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void DetectsFirstRunCleanExitAndCrash()
    {
        var store = new SessionStore(directory);
        Assert.Equal(PreviousRun.FirstRun, store.BeginRun(100, _ => false));
        store.Save(new SessionSnapshot { Workspaces = [new SessionWorkspace(Guid.NewGuid(), "W")] });
        store.EndRun();
        Assert.Equal(PreviousRun.Clean, store.BeginRun(101, _ => false));
        // 101 never called EndRun and is gone: a crash.
        Assert.Equal(PreviousRun.Crashed, store.BeginRun(102, _ => false));
        // 102's marker is still there but 102 is alive: another copy, not a crash.
        Assert.Equal(PreviousRun.Clean, store.BeginRun(103, pid => pid == 102));
    }

    [Fact]
    public void SaveLoadAndPrevious()
    {
        var store = new SessionStore(directory);
        Assert.Null(store.Load());
        var ws = new SessionWorkspace(Guid.NewGuid(), "W");
        var snapshot = new SessionSnapshot
        {
            Workspaces = [ws],
            SelectedWorkspaceId = ws.Id,
            Tabs = [new SessionTab { Url = "https://x/", WorkspaceId = ws.Id }],
        };
        store.Save(snapshot);
        Assert.Equal(["https://x/"], store.Load()!.Tabs.Select(t => t.Url));
        Assert.False(File.Exists(store.SessionPath + ".tmp"));
        store.SavePrevious(snapshot);
        Assert.Equal(1, store.LoadPrevious()!.PageCount);
        store.RemovePrevious();
        Assert.Null(store.LoadPrevious());
    }

    [Fact]
    public void UnreadableFileIsSetAside()
    {
        var store = new SessionStore(directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(store.SessionPath, """{"version": 7}""");
        Assert.Equal(7, Assert.Throws<SessionDecodeException>(() => store.Load()).NewerVersion);
        var moved = store.SetAsideUnreadable();
        Assert.NotNull(moved);
        Assert.True(File.Exists(moved));
        Assert.Null(store.Load());
    }
}

public class RecentlyClosedTests
{
    private static ClosedItem Item(string url, ClosedItemKind kind = ClosedItemKind.Tab) =>
        new() { Kind = kind, Tabs = [new ClosedTabRecord("gecko", url, url, null)] };

    [Fact]
    public void NewestFirstAndLimited()
    {
        var list = new RecentlyClosedList();
        for (var i = 0; i < 5; i++) list.Push(Item($"https://{i}/"), 3);
        Assert.Equal(["https://4/", "https://3/", "https://2/"], list.Items.Select(i => i.Tabs[0].Url));
        Assert.Equal("https://4/", list.PopLatest()!.Tabs[0].Url);
        Assert.Equal(2, list.Items.Count);
        list.Trim(1);
        Assert.Equal(["https://3/"], list.Items.Select(i => i.Tabs[0].Url));
    }

    [Fact]
    public void StartPageTabsAreNotRemembered()
    {
        var list = new RecentlyClosedList();
        list.Push(Item(""), 10);
        Assert.Empty(list.Items);
        var mixed = Item("https://a/", ClosedItemKind.Group);
        mixed = mixed with { Tabs = [.. mixed.Tabs, new ClosedTabRecord("gecko", "", "", null)] };
        list.Push(mixed, 10);
        Assert.Single(list.Items[0].Tabs);
    }

    [Fact]
    public void RemoveByIdAndTitles()
    {
        var list = new RecentlyClosedList();
        var a = Item("https://a.example/path");
        list.Push(a, 10);
        list.Push(new ClosedItem { Kind = ClosedItemKind.Group, Tabs = [.. a.Tabs, .. a.Tabs], Group = new SessionGroup(Guid.NewGuid(), "Research") }, 10);
        Assert.Equal("Research (2 tabs)", list.Items[0].Title);
        Assert.Equal(a.Id, list.Remove(a.Id)!.Id);
        Assert.Null(list.Remove(a.Id));
        Assert.Equal("host.example", new ClosedTabRecord("gecko", "https://host.example/x", "", null).DisplayTitle);
    }

    [Fact]
    public void PopOnEmptyIsNull() => Assert.Null(new RecentlyClosedList().PopLatest());
}

public class DuplicateDetectionTests
{
    [Fact]
    public void EquivalentAddresses()
    {
        Assert.True(UrlIdentity.SamePage("https://www.Example.com/", "http://example.com"));
        Assert.True(UrlIdentity.SamePage("https://example.com/a/#section", "https://example.com/a"));
        Assert.True(UrlIdentity.SamePage("https://example.com:443/a", "https://example.com/a/"));
    }

    [Fact]
    public void DifferentAddresses()
    {
        Assert.False(UrlIdentity.SamePage("https://example.com/a?x=1", "https://example.com/a?x=2"));
        Assert.False(UrlIdentity.SamePage("https://example.com/a", "https://example.org/a"));
        Assert.False(UrlIdentity.SamePage("https://example.com:8080/", "https://example.com/"));
        Assert.False(UrlIdentity.SamePage("", ""));
        Assert.False(UrlIdentity.SamePage("about:blank", "about:blank"));
        Assert.False(UrlIdentity.SamePage("mosaic://history", "mosaic://history"));
    }

    [Fact]
    public void FirstMatchSkipsExcluded()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        (Guid, string)[] candidates = [(a, "https://example.com/"), (b, "https://www.example.com")];
        Assert.Equal(a, DuplicateFinder.FirstMatch("https://example.com", candidates));
        Assert.Equal(b, DuplicateFinder.FirstMatch("https://example.com", candidates, a));
        Assert.Null(DuplicateFinder.FirstMatch("https://nothing.example", candidates));
    }
}

public class TabSearchTests
{
    [Fact]
    public void MatchesAllTermsAndRanksTitlePrefixFirst()
    {
        (string Title, string Url)[] tabs =
        [
            ("Swift Forums", "https://forums.swift.org/"),
            ("Taylor Swift", "https://en.wikipedia.org/wiki/Taylor_Swift"),
            ("Docs", "https://developer.apple.com/swift/"),
            ("Mail", "https://mail.example.com/"),
        ];
        Assert.Equal(["Swift Forums", "Taylor Swift", "Docs"], TabSearch.Rank(tabs, "swift", t => t.Title, t => t.Url).Select(t => t.Title));
        Assert.Equal(["Taylor Swift"], TabSearch.Rank(tabs, "swift wiki", t => t.Title, t => t.Url).Select(t => t.Title));
        Assert.Equal(4, TabSearch.Rank(tabs, "", t => t.Title, t => t.Url).Count);
    }

    [Fact]
    public void IgnoresCaseAndAccents()
    {
        Assert.NotNull(TabSearch.Score("CAFE", "Le Café", "https://x/"));
        Assert.NotNull(TabSearch.Score("mail", "Inbox", "https://mail.example.com/"));
        Assert.Null(TabSearch.Score("zzz", "Inbox", "https://mail.example.com/"));
    }
}

public class SleepPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(10_000);

    private static SleepCandidate Tab(double minutesAgo, Func<SleepCandidate, SleepCandidate>? configure = null)
    {
        var candidate = new SleepCandidate(Guid.NewGuid(), Now - TimeSpan.FromMinutes(minutesAgo));
        return configure?.Invoke(candidate) ?? candidate;
    }

    [Fact]
    public void UnloadsOnlyTabsPastTheThresholdOldestFirst()
    {
        var settings = new SleepSettings { InactiveMinutes = 30 };
        SleepCandidate old = Tab(90), older = Tab(120), fresh = Tab(10);
        Assert.Equal([older.Id, old.Id], SleepPolicy.TabsToUnload([old, fresh, older], settings, MemoryPressure.Normal, Now));
    }

    [Fact]
    public void Exemptions()
    {
        var settings = new SleepSettings { InactiveMinutes = 5, NeverUnloadPinned = true };
        SleepCandidate[] tabs =
        [
            Tab(60, c => c with { IsSelected = true }),
            Tab(60, c => c with { IsAsleep = true }),
            Tab(60, c => c with { HasLoadedContent = false }),
            Tab(60, c => c with { IsPinned = true }),
            Tab(60, c => c with { IsPlayingAudio = true }),
            Tab(60, c => c with { HasActiveDownload = true }),
        ];
        Assert.Empty(SleepPolicy.TabsToUnload(tabs, settings, MemoryPressure.Critical, Now));
        Assert.Equal(SleepExemption.PlayingAudio, SleepPolicy.Exemption(tabs[4], settings));
        Assert.Equal(SleepExemption.Downloading, SleepPolicy.Exemption(tabs[5], settings));
        Assert.Equal([tabs[3].Id], SleepPolicy.TabsToUnload(tabs, settings with { NeverUnloadPinned = false }, MemoryPressure.Normal, Now));
    }

    [Fact]
    public void MemoryPressureShortensTheThreshold()
    {
        var settings = new SleepSettings { InactiveMinutes = 60 };
        SleepCandidate twentyMinutes = Tab(20), twoMinutes = Tab(2), justNow = Tab(0);
        SleepCandidate[] tabs = [twentyMinutes, twoMinutes, justNow];
        Assert.Empty(SleepPolicy.TabsToUnload(tabs, settings, MemoryPressure.Normal, Now));
        Assert.Equal([twentyMinutes.Id], SleepPolicy.TabsToUnload(tabs, settings, MemoryPressure.Warning, Now));
        Assert.Equal(3, SleepPolicy.TabsToUnload(tabs, settings, MemoryPressure.Critical, Now).Count);
    }

    [Fact]
    public void Switches()
    {
        var off = new SleepSettings { UnloadInactiveTabs = false, RespondToMemoryPressure = true };
        Assert.Null(SleepPolicy.Threshold(off, MemoryPressure.Normal));
        Assert.Equal(TimeSpan.Zero, SleepPolicy.Threshold(off, MemoryPressure.Critical));
        var ignorePressure = new SleepSettings { UnloadInactiveTabs = false, RespondToMemoryPressure = false };
        Assert.Null(SleepPolicy.Threshold(ignorePressure, MemoryPressure.Critical));
        Assert.Equal(TimeSpan.FromSeconds(60), SleepPolicy.Threshold(new SleepSettings { InactiveMinutes = 2 }, MemoryPressure.Warning));
    }

    [Fact]
    public void WindowsMemoryThresholds()
    {
        Assert.Equal(MemoryPressure.Normal, MemoryPressureMonitor.Classify(60, 100, 50));
        Assert.Equal(MemoryPressure.Warning, MemoryPressureMonitor.Classify(86, 100, 50));
        Assert.Equal(MemoryPressure.Warning, MemoryPressureMonitor.Classify(60, 100, 5));
        Assert.Equal(MemoryPressure.Critical, MemoryPressureMonitor.Classify(97, 100, 50));
        // Off Windows the monitor reads normal and never throws.
        using var monitor = new MemoryPressureMonitor();
        var reading = monitor.Read();
        if (!OperatingSystem.IsWindows()) Assert.Equal(MemoryPressure.Normal, reading);
    }
}

public class TabSettingsTests
{
    [Fact]
    public void DefaultsAndRoundTrip()
    {
        var settings = new TabSettings();
        Assert.Equal(TabStartup.PreviousSession, settings.Startup);
        settings = settings with { Startup = TabStartup.StartPage, Sleep = settings.Sleep with { InactiveMinutes = 15 } };
        Assert.Equal(settings, TabSettings.Decode(settings.Encode()));
    }

    [Fact]
    public void TolerantDecoding()
    {
        Assert.Equal(new TabSettings(), TabSettings.Decode(null));
        Assert.Equal(new TabSettings(), TabSettings.Decode("garbage"));
        var partial = TabSettings.Decode("""{"startup":"startPage","recentlyClosedLimit":100000,"sleep":{"inactiveMinutes":0}}""");
        Assert.Equal(TabStartup.StartPage, partial.Startup);
        Assert.Equal(TabSettings.RecentlyClosedRange.Max, partial.RecentlyClosedLimit);
        Assert.Equal(1, partial.Sleep.InactiveMinutes);
        Assert.True(partial.Sleep.UnloadInactiveTabs);
        Assert.True(partial.ProtectPinnedTabs);
        Assert.Equal(TabStartup.PreviousSession, TabSettings.Decode("""{"startup":"somethingNew"}""").Startup);
    }

    [Fact]
    public void AssignmentsUseTheMacOSKeys()
    {
        var settings = new TabSettings();
        Assert.Equal(TabStartup.StartPage, settings.With("startup", "startPage")!.Startup);
        Assert.Equal(15, settings.With("inactiveMinutes", "15")!.Sleep.InactiveMinutes);
        Assert.False(settings.With("respondToMemoryPressure", "false")!.Sleep.RespondToMemoryPressure);
        Assert.Null(settings.With("nonsense", "1"));
        Assert.Null(settings.With("startup", "never"));
    }

    [Fact]
    public void StoreSurvivesDamage()
    {
        var path = Path.Combine(Path.GetTempPath(), "mosaic-tabsettings-" + Guid.NewGuid().ToString("N"), "tab-settings.json");
        try
        {
            var store = new TabSettingsStore(path);
            Assert.Equal(new TabSettings(), store.Load());
            store.Save(new TabSettings { OfferSwitchToOpenTab = false });
            Assert.False(store.Load().OfferSwitchToOpenTab);
            File.WriteAllText(path, "{not json");
            Assert.Equal(new TabSettings(), store.Load());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
