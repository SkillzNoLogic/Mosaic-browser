using Mosaic.Core.Tabs;

namespace Mosaic.Core.Browser;

// Tabs area, part 3: session save and restore, and crash recovery. Port of
// Sources/Mosaic/Features/Tabs/BrowserModel+Session.swift.
//
// The session (tabs with engine, URL, title, pinned, muted, group and workspace; groups;
// workspaces; each workspace's selected tab; Recently Closed) is written to
// %LOCALAPPDATA%\Mosaic\Session\session.json about a second after the structure changes, within
// fifteen seconds after a page changes, and on quit. The snapshot is taken on the UI thread
// (cheap) and encoded and written on the thread pool, one write at a time. A marker file tells
// a crash from a quit.
//
// At launch, per Settings › Tabs & Workspaces:
// - "Open previous session": everything comes back. After a crash too (with a notice).
// - "Start page": one new tab; workspaces and Recently Closed are always kept, pinned tabs too
//   if "Keep pinned tabs" is on. The rest is kept as the previous session (Reopen All Tabs from
//   Last Session). After a crash, a banner offers it.
// Restored tabs are asleep: only the selected one loads; the others load when selected.
//
// Startup cost: the window opens with the constructor's blank tab; StartTabsAsync reads the
// settings and the session on the thread pool and then replaces that tab, so nothing here runs
// before the first frame.
public sealed partial class BrowserModel
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PageSaveDelay = TimeSpan.FromSeconds(15);

    private SessionStore? sessionStore;
    private TabSettingsStore? tabSettingsStore;
    private bool tabsStarted;
    private bool tabsFinished;
    private DateTimeOffset? saveDue;
    private int saveVersion;
    private Task writeChain = Task.CompletedTask;
    /// <summary>Guards <see cref="writeChain"/> (held only to chain a write, never during one).</summary>
    private readonly object chainLock = new();
    /// <summary>Held while a session file is written (thread pool, and the final save at shutdown).</summary>
    private readonly object writeLock = new();
    /// <summary>The last session written, encoded with a blank date, to skip identical writes (thread pool only).</summary>
    private string? lastSavedSession;
    private bool saveFailureReported;
    private SessionSnapshot? previousSession;
    private bool offersCrashRecovery;
    /// <summary>IDs (in the snapshot) of tabs restored at launch, so a later "restore" skips them.</summary>
    private readonly HashSet<Guid> restoredSessionTabIds = [];

    /// <summary>How the previous run ended (from the run marker).</summary>
    public PreviousRun PreviousRun { get; private set; } = PreviousRun.FirstRun;

    /// <summary>True once the session has been restored (or found empty) and saving has started.</summary>
    public bool AreTabsStarted => tabsStarted;

    /// <summary>The session file, for <c>dump</c>; null before <see cref="StartTabsAsync"/>.</summary>
    public string? SessionFile => sessionStore?.SessionPath;

    /// <summary>
    /// A session this launch did not restore (start-page setting, or a crash), reopenable with
    /// <see cref="RestorePreviousSession"/> and from the crash-recovery banner.
    /// </summary>
    public SessionSnapshot? PreviousSession
    {
        get => previousSession;
        private set
        {
            if (Set(ref previousSession, value)) Notify(nameof(PreviousSessionPageCount));
        }
    }

    /// <summary>The previous run crashed and its tabs were not restored: show the banner offering to restore them.</summary>
    public bool OffersCrashRecovery { get => offersCrashRecovery; private set => Set(ref offersCrashRecovery, value); }

    /// <summary>Pages <see cref="RestorePreviousSession"/> would bring back (not counting tabs already restored).</summary>
    public int PreviousSessionPageCount =>
        previousSession?.Tabs.Count(t => t.Url.Length > 0 && !restoredSessionTabIds.Contains(t.Id)) ?? 0;

    /// <summary>
    /// Restores the session (or keeps the blank tab) and starts saving and the sleeping-tabs
    /// policy. Called once, after the window is up (the tabs area's startup mount); tests pass
    /// their own stores.
    /// </summary>
    public async Task StartTabsAsync(
        SessionStore? store = null, TabSettingsStore? settingsStore = null, IMemoryPressureSource? pressure = null)
    {
        if (tabsStarted || isShutDown) return;
        tabsStarted = true;
        EnsureTabsWired();
        sessionStore = store ?? new SessionStore(SessionStore.DefaultDirectory);
        tabSettingsStore = settingsStore ?? new TabSettingsStore(TabSettingsStore.DefaultPath);
        memoryPressureSource = pressure ?? new MemoryPressureMonitor();

        var sessions = sessionStore;
        var settingsFile = tabSettingsStore;
        var (settings, previousRun, snapshot, unreadable) = await Task.Run(() =>
        {
            var loaded = settingsFile.Load();
            var run = sessions.BeginRun();
            SessionSnapshot? saved = null;
            string? setAside = null;
            var failed = false;
            try
            {
                saved = sessions.Load();
            }
            catch (SessionDecodeException)
            {
                failed = true;
                setAside = sessions.SetAsideUnreadable();
            }
            return (loaded, run, saved, failed ? (setAside ?? "") : null);
        });
        if (isShutDown) return;

        tabSettings = settings;
        Notify(nameof(TabSettings));
        PreviousRun = previousRun;
        if (unreadable is not null)
        {
            Notice = "The saved session could not be read" + (unreadable.Length > 0 ? $"; it was kept as {Path.GetFileName(unreadable)}" : "") + ".";
        }

        if (snapshot is not null) RestoreAtLaunch(snapshot, previousRun == PreviousRun.Crashed);

        if (VisibleTabs.Count == 0)
        {
            NewTab();
        }
        else if (selectedTab is null || !VisibleTabs.Contains(selectedTab))
        {
            var remembered = selectionByWorkspace.TryGetValue(SelectedWorkspaceId, out var id) ? TabWithId(id) : null;
            if ((remembered ?? NavigableTabs.FirstOrDefault()) is { } first) Select(first);
        }

        StartSleepingTabs();
        _ = SaveSession();
    }

    private void RestoreAtLaunch(SessionSnapshot snapshot, bool crashed)
    {
        // The blank tab the window opened with is replaced when the session brings tabs back,
        // unless it has been used meanwhile.
        var lone = Tabs.Count == 1 && Tabs[0].IsStartPage ? Tabs[0] : null;
        RestoreStructure(snapshot);
        switch (tabSettings.Startup)
        {
            case TabStartup.PreviousSession:
                RestoreTabs(snapshot, selecting: true);
                if (lone is not null && Tabs.Count > 1 && !ReferenceEquals(lone, selectedTab)) CloseWithoutRecording(lone);
                _ = Task.Run(sessionStore!.RemovePrevious);
                if (crashed && snapshot.PageCount > 0) Notice = "Mosaic did not quit properly last time. Your tabs were restored.";
                break;
            case TabStartup.StartPage:
                if (tabSettings.KeepPinnedTabs) RestoreTabs(snapshot, selecting: false, saved => saved.IsPinned);
                var leftOver = snapshot.Tabs.Any(t => t.Url.Length > 0 && !restoredSessionTabIds.Contains(t.Id));
                var store = sessionStore!;
                if (leftOver)
                {
                    PreviousSession = snapshot;
                    _ = Task.Run(() => store.SavePrevious(snapshot));
                    OffersCrashRecovery = crashed;
                }
                else
                {
                    _ = LoadPreviousSessionAsync(store);
                }
                // The start page itself, in front of any pinned tabs (which stay asleep).
                if (lone is not null && Tabs.Contains(lone)) Select(lone);
                else NewTab();
                break;
        }
    }

    private async Task LoadPreviousSessionAsync(SessionStore store)
    {
        SessionSnapshot? previous = null;
        try
        {
            previous = await Task.Run(store.LoadPrevious);
        }
        catch (SessionDecodeException)
        {
        }
        if (previous is not null && previousSession is null) PreviousSession = previous;
    }

    /// <summary>Called from <see cref="Shutdown"/>: the final save, then the clean-exit marker.</summary>
    private void FinishTabs()
    {
        if (!tabsStarted || tabsFinished) return;
        saveVersion++;
        saveDue = null;
        sleepLoop?.Cancel();
        // Waits (briefly) for a write in flight, then writes the final state on this thread.
        try { writeChain.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        if (sessionStore is { } store)
        {
            try
            {
                lock (writeLock) store.Save(MakeSessionSnapshot());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            tabsFinished = true;
            store.EndRun();
        }
        tabsFinished = true;
        (memoryPressureSource as IDisposable)?.Dispose();
    }

    // ---- Saving ----

    /// <summary>Saves after <paramref name="delay"/> (one second by default); changes in quick succession are written once.</summary>
    private void ScheduleSessionSave(TimeSpan? delay = null)
    {
        if (!tabsStarted || tabsFinished || sessionStore is null) return;
        var now = time.GetUtcNow();
        var due = now + (delay ?? SaveDelay);
        if (saveDue is { } existing && existing <= due) return;
        saveDue = due;
        var version = ++saveVersion;
        _ = SaveLaterAsync(due - now, version);
    }

    private async Task SaveLaterAsync(TimeSpan wait, int version)
    {
        await Task.Delay(wait, time);
        if (version != saveVersion) return;
        saveDue = null;
        await SaveSession();
    }

    /// <summary>
    /// Writes the session now if it changed since the last write: the snapshot is taken here, the
    /// JSON is built and written on the thread pool. Returns the write, for tests and automation.
    /// </summary>
    public Task SaveSession()
    {
        if (!tabsStarted || tabsFinished || sessionStore is not { } store) return Task.CompletedTask;
        var snapshot = MakeSessionSnapshot();
        var context = SynchronizationContext.Current;
        lock (chainLock)
        {
            writeChain = writeChain.ContinueWith(_ => WriteSession(store, snapshot, context), TaskScheduler.Default);
            return writeChain;
        }
    }

    private void WriteSession(SessionStore store, SessionSnapshot snapshot, SynchronizationContext? context)
    {
        lock (writeLock)
        {
            var comparable = SessionCodec.Encode(snapshot with { SavedAt = DateTimeOffset.UnixEpoch });
            if (comparable == lastSavedSession) return;
            try
            {
                store.SaveEncoded(SessionCodec.Encode(snapshot));
                lastSavedSession = comparable;
                if (saveFailureReported) Post(context, () => saveFailureReported = false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Post(context, () =>
                {
                    if (saveFailureReported) return;
                    saveFailureReported = true;
                    Notice = $"Mosaic could not save your tabs: {error.Message}";
                });
            }
        }
    }

    private static void Post(SynchronizationContext? context, Action action)
    {
        if (context is null) action();
        else context.Post(_ => action(), null);
    }

    /// <summary>The session as it is now. Private tabs are never part of it.</summary>
    public SessionSnapshot MakeSessionSnapshot()
    {
        var selected = selectionByWorkspace.Values.ToHashSet();
        if (selectedTab is { } current) selected.Add(current.Id);
        return new SessionSnapshot
        {
            SavedAt = time.GetUtcNow(),
            Workspaces = Workspaces.Select(w => w.ToSession()).ToList(),
            SelectedWorkspaceId = SelectedWorkspaceId,
            Groups = Groups.Select(g => g.ToSession()).ToList(),
            Tabs = Tabs.Where(t => !IsPrivateTab(t)).Select(t => new SessionTab
            {
                Id = t.Id,
                Engine = t.Engine.Id(),
                Url = t.Url,
                Title = t.Title,
                IsPinned = t.IsPinned,
                IsMuted = t.IsMuted,
                WorkspaceId = WorkspaceIdOf(t),
                GroupId = t.GroupId,
                IsSelected = selected.Contains(t.Id),
            }).ToList(),
            RecentlyClosed = recentlyClosed.Items.ToList(),
        };
    }

    private void TabSettingsChanged(TabSettings old)
    {
        if (tabSettings.RecentlyClosedLimit < old.RecentlyClosedLimit)
        {
            recentlyClosed.Trim(tabSettings.RecentlyClosedLimit);
            RecentlyClosedDidChange();
        }
        if (tabSettings.Sleep != old.Sleep && tabsStarted) _ = RunSleepCheckAsync();
    }

    // ---- Restoring ----

    /// <summary>
    /// "Reopen All Tabs from Last Session" and the crash-recovery banner: brings back the session
    /// this launch did not restore (skipping tabs already restored, such as pinned tabs). The
    /// selected start-page tab, if any, is replaced.
    /// </summary>
    public void RestorePreviousSession()
    {
        if (previousSession is not { } snapshot) return;
        var lone = selectedTab is { IsStartPage: true } blank ? blank : null;
        foreach (var saved in snapshot.Workspaces)
        {
            if (IndexOfWorkspace(saved.Id) < 0) Workspaces.Add(Workspace.FromSession(saved));
        }
        // Pinned tabs kept since that session (other tab IDs) must not be doubled.
        var openPinned = Tabs.Where(t => t.IsPinned).Select(t => t.Url).ToList();
        RestoreTabs(snapshot, selecting: true, saved => !(saved.IsPinned && openPinned.Any(url => UrlIdentity.SamePage(url, saved.Url))));
        if (lone is not null && Tabs.Count > 1 && !ReferenceEquals(lone, selectedTab)) CloseWithoutRecording(lone);
        PreviousSession = null;
        OffersCrashRecovery = false;
        if (sessionStore is { } store) _ = Task.Run(store.RemovePrevious);
        OrganizationChanged();
    }

    public void DismissCrashRecovery() => OffersCrashRecovery = false;

    /// <summary>Workspaces and Recently Closed, which are kept whatever the startup setting.</summary>
    private void RestoreStructure(SessionSnapshot snapshot)
    {
        Workspaces.Clear();
        foreach (var saved in snapshot.Workspaces) Workspaces.Add(Workspace.FromSession(saved));
        if (Workspaces.Count == 0) Workspaces.Add(new Workspace(Guid.NewGuid(), "Personal", "person"));
        selectedWorkspaceId = snapshot.SelectedWorkspaceId ?? Workspaces[0].Id;
        // Tabs opened before the session was read (the blank tab) join the saved workspace.
        foreach (var tab in Tabs)
        {
            if (tab.WorkspaceId is not { } id || IndexOfWorkspace(id) < 0) tab.WorkspaceId = SelectedWorkspaceId;
        }
        recentlyClosed = new RecentlyClosedList(snapshot.RecentlyClosed);
        recentlyClosed.Trim(tabSettings.RecentlyClosedLimit);
        Notify(nameof(SelectedWorkspaceId));
        Notify(nameof(SelectedWorkspace));
        RecentlyClosedChanged?.Invoke();
        OrganizationChanged();
    }

    /// <summary>
    /// Recreates the snapshot's tabs (those passing <paramref name="include"/>) at the end of the
    /// list, asleep, with their groups. With <paramref name="selecting"/>, shows the snapshot's
    /// workspace and its selected tab, which is the one tab that loads.
    /// </summary>
    private void RestoreTabs(SessionSnapshot snapshot, bool selecting, Func<SessionTab, bool>? include = null)
    {
        var restored = new List<BrowserTab>();
        var selection = new Dictionary<Guid, BrowserTab>();
        foreach (var saved in snapshot.Tabs)
        {
            if (include is not null && !include(saved)) continue;
            if (restoredSessionTabIds.Contains(saved.Id)) continue;
            var tab = MakeRestoredTab(saved.Engine, saved.Url, saved.Title);
            var workspace = saved.WorkspaceId is { } wid && IndexOfWorkspace(wid) >= 0 ? wid : SelectedWorkspaceId;
            tab.WorkspaceId = workspace;
            tab.IsPinned = saved.IsPinned;
            tab.IsMuted = saved.IsMuted;
            if (saved.GroupId is { } gid && snapshot.Groups.FirstOrDefault(g => g.Id == gid) is { } group)
            {
                if (GroupWithId(gid) is null) Groups.Add(TabGroup.FromSession(group));
                tab.GroupId = gid;
            }
            restored.Add(tab);
            restoredSessionTabIds.Add(saved.Id);
            if (saved.IsSelected) selection[workspace] = tab;
        }
        if (restored.Count == 0) return;
        foreach (var tab in restored) Tabs.Add(tab);
        NormalizeTabOrder();
        foreach (var (workspace, tab) in selection)
        {
            if (selecting || !selectionByWorkspace.ContainsKey(workspace)) selectionByWorkspace[workspace] = tab.Id;
        }
        Notify(nameof(PreviousSessionPageCount));
        if (!selecting) return;
        if (snapshot.SelectedWorkspaceId is { } sel && IndexOfWorkspace(sel) >= 0) SelectedWorkspaceId = sel;
        var target = selection.GetValueOrDefault(SelectedWorkspaceId) ?? NavigableTabs.FirstOrDefault(restored.Contains);
        if (target is not null) Select(target);
    }
}
