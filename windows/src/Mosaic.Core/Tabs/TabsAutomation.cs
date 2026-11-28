using System.Globalization;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Tabs;

/// <summary>
/// Automation commands and <c>dump</c> fields of the tabs area, with the names of
/// <c>Sources/Mosaic/Features/Tabs/TabsAutomation.swift</c>.
/// </summary>
/// <remarks>
/// A command that acts on a tab takes its index among the current workspace's tabs as the value,
/// or acts on the selected tab when the value is empty. Two-part values are separated by "|".
/// Workspaces and groups are named by name (or workspace index, group ID).
/// <code>
/// pin [i]  unpin [i]  togglePin [i]  mute [i]  unmute [i]  duplicate [i]
/// forceCloseTab [i]  selectVisible i  moveTab "from|to"  ageTab "i|minutes"
/// reopenClosed [n]  clearRecentlyClosed
/// newWorkspace [name]  selectWorkspace ws  renameWorkspace "ws|name"
/// deleteWorkspace ws  moveToWorkspace "ws" or "i|ws"  nextWorkspace  previousWorkspace
/// group [name|color]  addToGroup "group" or "i|group"  removeFromGroup [i]
/// ungroup g  closeGroup g  collapseGroup g  expandGroup g  renameGroup "g|name"
/// groupColor "g|color"  moveGroup "g|index"
/// unload [i]  unloadBackground  sleepCheck [normal|warning|critical]
/// memoryPressure normal|warning|critical  tabSearch show|hide|toggle  tabSearchQuery q
/// acceptDuplicate  dismissDuplicate  restoreSession  dismissRestore  saveSession
/// tabSetting "key=value"  tabEvent "tabs.search"  downloadActive "i|true"
/// </code>
/// Unlike macOS, commands that start asynchronous work (unload, sleepCheck, saveSession) reply
/// once it has finished, so a script can <c>dump</c> straight after. <c>renderTabsView</c> is
/// registered by the app (it needs WinUI).
/// </remarks>
public static class TabsAutomation
{
    public static readonly IReadOnlySet<string> Commands = new HashSet<string>
    {
        "pin", "unpin", "togglePin", "mute", "unmute", "duplicate", "forceCloseTab", "selectVisible",
        "moveTab", "ageTab", "reopenClosed", "clearRecentlyClosed", "newWorkspace", "selectWorkspace",
        "renameWorkspace", "deleteWorkspace", "moveToWorkspace", "nextWorkspace", "previousWorkspace",
        "group", "addToGroup", "removeFromGroup", "ungroup", "closeGroup", "collapseGroup", "expandGroup",
        "renameGroup", "groupColor", "moveGroup", "unload", "unloadBackground", "sleepCheck",
        "memoryPressure", "tabSearch", "tabSearchQuery", "acceptDuplicate", "dismissDuplicate", "restoreSession",
        "dismissRestore", "saveSession", "tabSetting", "tabEvent", "downloadActive",
    };

    private static int registered;

    /// <summary>Registers the commands and the "tabsArea" dump key once (the app's tabs mount; tests).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1) return;
        AutomationRegistry.Register(HandleAsync);
        AutomationRegistry.RegisterState("tabsArea", State);
    }

    private static Task<AutomationReply?> Done() => Task.FromResult<AutomationReply?>(AutomationRegistry.Ok());

    private static Task<AutomationReply?> Fail(string message) => Task.FromResult<AutomationReply?>(AutomationRegistry.Error(message));

    public static async Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output)
    {
        if (!Commands.Contains(command)) return null;
        var parts = value.Split('|');

        BrowserTab? Tab(string? text)
        {
            if (string.IsNullOrEmpty(text)) return model.SelectedTab;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < model.VisibleTabs.Count ? model.VisibleTabs[index] : null;
        }
        Workspace? WorkspaceNamed(string text)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < model.Workspaces.Count) return model.Workspaces[index];
            return model.Workspaces.FirstOrDefault(w => w.Name == text);
        }
        TabGroup? GroupNamed(string text) =>
            model.Groups.FirstOrDefault(g => g.Name == text)
            ?? model.Groups.FirstOrDefault(g => string.Equals(g.Id.ToString(), text, StringComparison.OrdinalIgnoreCase));
        // "i|x" → (tab i, x); "x" → (selected tab, x).
        (BrowserTab? Tab, string Argument) TabAndArgument() => parts.Length >= 2 ? (Tab(parts[0]), parts[1]) : (model.SelectedTab, value);

        Task<AutomationReply?> WithTab(string? text, Action<BrowserTab> action)
        {
            if (Tab(text) is not { } tab) return Fail($"There is no tab {text} in this workspace.");
            action(tab);
            return Done();
        }
        Task<AutomationReply?> WithWorkspace(string text, Action<Workspace> action)
        {
            if (WorkspaceNamed(text) is not { } workspace) return Fail($"There is no workspace \"{text}\".");
            action(workspace);
            return Done();
        }
        Task<AutomationReply?> WithGroup(string text, Action<TabGroup> action)
        {
            if (GroupNamed(text) is not { } group) return Fail($"There is no group \"{text}\".");
            action(group);
            return Done();
        }

        switch (command)
        {
            case "pin": return await WithTab(value, t => model.SetPinned(true, t));
            case "unpin": return await WithTab(value, t => model.SetPinned(false, t));
            case "togglePin": return await WithTab(value, model.TogglePin);
            case "mute": return await WithTab(value, t => model.SetMuted(true, t));
            case "unmute": return await WithTab(value, t => model.SetMuted(false, t));
            case "duplicate": return await WithTab(value, t => model.Duplicate(t));
            case "forceCloseTab": return await WithTab(value, model.Close);
            case "selectVisible": return await WithTab(value, model.Select);
            case "moveTab":
                if (parts.Length == 2 && Tab(parts[0]) is { } moving && int.TryParse(parts[1], out var to))
                {
                    model.MoveTab(moving, to);
                    return AutomationRegistry.Ok();
                }
                return AutomationRegistry.Error("Expected \"from|to\".");
            case "ageTab":
                if (parts.Length == 2 && Tab(parts[0]) is { } aged
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
                {
                    aged.LastActivated = model.Now - TimeSpan.FromMinutes(minutes);
                    return AutomationRegistry.Ok();
                }
                return AutomationRegistry.Error("Expected \"index|minutes\".");
            case "reopenClosed":
                if (int.TryParse(value, out var closedIndex) && closedIndex >= 0 && closedIndex < model.RecentlyClosedItems.Count)
                {
                    model.ReopenClosed(model.RecentlyClosedItems[closedIndex].Id);
                }
                else
                {
                    model.ReopenClosedTab();
                }
                return AutomationRegistry.Ok();
            case "clearRecentlyClosed":
                model.ClearRecentlyClosed();
                return AutomationRegistry.Ok();
            case "newWorkspace":
                model.CreateWorkspace(value.Length == 0 ? null : value);
                return AutomationRegistry.Ok();
            case "selectWorkspace": return await WithWorkspace(value, w => model.SelectWorkspace(w.Id));
            case "renameWorkspace":
                if (parts.Length != 2) return AutomationRegistry.Error("Expected \"workspace|name\".");
                return await WithWorkspace(parts[0], w => model.RenameWorkspace(w.Id, parts[1]));
            case "deleteWorkspace": return await WithWorkspace(value, w => model.DeleteWorkspace(w.Id));
            case "moveToWorkspace":
            {
                var (tab, name) = TabAndArgument();
                if (tab is null) return AutomationRegistry.Error("There is no such tab.");
                return await WithWorkspace(name, w => model.MoveTabToWorkspace(tab, w.Id));
            }
            case "nextWorkspace":
                model.SelectAdjacentWorkspace(1);
                return AutomationRegistry.Ok();
            case "previousWorkspace":
                model.SelectAdjacentWorkspace(-1);
                return AutomationRegistry.Ok();
            case "group":
            {
                var color = parts.Length > 1 ? TabGroupColors.FromId(parts[1]) : null;
                if (model.SelectedTab is { } selected) model.CreateGroup([selected], parts[0], color);
                return AutomationRegistry.Ok();
            }
            case "addToGroup":
            {
                var (tab, name) = TabAndArgument();
                if (tab is null) return AutomationRegistry.Error("There is no such tab.");
                return await WithGroup(name, g => model.AddTabToGroup(tab, g.Id));
            }
            case "removeFromGroup": return await WithTab(value, model.RemoveTabFromGroup);
            case "ungroup": return await WithGroup(value, g => model.Ungroup(g.Id));
            case "closeGroup": return await WithGroup(value, g => model.CloseGroup(g.Id));
            case "collapseGroup": return await WithGroup(value, g => model.SetGroupCollapsed(g.Id, true));
            case "expandGroup": return await WithGroup(value, g => model.SetGroupCollapsed(g.Id, false));
            case "renameGroup":
                if (parts.Length != 2) return AutomationRegistry.Error("Expected \"group|name\".");
                return await WithGroup(parts[0], g => model.RenameGroup(g.Id, parts[1]));
            case "groupColor":
                if (parts.Length != 2 || TabGroupColors.FromId(parts[1]) is not { } newColor)
                {
                    return AutomationRegistry.Error("Expected \"group|colour\" with one of " + string.Join(", ", Enum.GetValues<TabGroupColor>().Select(c => c.Id())) + ".");
                }
                return await WithGroup(parts[0], g => model.SetGroupColor(g.Id, newColor));
            case "moveGroup":
                if (parts.Length != 2 || !int.TryParse(parts[1], out var groupIndex)) return AutomationRegistry.Error("Expected \"group|index\".");
                return await WithGroup(parts[0], g => model.MoveGroup(g.Id, groupIndex));
            case "unload":
                if (Tab(value) is not { } sleeper) return AutomationRegistry.Error($"There is no tab {value} in this workspace.");
                return await model.UnloadAsync(sleeper) ? AutomationRegistry.Ok() : AutomationRegistry.Error("The tab was not unloaded.");
            case "unloadBackground":
                await model.UnloadBackgroundTabsAsync();
                return AutomationRegistry.Ok();
            case "sleepCheck":
                await model.RunSleepCheckAsync(MemoryPressureNames.FromId(value));
                return AutomationRegistry.Ok();
            case "memoryPressure":
                if (MemoryPressureNames.FromId(value) is not { } level) return AutomationRegistry.Error("Expected normal, warning or critical.");
                model.MemoryPressureChanged(level);
                return AutomationRegistry.Ok();
            case "tabSearch":
                switch (value)
                {
                    case "hide": model.HideTabSearch(); break;
                    case "toggle": model.ToggleTabSearch(); break;
                    default: model.ShowTabSearch(); break;
                }
                return AutomationRegistry.Ok();
            case "tabSearchQuery":
                model.TabSearchQuery = value;
                return AutomationRegistry.Ok();
            case "acceptDuplicate":
                model.AcceptDuplicateOffer();
                return AutomationRegistry.Ok();
            case "dismissDuplicate":
                model.DismissDuplicateOffer();
                return AutomationRegistry.Ok();
            case "restoreSession":
                model.RestorePreviousSession();
                return AutomationRegistry.Ok();
            case "dismissRestore":
                model.DismissCrashRecovery();
                return AutomationRegistry.Ok();
            case "saveSession":
                await model.SaveSession();
                return AutomationRegistry.Ok();
            case "tabSetting":
            {
                var pair = value.Split('=', 2);
                if (pair.Length != 2 || model.TabSettings.With(pair[0], pair[1]) is not { } updated)
                {
                    return AutomationRegistry.Error($"Unknown setting \"{value}\".");
                }
                model.TabSettings = updated;
                return AutomationRegistry.Ok();
            }
            case "tabEvent":
                model.HandleTabsEvent(value);
                return AutomationRegistry.Ok();
            case "downloadActive":
            {
                var (tab, flag) = TabAndArgument();
                if (tab is null) return AutomationRegistry.Error("There is no such tab.");
                model.SetDownloadActive(flag != "false", tab.Id);
                return AutomationRegistry.Ok();
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The "tabsArea" <c>dump</c> key: macOS's <c>tabsArea</c> fields, plus <c>tabs</c>, the
    /// per-tab fields macOS adds to each <c>dump.tabs</c> entry, in the same order as <c>dump.tabs</c>.
    /// </summary>
    public static JsonNode State(BrowserModel model)
    {
        var now = model.Now;
        var indices = model.Tabs.Select((tab, index) => (tab, index)).ToDictionary(x => x.tab, x => x.index);
        var state = new JsonObject
        {
            ["tabs"] = new JsonArray([.. model.Tabs.Select(tab => (JsonNode)new JsonObject
            {
                ["id"] = tab.Id.ToString(),
                ["pinned"] = tab.IsPinned,
                ["muted"] = tab.IsMuted,
                ["workspace"] = model.WorkspaceOf(tab)?.Name ?? "",
                ["group"] = model.GroupOf(tab)?.Name ?? (tab.GroupId is null ? "" : "?"),
                ["asleep"] = tab.IsAsleep,
                ["playingAudio"] = tab.IsPlayingAudio,
                ["chromiumAudio"] = tab.Chromium is IChromiumAudio,
                ["downloading"] = model.HasActiveDownload(tab),
                ["inactiveSeconds"] = (int)Math.Max(0, (now - tab.LastActivated).TotalSeconds),
            })]),
            ["selectedWorkspace"] = model.SelectedWorkspace.Name,
            ["workspaces"] = new JsonArray([.. model.Workspaces.Select(w => (JsonNode)new JsonObject
            {
                ["id"] = w.Id.ToString(),
                ["name"] = w.Name,
                ["symbol"] = w.Symbol,
                ["selected"] = w.Id == model.SelectedWorkspaceId,
                ["tabs"] = model.TabsIn(w.Id).Count,
            })]),
            ["visibleTabs"] = new JsonArray([.. model.VisibleTabs.Select(t => (JsonNode)indices[t])]),
            ["visibleTitles"] = new JsonArray([.. model.VisibleTabs.Select(t => (JsonNode)t.DisplayTitle)]),
            ["groups"] = new JsonArray([.. model.Groups.Select(g => (JsonNode)new JsonObject
            {
                ["id"] = g.Id.ToString(),
                ["name"] = g.Name,
                ["color"] = g.Color.Id(),
                ["collapsed"] = g.IsCollapsed,
                ["tabs"] = new JsonArray([.. model.TabsInGroup(g.Id).Select(t => (JsonNode)t.DisplayTitle)]),
            })]),
            ["recentlyClosed"] = new JsonArray([.. model.RecentlyClosedItems.Select(item => (JsonNode)new JsonObject
            {
                ["title"] = item.Title,
                ["kind"] = SessionJson.KindName(item.Kind),
                ["urls"] = new JsonArray([.. item.Tabs.Select(t => (JsonNode)t.Url)]),
                ["engines"] = new JsonArray([.. item.Tabs.Select(t => (JsonNode)t.Engine)]),
            })]),
            ["offersCrashRecovery"] = model.OffersCrashRecovery,
            ["previousSessionPages"] = model.PreviousSession is null ? -1 : model.PreviousSessionPageCount,
            ["previousRun"] = model.PreviousRun switch { PreviousRun.Clean => "clean", PreviousRun.Crashed => "crashed", _ => "firstRun" },
            ["started"] = model.AreTabsStarted,
            ["tabSearchVisible"] = model.IsTabSearchVisible,
            ["tabSearchQuery"] = model.TabSearchQuery,
            ["tabSearchResults"] = new JsonArray([.. model.TabSearchResults(model.TabSearchQuery).Select(t => (JsonNode)t.DisplayTitle)]),
            ["memoryPressure"] = model.MemoryPressure.Id(),
            ["asleep"] = model.AsleepTabCount,
            ["sessionFile"] = model.SessionFile ?? "",
            ["settings"] = model.TabSettings.ToJson(),
        };
        if (model.DuplicateOffer is { } offer)
        {
            state["duplicateOffer"] = new JsonObject
            {
                ["title"] = offer.Title,
                ["closesNewTab"] = offer.ClosesNewTab,
                ["existingIndex"] = model.TabWithId(offer.ExistingTabId) is { } existing ? indices[existing] : -1,
                ["workspace"] = offer.WorkspaceName ?? "",
            };
        }
        return state;
    }
}
