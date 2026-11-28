using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Mosaic.Core.Browser;
using Mosaic.Core.Tabs;

namespace Mosaic.App.Features.Tabs;

/// <summary>
/// The tabs area's menus, for the shell to attach where it draws tabs, groups and workspaces
/// (port of <c>TabsMenuController.swift</c>'s Tab and History items):
/// <code>
/// tabItem.ContextFlyout = TabsMenus.ForTab(model, tab);
/// groupHeader.ContextFlyout = TabsMenus.ForGroup(model, group);
/// historyButton.Flyout = TabsMenus.RecentlyClosed(model);
/// </code>
/// Each menu is rebuilt as it opens, so it always shows the current state, and hides Gecko's
/// page while it is open (a Win32 child window would cover it).
/// </summary>
public static class TabsMenus
{
    /// <summary>Says <see cref="FeatureMounts.CoverPage"/> while <paramref name="flyout"/> is open.</summary>
    public static void CoverWhileOpen(FlyoutBase flyout, string id)
    {
        flyout.Opening += (_, _) => FeatureMounts.CoverPage(id, true);
        flyout.Closed += (_, _) => FeatureMounts.CoverPage(id, false);
    }

    /// <summary>The context menu of a tab.</summary>
    public static MenuFlyout ForTab(BrowserModel model, BrowserTab tab)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => FillTabMenu(menu, model, tab);
        CoverWhileOpen(menu, "tabs.menu.tab");
        return menu;
    }

    /// <summary>The selected tab's menu (a "Tab" menu in a menu bar or the "…" menu).</summary>
    public static MenuFlyout ForSelectedTab(BrowserModel model)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add(Item("Search Tabs…", model.ShowTabSearch, "Ctrl+Shift+A"));
            menu.Items.Add(new MenuFlyoutSeparator());
            if (model.SelectedTab is { } tab)
            {
                var tabMenu = new MenuFlyout();
                FillTabMenu(tabMenu, model, tab);
                foreach (var item in tabMenu.Items.ToList())
                {
                    tabMenu.Items.Remove(item);
                    menu.Items.Add(item);
                }
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("New Workspace", () => model.CreateWorkspace(), "Ctrl+Alt+N"));
            menu.Items.Add(WorkspacesSubmenu(model));
            menu.Items.Add(Item("Next Workspace", () => model.SelectAdjacentWorkspace(1), "Ctrl+Alt+→", model.Workspaces.Count > 1));
            menu.Items.Add(Item("Previous Workspace", () => model.SelectAdjacentWorkspace(-1), "Ctrl+Alt+←", model.Workspaces.Count > 1));
            menu.Items.Add(new MenuFlyoutSeparator());
            AddHistoryItems(menu, model);
        };
        CoverWhileOpen(menu, "tabs.menu.selected");
        return menu;
    }

    /// <summary>Recently Closed, Reopen Closed Tab and Reopen All Tabs from Last Session (a History menu).</summary>
    public static MenuFlyout RecentlyClosed(BrowserModel model)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            AddHistoryItems(menu, model);
        };
        CoverWhileOpen(menu, "tabs.menu.history");
        return menu;
    }

    /// <summary>The context menu of a group header.</summary>
    public static MenuFlyout ForGroup(BrowserModel model, TabGroup group)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var rename = Item("Rename Group…", () => { });
            rename.Click += async (_, _) =>
            {
                if (await AskForTextAsync(menu.XamlRoot, "Rename group", group.Name, "Name") is { } name) model.RenameGroup(group.Id, name);
            };
            menu.Items.Add(rename);
            var colors = new MenuFlyoutSubItem { Text = "Colour" };
            foreach (var color in Enum.GetValues<TabGroupColor>())
            {
                var item = new RadioMenuFlyoutItem { Text = color.DisplayName(), IsChecked = group.Color == color, GroupName = "tabs.group.colour" };
                item.Click += (_, _) => model.SetGroupColor(group.Id, color);
                colors.Items.Add(item);
            }
            menu.Items.Add(colors);
            menu.Items.Add(Item(group.IsCollapsed ? "Expand Group" : "Collapse Group", () => model.ToggleGroupCollapsed(group.Id)));
            menu.Items.Add(Item("New Tab in Group", () =>
            {
                var tab = model.NewTab();
                model.AddTabToGroup(tab, group.Id);
            }));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Ungroup", () => model.Ungroup(group.Id)));
            menu.Items.Add(Item("Close Group", () => model.CloseGroup(group.Id)));
        };
        CoverWhileOpen(menu, "tabs.menu.group");
        return menu;
    }

    /// <summary>The context menu of a workspace in the switcher.</summary>
    public static MenuFlyout ForWorkspace(BrowserModel model, Workspace workspace)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var rename = Item("Rename Workspace…", () => { });
            rename.Click += async (_, _) =>
            {
                if (await AskForTextAsync(menu.XamlRoot, "Rename workspace", workspace.Name, "Name") is { } name) model.RenameWorkspace(workspace.Id, name);
            };
            menu.Items.Add(rename);
            var icons = new MenuFlyoutSubItem { Text = "Icon" };
            foreach (var symbol in Workspace.Symbols)
            {
                var item = new RadioMenuFlyoutItem { Text = symbol, IsChecked = workspace.Symbol == symbol, GroupName = "tabs.workspace.icon" };
                item.Click += (_, _) => model.SetWorkspaceSymbol(workspace.Id, symbol);
                icons.Items.Add(item);
            }
            menu.Items.Add(icons);
            var index = model.Workspaces.IndexOf(workspace);
            menu.Items.Add(Item("Move Left", () => model.MoveWorkspace(workspace.Id, index - 1), enabled: index > 0));
            menu.Items.Add(Item("Move Right", () => model.MoveWorkspace(workspace.Id, index + 1), enabled: index < model.Workspaces.Count - 1));
            menu.Items.Add(new MenuFlyoutSeparator());
            var delete = Item("Delete Workspace…", () => { }, enabled: model.Workspaces.Count > 1);
            delete.Click += async (_, _) => await ConfirmAndDeleteWorkspaceAsync(model, workspace, menu.XamlRoot);
            menu.Items.Add(delete);
        };
        CoverWhileOpen(menu, "tabs.menu.workspace");
        return menu;
    }

    private static void FillTabMenu(MenuFlyout menu, BrowserModel model, BrowserTab tab)
    {
        menu.Items.Clear();
        if (!model.Tabs.Contains(tab)) return;
        menu.Items.Add(Item(tab.IsPinned ? "Unpin Tab" : "Pin Tab", () => model.TogglePin(tab)));
        menu.Items.Add(Item("Duplicate Tab", () => model.Duplicate(tab)));
        menu.Items.Add(Item(tab.IsMuted ? "Unmute Tab" : "Mute Tab", () => model.ToggleMute(tab)));
        menu.Items.Add(Item("Unload Tab", () => _ = model.UnloadTabAsync(tab),
            enabled: !tab.IsAsleep && !tab.IsStartPage && !tab.IsInternalPage && model.VisibleTabs.Count > 1));
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(Item("Add Tab to New Group", () => model.CreateGroup([tab])));
        var otherGroups = model.Groups.Where(g => g.Id != tab.GroupId && model.TabsInGroup(g.Id).Any(t => model.WorkspaceIdOf(t) == model.WorkspaceIdOf(tab))).ToList();
        if (otherGroups.Count > 0)
        {
            var groups = new MenuFlyoutSubItem { Text = "Add Tab to Group" };
            foreach (var group in otherGroups) groups.Items.Add(Item(group.DisplayName, () => model.AddTabToGroup(tab, group.Id)));
            menu.Items.Add(groups);
        }
        if (model.GroupOf(tab) is { } own)
        {
            menu.Items.Add(Item("Remove Tab from Group", () => model.RemoveTabFromGroup(tab)));
            menu.Items.Add(Item(own.IsCollapsed ? "Expand Group" : "Collapse Group", () => model.ToggleGroupCollapsed(own.Id)));
        }
        var move = new MenuFlyoutSubItem { Text = "Move Tab to Workspace" };
        var current = model.WorkspaceIdOf(tab);
        foreach (var workspace in model.Workspaces.Where(w => w.Id != current))
        {
            move.Items.Add(Item(workspace.Name, () => model.MoveTabToWorkspace(tab, workspace.Id)));
        }
        if (move.Items.Count > 0) move.Items.Add(new MenuFlyoutSeparator());
        move.Items.Add(Item("New Workspace", () =>
        {
            var workspace = model.CreateWorkspace(switchTo: false);
            model.MoveTabToWorkspace(tab, workspace.Id);
        }));
        menu.Items.Add(move);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Close Tab", () => model.Close(tab), "Ctrl+W"));
        var others = model.TabsIn(current).Where(t => !ReferenceEquals(t, tab) && !t.IsPinned).ToList();
        menu.Items.Add(Item("Close Other Tabs", () =>
        {
            foreach (var other in others) model.Close(other);
        }, enabled: others.Count > 0));
    }

    private static void AddHistoryItems(MenuFlyout menu, BrowserModel model)
    {
        var latest = model.RecentlyClosedItems.FirstOrDefault();
        var title = latest?.Kind switch
        {
            ClosedItemKind.Group => "Reopen Closed Group",
            ClosedItemKind.Workspace => "Reopen Closed Workspace",
            _ => "Reopen Closed Tab",
        };
        menu.Items.Add(Item(title, () => model.ReopenClosedTab(), "Ctrl+Shift+T", latest is not null));
        var recent = new MenuFlyoutSubItem { Text = "Recently Closed" };
        if (model.RecentlyClosedItems.Count == 0)
        {
            recent.Items.Add(new MenuFlyoutItem { Text = "No Recently Closed Tabs", IsEnabled = false });
        }
        else
        {
            foreach (var item in model.RecentlyClosedItems)
            {
                var entry = Item(item.Title, () => model.ReopenClosed(item.Id));
                ToolTipService.SetToolTip(entry, item.Tabs.FirstOrDefault()?.Url);
                recent.Items.Add(entry);
            }
            recent.Items.Add(new MenuFlyoutSeparator());
            recent.Items.Add(Item("Clear Recently Closed", model.ClearRecentlyClosed));
        }
        menu.Items.Add(recent);
        menu.Items.Add(Item("Reopen All Tabs from Last Session", model.RestorePreviousSession, enabled: model.PreviousSession is not null));
    }

    private static MenuFlyoutSubItem WorkspacesSubmenu(BrowserModel model)
    {
        var switchTo = new MenuFlyoutSubItem { Text = "Switch to Workspace" };
        foreach (var workspace in model.Workspaces)
        {
            var item = new RadioMenuFlyoutItem { Text = workspace.Name, IsChecked = workspace.Id == model.SelectedWorkspaceId, GroupName = "tabs.workspaces" };
            item.Click += (_, _) => model.SelectWorkspace(workspace.Id);
            switchTo.Items.Add(item);
        }
        return switchTo;
    }

    private static MenuFlyoutItem Item(string text, Action action, string? shortcut = null, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (shortcut is not null) item.KeyboardAcceleratorTextOverride = shortcut;
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Deletes a workspace, asking first when that would close tabs that show something
    /// (Ctrl+Shift+T brings it back with its tabs).
    /// </summary>
    public static async Task ConfirmAndDeleteWorkspaceAsync(BrowserModel model, Workspace workspace, XamlRoot? root)
    {
        if (model.Workspaces.Count < 2) return;
        if (model.WorkspaceDeletionNeedsConfirmation(workspace.Id) && root is not null)
        {
            var count = model.TabsIn(workspace.Id).Count(t => !t.IsStartPage);
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = $"Delete “{workspace.Name}”?",
                Content = $"Its {TabsStyle.Plural(count, "tab", "tabs")} will be closed. Reopen Closed Tab (Ctrl+Shift+T) brings the workspace back with its tabs.",
                PrimaryButtonText = "Delete Workspace and Close Tabs",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            FeatureMounts.CoverPage("tabs.dialog", true);
            try
            {
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            finally
            {
                FeatureMounts.CoverPage("tabs.dialog", false);
            }
        }
        model.DeleteWorkspace(workspace.Id);
    }

    /// <summary>A small dialog with one text field; null when cancelled or left empty.</summary>
    public static async Task<string?> AskForTextAsync(XamlRoot? root, string title, string text, string label)
    {
        if (root is null) return null;
        var box = new TextBox { Text = text, Header = label, SelectionStart = 0, SelectionLength = text.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        FeatureMounts.CoverPage("tabs.dialog", true);
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        }
        finally
        {
            FeatureMounts.CoverPage("tabs.dialog", false);
        }
        var trimmed = box.Text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
