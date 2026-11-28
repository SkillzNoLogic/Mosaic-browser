using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using Mosaic.Core.Tabs;
using Mosaic.Core.Tests.Browser;
using static Mosaic.Core.Tests.Browser.Snapshots;

namespace Mosaic.Core.Tests.Tabs;

public class TabsOrganizationTests
{
    [Fact]
    public void WorkspacesKeepTheirOwnTabsAndSelection() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var first = m.Tabs[0];
        var personal = m.SelectedWorkspace;
        Assert.Equal(personal.Id, first.WorkspaceId);
        Assert.Equal([first], m.VisibleTabs);

        var work = m.CreateWorkspace("Work");
        var workTab = m.SelectedTab!;
        Assert.Same(work, m.SelectedWorkspace);
        Assert.Equal(work.Id, workTab.WorkspaceId);
        Assert.Equal(2, m.Tabs.Count);
        Assert.Equal([workTab], m.VisibleTabs);

        var second = m.NewTab("https://b.example/");
        m.Select(workTab);
        m.SelectWorkspace(personal.Id);
        Assert.Same(first, m.SelectedTab);
        Assert.Equal([first], m.VisibleTabs);
        m.SelectWorkspace(work.Id);
        Assert.Same(workTab, m.SelectedTab);
        Assert.Equal([workTab, second], m.VisibleTabs);

        m.SelectAdjacentWorkspace(1);
        Assert.Same(personal, m.SelectedWorkspace);
        m.RenameWorkspace(work.Id, "  Job ");
        Assert.Equal("Job", work.Name);
        m.RenameWorkspace(work.Id, "   ");
        Assert.Equal("Job", work.Name);
    });

    [Fact]
    public void ClosingStaysInTheWorkspace() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var b = m.NewTab("https://b.example/");
        m.CreateWorkspace("Other");
        var other = m.SelectedTab!;
        m.Select(b);

        m.Close(b);
        Assert.Same(a, m.SelectedTab);

        // The workspace's last tab: a new tab opens there rather than jumping to another workspace.
        m.Close(a);
        Assert.Equal(m.Workspaces[0].Id, m.SelectedWorkspaceId);
        Assert.True(m.SelectedTab!.IsStartPage);
        Assert.NotSame(other, m.SelectedTab);
        Assert.Contains(other, m.Tabs);
    });

    [Fact]
    public void AdjacentTabsCycleWithinTheWorkspace() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var b = m.NewTab("https://b.example/");
        m.CreateWorkspace("Other");
        m.Select(b);
        m.SelectAdjacentTab(1);
        Assert.Same(a, m.SelectedTab);
        m.SelectAdjacentTab(-1);
        Assert.Same(b, m.SelectedTab);
    });

    [Fact]
    public void PinnedTabsGoFirstAndNeedASecondCtrlW() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var b = m.NewTab("https://b.example/");
        var c = m.NewTab("https://c.example/");

        m.SetPinned(true, c);
        Assert.Equal([c, a, b], m.Tabs);
        m.SetPinned(true, b);
        Assert.Equal([c, b, a], m.Tabs);
        Assert.Equal([c, b], m.PinnedTabs);
        m.SetPinned(false, c);
        Assert.Equal([b, c, a], m.Tabs);

        m.Select(b);
        m.CloseSelectedTab();
        Assert.Contains(b, m.Tabs);
        Assert.Contains("pinned", m.Notice);
        m.CloseSelectedTab();
        Assert.DoesNotContain(b, m.Tabs);

        // After three seconds the first press counts as a first press again.
        m.SetPinned(true, c);
        m.Select(c);
        m.CloseSelectedTab();
        await f.Time.AdvanceAsync(TimeSpan.FromSeconds(4));
        m.CloseSelectedTab();
        Assert.Contains(c, m.Tabs);

        m.TabSettings = m.TabSettings with { ProtectPinnedTabs = false };
        m.Select(c);
        m.CloseSelectedTab();
        Assert.DoesNotContain(c, m.Tabs);
    });

    [Fact]
    public void MuteGoesToGeckoAndFollowsTheTabToANewGeckoTab() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var tab = f.OpenGecko("https://music.example/");
        m.SetMuted(true, tab);
        Assert.Equal([tab.GeckoId!.Value, (object)true], f.Gecko.CallsTo("setMuted").Last());

        m.SetEngine(EngineKind.Chromium);
        m.SetEngine(EngineKind.Gecko);
        Assert.True(tab.IsMuted);
        Assert.Equal([tab.GeckoId!.Value, (object)true], f.Gecko.CallsTo("setMuted").Last());

        m.ToggleMute(tab);
        Assert.False(tab.IsMuted);
        Assert.Equal([tab.GeckoId!.Value, (object)false], f.Gecko.CallsTo("setMuted").Last());
    });

    [Fact]
    public void MuteAndAudioGoThroughChromiumWhenTheBrowserCanDoIt() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture(audioChromium: true);
        var m = f.Model;
        var tab = f.OpenChromium("https://video.example/");
        var browser = f.AudioChromium!.Created.Single();
        Assert.False(browser.IsMuted);

        browser.Play(true);
        Assert.True(tab.IsPlayingAudio);
        Assert.True(tab.ShowsAudioIndicator);
        m.SetMuted(true, tab);
        Assert.True(browser.IsMuted);

        // A new browser for the same tab (the tab slept and woke up) is muted from the start.
        m.SetEngine(EngineKind.Gecko);
        m.SetEngine(EngineKind.Chromium);
        Assert.True(f.AudioChromium.Created.Last().IsMuted);
        Assert.False(tab.IsPlayingAudio);
    });

    [Fact]
    public void DuplicateOpensTheSamePageNextToIt() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var b = f.OpenGecko("https://b.example/");
        m.SetMuted(true, b);
        m.Select(a);

        var copy = m.Duplicate(b);
        Assert.Equal([a, b, copy], m.Tabs);
        Assert.Same(copy, m.SelectedTab);
        Assert.Equal("https://b.example/", copy.Url);
        Assert.True(copy.IsMuted);
        Assert.Equal(EngineKind.Gecko, copy.Engine);
        Assert.NotNull(copy.GeckoId);
        Assert.NotEqual(b.GeckoId, copy.GeckoId);

        m.SetPinned(true, b);
        var pinnedCopy = m.Duplicate(b, select: false);
        Assert.True(pinnedCopy.IsPinned);
        Assert.Equal(1, m.Tabs.IndexOf(pinnedCopy));
    });

    [Fact]
    public void LinksOpenNextToTheirOpenerInItsGroup() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var opener = f.OpenChromium("https://opener.example/");
        var last = m.NewTab("https://last.example/");
        var group = m.CreateGroup([opener], "Research")!;
        m.Select(opener);

        f.Chromium.Created[0].RaiseNewTab("https://linked.example/");
        var linked = m.Tabs.Single(t => t.Url == "https://linked.example/");
        Assert.Equal(m.Tabs.IndexOf(opener) + 1, m.Tabs.IndexOf(linked));
        Assert.Equal(group.Id, linked.GroupId);
        Assert.Same(linked, m.SelectedTab);

        // A plain new tab from a grouped tab goes after the group, ungrouped.
        m.Select(opener);
        var plain = m.NewTab();
        Assert.Null(plain.GroupId);
        Assert.Equal([a, opener, linked, plain, last], m.Tabs);
    });

    [Fact]
    public void GroupsCollapseRenameRecolourAndDissolve() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var b = m.NewTab("https://b.example/");
        var c = m.NewTab("https://c.example/");
        var d = m.NewTab("https://d.example/");

        var group = m.CreateGroup([b, d], " Reading ")!;
        Assert.Equal("Reading", group.Name);
        Assert.Equal(TabGroupColor.Grey, group.Color);
        Assert.Equal([a, b, d, c], m.Tabs);
        Assert.Equal([TabStripEntryKind.Tab, TabStripEntryKind.GroupHeader, TabStripEntryKind.Tab, TabStripEntryKind.Tab, TabStripEntryKind.Tab], m.TabStrip().Select(e => e.Kind));

        // Collapsing the selected tab's group moves the selection out of it.
        m.Select(d);
        m.SetGroupCollapsed(group.Id, true);
        Assert.Same(c, m.SelectedTab);
        Assert.Equal([a, c], m.NavigableTabs);
        Assert.True(m.TabStrip().Single(e => e.Tab == b).IsHidden);

        // Selecting a member expands it again.
        m.Select(b);
        Assert.False(group.IsCollapsed);

        m.RenameGroup(group.Id, "Later");
        m.SetGroupColor(group.Id, TabGroupColor.Purple);
        Assert.Equal(("Later", TabGroupColor.Purple), (group.Name, group.Color));

        m.AddTabToGroup(a, group.Id);
        Assert.Equal([b, d, a, c], m.Tabs);
        m.RemoveTabFromGroup(b);
        Assert.Equal([d, a, b, c], m.Tabs);
        Assert.Null(b.GroupId);

        m.Ungroup(group.Id);
        Assert.Empty(m.Groups);
        Assert.All(m.Tabs, t => Assert.Null(t.GroupId));
    });

    [Fact]
    public void DragAndDropMovesTabsAndGroups() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var b = m.NewTab("https://b.example/");
        var c = m.NewTab("https://c.example/");
        var d = m.NewTab("https://d.example/");

        m.MoveTab(d, 0);
        Assert.Equal([d, a, b, c], m.Tabs);
        m.MoveTabBefore(d, null);
        Assert.Equal([a, b, c, d], m.Tabs);
        m.MoveTabBefore(a, d);
        Assert.Equal([b, c, a, d], m.Tabs);
        Assert.Equal(m.Tabs, m.VisibleTabs);

        var group = m.CreateGroup([b, c])!;
        m.MoveTabBefore(d, c);
        Assert.Equal(group.Id, d.GroupId);
        m.MoveGroupBefore(group.Id, null);
        Assert.Equal([a, b, d, c], m.Tabs);
        m.MoveGroup(group.Id, 0);
        Assert.Equal([b, d, c, a], m.Tabs);
    });

    [Fact]
    public void MovingTheSelectedTabToAnotherWorkspaceSelectsItsNeighbour() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var b = m.NewTab("https://b.example/");
        var other = m.CreateWorkspace("Other", switchTo: false);

        m.MoveTabToWorkspace(b, other.Id);
        Assert.Same(a, m.SelectedTab);
        Assert.Equal([a], m.VisibleTabs);
        Assert.Equal([b], m.TabsIn(other.Id));
        m.SelectWorkspace(other.Id);
        Assert.Same(b, m.SelectedTab);
    });

    [Fact]
    public void ClosedTabsComeBackWhereTheyWere() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var b = f.OpenChromium("https://b.example/");
        var c = m.NewTab("https://c.example/");
        m.SetMuted(true, b);

        m.Close(b);
        var item = Assert.Single(m.RecentlyClosedItems);
        Assert.Equal("https://b.example/", item.Tabs[0].Url);
        // Start pages are not remembered.
        var blank = m.NewTab(select: false);
        m.Close(blank);
        Assert.Single(m.RecentlyClosedItems);

        Assert.True(m.ReopenClosedTab());
        var back = m.SelectedTab!;
        Assert.Equal("https://b.example/", back.Url);
        Assert.Equal(EngineKind.Chromium, back.Engine);
        Assert.True(back.IsMuted);
        Assert.False(back.IsAsleep);
        Assert.NotNull(back.Chromium);
        Assert.Equal([a, back, c], m.Tabs);

        Assert.False(m.ReopenClosedTab());
        Assert.Equal("No recently closed tabs.", m.Notice);
    });

    [Fact]
    public void AClosedGroupComesBackAsAGroup() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var b = m.NewTab("https://b.example/");
        var c = m.NewTab("https://c.example/");
        var group = m.CreateGroup([b, c], "Trip", TabGroupColor.Green)!;

        m.CloseGroup(group.Id);
        Assert.Equal([a], m.Tabs);
        Assert.Empty(m.Groups);
        var item = Assert.Single(m.RecentlyClosedItems);
        Assert.Equal(ClosedItemKind.Group, item.Kind);
        Assert.Equal("Trip (2 tabs)", item.Title);

        m.ReopenClosedTab();
        var restored = Assert.Single(m.Groups);
        Assert.Equal((group.Id, "Trip", TabGroupColor.Green), (restored.Id, restored.Name, restored.Color));
        Assert.Equal(["", "https://b.example/", "https://c.example/"], m.Tabs.Select(t => t.Url));
        Assert.All(m.Tabs.Skip(1), t => Assert.Equal(group.Id, t.GroupId));
        // The first tab is selected and loads; the second stays asleep until selected.
        Assert.Same(m.Tabs[1], m.SelectedTab);
        Assert.True(m.Tabs[2].IsAsleep);
    });

    [Fact]
    public void ADeletedWorkspaceComesBackWithItsTabs() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var personal = m.SelectedWorkspace;
        var work = m.CreateWorkspace("Work");
        m.Navigate("https://work.example/");
        m.NewTab("https://mail.example/");
        Assert.True(m.WorkspaceDeletionNeedsConfirmation(work.Id));

        m.DeleteWorkspace(work.Id);
        Assert.Equal([personal], m.Workspaces);
        Assert.Same(personal, m.SelectedWorkspace);
        Assert.Single(m.Tabs);
        Assert.Equal(ClosedItemKind.Workspace, m.RecentlyClosedItems[0].Kind);
        Assert.Equal("Work (2 tabs)", m.RecentlyClosedItems[0].Title);

        // The last workspace cannot be deleted.
        m.DeleteWorkspace(personal.Id);
        Assert.Single(m.Workspaces);

        m.ReopenClosedTab();
        Assert.Equal(["Personal", "Work"], m.Workspaces.Select(w => w.Name));
        Assert.Equal(work.Id, m.SelectedWorkspaceId);
        Assert.Equal(["https://work.example/", "https://mail.example/"], m.VisibleTabs.Select(t => t.Url));
    });

    [Fact]
    public void PrivateTabsLeaveNoTrace() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        await f.StartAsync();
        var secret = m.NewTab("https://secret.example/");
        m.IsPrivateTab = tab => ReferenceEquals(tab, secret);

        Assert.DoesNotContain(m.MakeSessionSnapshot().Tabs, t => t.Url == "https://secret.example/");
        m.Close(secret);
        Assert.Empty(m.RecentlyClosedItems);
        await m.SaveSession();
        Assert.DoesNotContain("secret", File.ReadAllText(f.Store.SessionPath));
    });

    [Fact]
    public void AnAddressAlreadyOpenOffersToSwitch() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        m.Navigate("https://www.example.com/page");
        m.CreateWorkspace("Other");
        var fresh = m.SelectedTab!;

        m.Navigate("example.com/page/");
        var offer = m.DuplicateOffer!;
        Assert.True(offer.ClosesNewTab);
        Assert.Equal(a.Id, offer.ExistingTabId);
        Assert.Equal("Personal", offer.WorkspaceName);
        Assert.Contains("already open in Personal", offer.Message);

        m.AcceptDuplicateOffer();
        Assert.Same(a, m.SelectedTab);
        Assert.DoesNotContain(fresh, m.Tabs);
        Assert.Empty(m.RecentlyClosedItems);

        // The offer goes away by itself after ten seconds.
        m.NewTab();
        m.Navigate("https://www.example.com/page");
        Assert.NotNull(m.DuplicateOffer);
        await f.Time.AdvanceAsync(TimeSpan.FromSeconds(11));
        Assert.Null(m.DuplicateOffer);

        m.TabSettings = m.TabSettings with { OfferSwitchToOpenTab = false };
        m.NewTab();
        m.Navigate("https://www.example.com/page");
        Assert.Null(m.DuplicateOffer);
    });

    [Fact]
    public void TabSearchFindsTabsOfEveryWorkspaceCurrentFirst() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        m.Navigate("https://docs.example/swift");
        m.CreateWorkspace("Other");
        m.Navigate("https://swift.org/");

        Assert.Equal(["swift.org", "docs.example"], m.TabSearchResults("swift").Select(t => t.DisplayTitle));
        Assert.Equal(2, m.TabSearchResults("").Count);
        m.Close(m.SelectedTab!);
        Assert.Single(m.ClosedSearchResults("swift"));
        Assert.Empty(m.ClosedSearchResults("nothing"));
    });

    [Fact]
    public void ShortcutsPressedInGeckoArriveAsTabsEvents() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        var b = f.OpenGecko("https://b.example/");
        var c = f.OpenGecko("https://c.example/");
        m.Close(c);

        GeckoTabState[] Live() => [.. m.Tabs.Where(t => t.GeckoId is not null).Select(t => Tab(t.GeckoId!.Value, t.Url))];
        f.Gecko.ReportEvents(Live(), "tabs.reopenClosed");
        Assert.Equal("https://c.example/", m.SelectedTab!.Url);

        f.Gecko.ReportEvents(Live(), "tabs.select1");
        Assert.Same(a, m.SelectedTab);
        f.Gecko.ReportEvents(Live(), "tabs.select9");
        Assert.Equal("https://c.example/", m.SelectedTab!.Url);
        f.Gecko.ReportEvents(Live(), "tabs.search");
        Assert.True(m.IsTabSearchVisible);
    });
}

public class TabsSessionTests
{
    [Fact]
    public void TheSessionComesBackOnTheNextLaunchWithOnlyTheSelectedTabLoaded() => TestLoop.Run(async () =>
    {
        string profile;
        Guid workId;
        using var first = new TabsFixture();
        {
            var m = first.Model;
            await first.StartAsync();
            Assert.Equal(PreviousRun.FirstRun, m.PreviousRun);
            var blank = m.Tabs[0];
            var mozilla = first.OpenGecko("https://mozilla.org/");
            m.Close(blank);
            m.SetPinned(true, mozilla);
            var chrome = first.OpenChromium("https://chromium.org/");
            m.SetMuted(true, chrome);
            m.CreateGroup([chrome], "Engines", TabGroupColor.Orange);
            var work = m.CreateWorkspace("Work", "briefcase");
            workId = work.Id;
            m.Navigate("https://work.example/");
            m.NewTab("https://mail.example/");
            m.SelectWorkspace(m.Workspaces[0].Id);
            m.Select(chrome);
            first.OpenChromium("https://closed.example/");
            m.CloseSelectedTab();
            m.Shutdown();
            profile = first.SessionDirectory;
        }
        Assert.True(File.Exists(first.Store.SessionPath));
        Assert.False(File.Exists(Path.Combine(first.Store.Directory, "running")));

        using var second = new TabsFixture(profile);
        var model = second.Model;
        await second.StartAsync();
        Assert.Equal(PreviousRun.Clean, model.PreviousRun);
        Assert.Equal(["Personal", "Work"], model.Workspaces.Select(w => w.Name));
        Assert.Equal("briefcase", model.Workspaces[1].Symbol);
        Assert.Equal(workId, model.Workspaces[1].Id);
        // The blank tab the window opened with is gone; the session's tabs are back in order.
        Assert.Equal(["https://mozilla.org/", "https://chromium.org/"], model.VisibleTabs.Select(t => t.Url));
        var pinned = model.VisibleTabs[0];
        var muted = model.VisibleTabs[1];
        Assert.True(pinned.IsPinned);
        Assert.True(muted.IsMuted);
        Assert.Equal("Engines", model.GroupOf(muted)!.Name);
        Assert.Same(muted, model.SelectedTab);
        Assert.Equal(["https://work.example/", "https://mail.example/"], model.TabsIn(workId).Select(t => t.Url));
        Assert.Equal("https://closed.example/", Assert.Single(model.RecentlyClosedItems).Tabs[0].Url);

        // Only the selected tab loaded; the others sleep until selected.
        Assert.Single(second.Chromium.Created);
        Assert.Empty(second.Gecko.CallsTo("openTab"));
        Assert.True(pinned.IsAsleep);
        Assert.False(muted.IsAsleep);
        Assert.Equal(3, model.AsleepTabCount);

        model.Select(pinned);
        Assert.False(pinned.IsAsleep);
        Assert.Single(second.Gecko.CallsTo("openTab"));
        Assert.Equal("https://mozilla.org/", second.Gecko.CallsTo("openTab")[0][0]);

        // Each workspace kept its selected tab.
        model.SelectWorkspace(workId);
        Assert.Equal("https://mail.example/", model.SelectedTab!.Url);
    });

    [Fact]
    public void ACrashIsDetectedAndTheTabsAreRestored() => TestLoop.Run(async () =>
    {
        using var first = new TabsFixture();
        await first.StartAsync();
        first.Model.Navigate("https://unsaved.example/");
        await first.Model.SaveSession();
        // No Shutdown: the process was killed.

        using var second = new TabsFixture(first.SessionDirectory);
        await second.StartAsync();
        Assert.Equal(PreviousRun.Crashed, second.Model.PreviousRun);
        Assert.Equal("https://unsaved.example/", second.Model.SelectedTab!.Url);
        Assert.Equal("Mosaic did not quit properly last time. Your tabs were restored.", second.Model.Notice);
    });

    [Fact]
    public void WithTheStartPageSettingPinnedTabsStayAndTheRestCanBeReopened() => TestLoop.Run(async () =>
    {
        using var first = new TabsFixture();
        await first.StartAsync();
        var m = first.Model;
        m.TabSettings = m.TabSettings with { Startup = TabStartup.StartPage };
        var blank = m.Tabs[0];
        var pinned = first.OpenGecko("https://pinned.example/");
        m.SetPinned(true, pinned);
        first.OpenGecko("https://other.example/");
        m.Close(blank);
        m.Shutdown();
        // The settings are written on the thread pool.
        for (var i = 0; i < 100 && first.SettingsStore.Load().Startup != TabStartup.StartPage; i++) await Task.Delay(10);

        using var second = new TabsFixture(first.SessionDirectory);
        await second.StartAsync();
        var model = second.Model;
        Assert.Equal(TabStartup.StartPage, model.TabSettings.Startup);
        Assert.Equal(["https://pinned.example/", ""], model.VisibleTabs.Select(t => t.Url));
        Assert.True(model.SelectedTab!.IsStartPage);
        Assert.True(model.VisibleTabs[0].IsAsleep);
        Assert.Equal(1, model.PreviousSessionPageCount);
        Assert.False(model.OffersCrashRecovery);
        for (var i = 0; i < 100 && !File.Exists(second.Store.PreviousSessionPath); i++) await Task.Delay(10);
        Assert.True(File.Exists(second.Store.PreviousSessionPath));

        model.RestorePreviousSession();
        // The pinned tab is not doubled; the blank tab made way.
        Assert.Equal(["https://pinned.example/", "https://other.example/"], model.VisibleTabs.Select(t => t.Url));
        Assert.Null(model.PreviousSession);
    });

    [Fact]
    public void AfterACrashWithTheStartPageSettingABannerOffersTheTabs() => TestLoop.Run(async () =>
    {
        using var first = new TabsFixture();
        await first.StartAsync();
        first.Model.TabSettings = first.Model.TabSettings with { Startup = TabStartup.StartPage };
        first.Model.Navigate("https://lost.example/");
        await first.Model.SaveSession();
        for (var i = 0; i < 100 && first.SettingsStore.Load().Startup != TabStartup.StartPage; i++) await Task.Delay(10);

        using var second = new TabsFixture(first.SessionDirectory);
        await second.StartAsync();
        Assert.True(second.Model.OffersCrashRecovery);
        Assert.Equal(1, second.Model.PreviousSessionPageCount);
        second.Model.RestorePreviousSession();
        Assert.False(second.Model.OffersCrashRecovery);
        Assert.Equal("https://lost.example/", second.Model.SelectedTab!.Url);
        Assert.Single(second.Model.Tabs);
    });

    [Fact]
    public void AnUnreadableSessionIsSetAsideNotOverwritten() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        Directory.CreateDirectory(f.Store.Directory);
        File.WriteAllText(f.Store.SessionPath, """{"version": 42}""");
        await f.StartAsync();
        Assert.StartsWith("The saved session could not be read; it was kept as session-unreadable-", f.Model.Notice);
        Assert.Single(Directory.GetFiles(f.Store.Directory, "session-unreadable-*.json"));
        Assert.Single(f.Model.Tabs);
    });

    [Fact]
    public void ChangesAreSavedAfterASecondAndUnchangedSessionsAreNotRewritten() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        await f.StartAsync();
        await f.Model.SaveSession();
        var written = File.GetLastWriteTimeUtc(f.Store.SessionPath);
        File.SetLastWriteTimeUtc(f.Store.SessionPath, written.AddMinutes(-5));
        await f.Model.SaveSession();
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(f.Store.SessionPath));

        f.Model.CreateWorkspace("Later");
        await f.Time.AdvanceAsync(TimeSpan.FromSeconds(1.5));
        await f.Model.SaveSession();
        Assert.Contains("Later", File.ReadAllText(f.Store.SessionPath));
    });
}

public class SleepingTabsTests
{
    [Fact]
    public void InactiveTabsSleepAndWakeWhenSelected() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        await f.StartAsync();
        var chrome = f.OpenChromium("https://chromium.org/", select: false);
        var gecko = f.OpenGecko("https://mozilla.org/", select: false);
        var browser = f.Chromium.Created.Single();
        await f.Time.AdvanceAsync(TimeSpan.FromMinutes(61));

        var unloaded = await m.RunSleepCheckAsync();
        Assert.Equal(2, unloaded.Count);
        Assert.True(browser.IsClosed);
        Assert.Null(chrome.Chromium);
        Assert.True(chrome.IsAsleep);
        Assert.True(gecko.IsAsleep);
        Assert.Equal([gecko.GeckoId!.Value, (object)false], f.Gecko.CallsTo("discardTab").Single());
        Assert.False(m.SelectedTab!.IsAsleep);

        m.Select(chrome);
        Assert.False(chrome.IsAsleep);
        Assert.Equal(2, f.Chromium.Created.Count);
        Assert.Equal("https://chromium.org/", f.Chromium.Created[1].Loads[0]);

        // A discarded Gecko tab is restored by Gecko itself when selected.
        var opens = f.Gecko.CallsTo("openTab").Count;
        m.Select(gecko);
        Assert.False(gecko.IsAsleep);
        Assert.Equal(opens, f.Gecko.CallsTo("openTab").Count);
        Assert.Equal(gecko.GeckoId!.Value, f.Gecko.CallsTo("selectTab").Last()[0]);
    });

    [Fact]
    public void ExemptTabsStayAwake() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        await f.StartAsync();
        var downloading = f.OpenChromium("https://files.example/", select: false);
        var pinned = f.OpenChromium("https://pinned.example/", select: false);
        m.SetPinned(true, pinned);
        var music = f.OpenGecko("https://music.example/", select: false);
        var start = m.NewTab(select: false);
        m.SetDownloadActive(true, downloading.Id);
        f.Gecko.Handlers["tabActivity"] = _ => new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["id"] = music.GeckoId!.Value, ["audible"] = true, ["discarded"] = false });

        var unloaded = await m.RunSleepCheckAsync(MemoryPressure.Critical);
        Assert.Empty(unloaded);
        Assert.True(music.IsPlayingAudio);
        Assert.False(start.IsAsleep);

        m.SetDownloadActive(false, downloading.Id);
        Assert.Equal([downloading.Id], await m.RunSleepCheckAsync(MemoryPressure.Critical));
    });

    [Fact]
    public void WindowsMemoryPressureUnloadsSooner() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        await f.StartAsync();
        var background = f.OpenChromium("https://background.example/", select: false);

        f.Pressure.Level = MemoryPressure.Critical;
        await f.Time.AdvanceAsync(TimeSpan.FromSeconds(10));
        await TestLoop.Settle();
        Assert.Equal(MemoryPressure.Critical, m.MemoryPressure);
        Assert.True(background.IsAsleep);
        Assert.NotNull(m.LastSleepCheck);
    });

    [Fact]
    public void ManualUnloadingAndItsLimits() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        await f.StartAsync();
        var only = m.Tabs[0];
        await m.UnloadTabAsync(only);
        Assert.Equal("The only tab of a workspace can’t be unloaded.", m.Notice);

        var page = f.OpenChromium("https://page.example/");
        Assert.True(await m.UnloadAsync(page));
        Assert.Same(only, m.SelectedTab);
        await m.UnloadTabAsync(page);
        Assert.Equal("This tab is already unloaded.", m.Notice);

        m.Select(page);
        f.OpenChromium("https://other.example/", select: false);
        Assert.Equal(1, await m.UnloadBackgroundTabsAsync());
        Assert.Equal("No tabs to unload.", (await m.UnloadBackgroundTabsAsync(), m.Notice).Notice);
    });

    [Fact]
    public void WhenGeckoCannotDiscardTheTabIsClosedAndReopenedLater() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        await f.StartAsync();
        var tab = f.OpenGecko("https://old-agent.example/", select: false);
        var id = tab.GeckoId!.Value;
        f.Gecko.Handlers["discardTab"] = _ => throw new InvalidOperationException("Unknown method discardTab");

        Assert.True(await m.UnloadAsync(tab));
        Assert.True(tab.IsAsleep);
        Assert.Null(tab.GeckoId);
        Assert.Contains(f.Gecko.CallsTo("closeTab"), args => (int)args[0]! == id);

        // A snapshot without it does not close the Mosaic tab.
        f.Gecko.Report();
        Assert.Contains(tab, m.Tabs);
        m.Select(tab);
        Assert.NotNull(tab.GeckoId);
        Assert.NotEqual(id, tab.GeckoId);
    });

    [Fact]
    public void ADiscardedGeckoTabIgnoresItsPlaceholderState() => TestLoop.Run(async () =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        await f.StartAsync();
        var tab = f.OpenGecko("https://keep.example/", select: false);
        f.Gecko.Report(Tab(tab.GeckoId!.Value, "https://keep.example/", "Keep"));
        await m.UnloadAsync(tab);

        f.Gecko.Report(Tab(tab.GeckoId!.Value, "about:blank", ""));
        Assert.Equal("https://keep.example/", tab.Url);
        Assert.Equal("Keep", tab.Title);
    });
}

public class TabsAutomationTests
{
    [Fact]
    public void CommandsAndDumpUseTheMacOSNames() => TestLoop.Run(async () =>
    {
        // Through the handler directly: registering in AutomationRegistry is process-wide and
        // would add "tabsArea" to every other test's dump.
        using var f = new TabsFixture();
        var m = f.Model;
        await f.StartAsync();
        var automation = new Automation(m, _ => Task.CompletedTask);
        async Task<System.Text.Json.Nodes.JsonObject> Run(string command, string value = "")
        {
            if (command == "dump")
            {
                var state = automation.DumpState();
                state["tabsArea"] = TabsAutomation.State(m);
                return new System.Text.Json.Nodes.JsonObject { ["ok"] = true, ["state"] = state };
            }
            var reply = await TabsAutomation.HandleAsync(m, command, value, null)
                ?? await automation.HandleAsync(new System.Text.Json.Nodes.JsonObject { ["do"] = command, ["value"] = value }.ToJsonString());
            return System.Text.Json.Nodes.JsonNode.Parse(reply.Json)!.AsObject();
        }

        await Run("navigate", "https://one.example/");
        await Run("newTab", "https://two.example/");
        Assert.True((bool)(await Run("pin", "1"))["ok"]!);
        Assert.True(m.Tabs[0].IsPinned);
        await Run("selectVisible", "1");
        await Run("group", "Reading|purple");
        await Run("newWorkspace", "Work");
        await Run("selectWorkspace", "0");
        await Run("mute", "0");
        Assert.False((bool)(await Run("pin", "7"))["ok"]!);
        Assert.False((bool)(await Run("tabSetting", "nonsense=1"))["ok"]!);
        await Run("tabSetting", "inactiveMinutes=15");
        await Run("tabSearchQuery", "two");

        var dump = (await Run("dump"))["state"]!.AsObject();
        var area = dump["tabsArea"]!.AsObject();
        Assert.Equal("Personal", area["selectedWorkspace"]!.GetValue<string>());
        Assert.Equal(2, area["workspaces"]!.AsArray().Count);
        Assert.Equal(15, area["settings"]!["sleep"]!["inactiveMinutes"]!.GetValue<int>());
        Assert.Equal(["two.example"], area["tabSearchResults"]!.AsArray().Select(n => n!.GetValue<string>()));
        var tabs = area["tabs"]!.AsArray();
        Assert.Equal(dump["tabs"]!.AsArray().Count, tabs.Count);
        Assert.True(tabs[0]!["pinned"]!.GetValue<bool>());
        Assert.True(tabs[0]!["muted"]!.GetValue<bool>());
        Assert.Equal("Reading", area["groups"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("purple", area["groups"]![0]!["color"]!.GetValue<string>());
        Assert.Equal("firstRun", area["previousRun"]!.GetValue<string>());

        await Run("unload", "0");
        Assert.True(m.VisibleTabs[0].IsAsleep);
        await Run("forceCloseTab", "1");
        await Run("reopenClosed");
        Assert.Equal(3, m.Tabs.Count);
        await Run("saveSession");
        Assert.True(File.Exists(f.Store.SessionPath));
    });
}

public class TabsShortcutsTests
{
    [Fact]
    public void KeysMatchTheMacOSShortcutsWithCtrl()
    {
        Assert.Equal("tabs.reopenClosed", TabsShortcuts.Match('T', ShortcutModifiers.Control | ShortcutModifiers.Shift));
        Assert.Equal("tabs.search", TabsShortcuts.Match('A', ShortcutModifiers.Control | ShortcutModifiers.Shift));
        Assert.Equal("tabs.select3", TabsShortcuts.Match('3', ShortcutModifiers.Control));
        Assert.Equal("workspaces.next", TabsShortcuts.Match(0x27, ShortcutModifiers.Control | ShortcutModifiers.Alt));
        // Ctrl+T stays the new-tab shortcut; AltGr (Ctrl+Alt) never triggers a Ctrl shortcut.
        Assert.Null(TabsShortcuts.Match('T', ShortcutModifiers.Control));
        Assert.Null(TabsShortcuts.Match('3', ShortcutModifiers.Control | ShortcutModifiers.Alt));
    }

    [Fact]
    public void HandlingRunsTheCommand() => TestLoop.Run(() =>
    {
        using var f = new TabsFixture();
        var m = f.Model;
        var a = m.Tabs[0];
        m.NewTab("https://b.example/");
        Assert.True(TabsShortcuts.Handle(m, '1', ShortcutModifiers.Control));
        Assert.Same(a, m.SelectedTab);
        Assert.True(TabsShortcuts.Handle(m, 'A', ShortcutModifiers.Control | ShortcutModifiers.Shift));
        Assert.True(m.IsTabSearchVisible);
        Assert.True(TabsShortcuts.Handle(m, 'N', ShortcutModifiers.Control | ShortcutModifiers.Alt));
        Assert.Equal(2, m.Workspaces.Count);
        Assert.True(TabsShortcuts.Handle(m, 0x25, ShortcutModifiers.Control | ShortcutModifiers.Alt));
        Assert.Same(m.Workspaces[0], m.SelectedWorkspace);
        Assert.False(TabsShortcuts.Handle(m, 'Q', ShortcutModifiers.Control));
    });
}
