using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mosaic.Core.Browser;
using Mosaic.Core.Tabs;
using Windows.System;

namespace Mosaic.App.Features.Tabs;

/// <summary>Settings › Tabs &amp; Workspaces. Port of <c>TabsSettingsPane.swift</c>.</summary>
internal sealed class TabsSettingsPage : UserControl
{
    private readonly BrowserModel model;
    private readonly ComboBox startup = new() { MinWidth = 200 };
    private readonly ToggleSwitch keepPinned = new() { OnContent = "", OffContent = "" };
    private readonly ToggleSwitch protectPinned = new() { OnContent = "", OffContent = "" };
    private readonly ToggleSwitch offerSwitch = new() { OnContent = "", OffContent = "" };
    private readonly NumberBox recentLimit = new()
    {
        Minimum = TabSettings.RecentlyClosedRange.Min,
        Maximum = TabSettings.RecentlyClosedRange.Max,
        SmallChange = 5,
        LargeChange = 10,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        MinWidth = 130,
    };
    private readonly TextBlock recentCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button clearRecent = new() { Content = "Clear" };
    private readonly Border previousSessionRow;
    private readonly TextBlock startupExplanation;
    private readonly StackPanel workspaceRows = new();
    private readonly Dictionary<Guid, (TextBox Name, TextBlock Count, Button Switch, TextBlock Current, Button Delete, ContentControl Icon)> rows = [];
    private readonly List<(Workspace Workspace, PropertyChangedEventHandler Handler)> workspaceHandlers = [];
    private bool updating;

    public TabsSettingsPage(BrowserModel model)
    {
        this.model = model;
        AutomationProperties.SetName(this, "Tabs & Workspaces settings");

        startup.Items.Add(new ComboBoxItem { Content = "Open previous session", Tag = TabStartup.PreviousSession });
        startup.Items.Add(new ComboBoxItem { Content = "Start page", Tag = TabStartup.StartPage });
        startup.SelectionChanged += (_, _) =>
        {
            if (!updating && startup.SelectedItem is ComboBoxItem { Tag: TabStartup value }) Change(s => s with { Startup = value });
        };
        keepPinned.Toggled += (_, _) => Change(s => s with { KeepPinnedTabs = keepPinned.IsOn });
        protectPinned.Toggled += (_, _) => Change(s => s with { ProtectPinnedTabs = protectPinned.IsOn });
        offerSwitch.Toggled += (_, _) => Change(s => s with { OfferSwitchToOpenTab = offerSwitch.IsOn });
        recentLimit.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue)) Change(s => s with { RecentlyClosedLimit = (int)Math.Round(args.NewValue) });
        };
        clearRecent.Click += (_, _) => model.ClearRecentlyClosed();
        AutomationProperties.SetName(clearRecent, "Clear recently closed tabs");

        var reopen = new Button { Content = "Reopen All Tabs from Last Session" };
        reopen.Click += (_, _) => model.RestorePreviousSession();
        previousSessionRow = TabsStyle.Row("Last session", null, reopen);
        startupExplanation = TabsStyle.Footnote("");

        var recentPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        recentPanel.Children.Add(recentCount);
        recentPanel.Children.Add(clearRecent);

        var newWorkspace = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { TabsStyle.Icon("plus", "+"), new TextBlock { Text = "New Workspace" } },
            },
            Margin = new Thickness(0, 4, 0, 0),
        };
        newWorkspace.Click += (_, _) => model.CreateWorkspace(switchTo: false);

        var content = new StackPanel();
        content.Children.Add(TabsStyle.SectionHeader("Startup"));
        content.Children.Add(TabsStyle.Row("When Mosaic starts", null, startup));
        content.Children.Add(TabsStyle.Row("Keep pinned tabs", "With the start page, still bring back pinned tabs", keepPinned));
        content.Children.Add(previousSessionRow);
        content.Children.Add(startupExplanation);

        content.Children.Add(TabsStyle.SectionHeader("Tabs"));
        content.Children.Add(TabsStyle.Row("Ask before closing a pinned tab with Ctrl+W",
            "Ctrl+W on a pinned tab then only closes it when pressed a second time", protectPinned));
        content.Children.Add(TabsStyle.Row("Offer to switch to a tab that is already open",
            "When you open an address that another tab shows, Mosaic offers to go to that tab", offerSwitch));
        content.Children.Add(TabsStyle.Row("Remember recently closed", "Tabs, groups and workspaces Ctrl+Shift+T can bring back", recentLimit));
        content.Children.Add(TabsStyle.Row("Recently closed", null, recentPanel));

        content.Children.Add(TabsStyle.SectionHeader("Workspaces"));
        content.Children.Add(workspaceRows);
        content.Children.Add(newWorkspace);
        content.Children.Add(TabsStyle.Footnote(
            "Each workspace has its own tabs and groups. New tabs open in the current workspace. Switch with Ctrl+Alt+← and Ctrl+Alt+→, or from the workspace switcher."));

        Content = TabsStyle.Page("Tabs & Workspaces", content);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        model.PropertyChanged += ModelChanged;
        model.RecentlyClosedChanged += Refresh;
        model.TabOrganizationChanged += RefreshCounts;
        model.Workspaces.CollectionChanged += WorkspacesChanged;
        RebuildWorkspaces();
        Refresh();
    }

    private void Detach()
    {
        model.PropertyChanged -= ModelChanged;
        model.RecentlyClosedChanged -= Refresh;
        model.TabOrganizationChanged -= RefreshCounts;
        model.Workspaces.CollectionChanged -= WorkspacesChanged;
        ReleaseWorkspaceHandlers();
    }

    private void ReleaseWorkspaceHandlers()
    {
        foreach (var (workspace, handler) in workspaceHandlers) workspace.PropertyChanged -= handler;
        workspaceHandlers.Clear();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserModel.TabSettings) or nameof(BrowserModel.PreviousSession)) Refresh();
        if (e.PropertyName is nameof(BrowserModel.SelectedWorkspaceId)) RefreshCounts();
    }

    private void WorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildWorkspaces();

    private void Change(Func<TabSettings, TabSettings> change)
    {
        if (updating) return;
        model.TabSettings = change(model.TabSettings);
    }

    private void Refresh()
    {
        updating = true;
        try
        {
            var settings = model.TabSettings;
            startup.SelectedIndex = settings.Startup == TabStartup.StartPage ? 1 : 0;
            keepPinned.IsOn = settings.KeepPinnedTabs;
            keepPinned.IsEnabled = settings.Startup == TabStartup.StartPage;
            protectPinned.IsOn = settings.ProtectPinnedTabs;
            offerSwitch.IsOn = settings.OfferSwitchToOpenTab;
            recentLimit.Value = settings.RecentlyClosedLimit;
            recentCount.Text = model.RecentlyClosedItems.Count.ToString(global::System.Globalization.CultureInfo.CurrentCulture);
            clearRecent.IsEnabled = model.RecentlyClosedItems.Count > 0;
            previousSessionRow.Visibility = model.PreviousSession is null ? Visibility.Collapsed : Visibility.Visible;
            startupExplanation.Text = settings.Startup == TabStartup.PreviousSession
                ? "Tabs, groups and workspaces come back as you left them. Only the tab you were on loads; the others load when you select them. After a crash your tabs are restored the same way."
                : "Mosaic opens one new tab. Workspaces are kept, and Reopen All Tabs from Last Session brings the rest back. If Mosaic quit unexpectedly, it offers to restore your tabs.";
        }
        finally
        {
            updating = false;
        }
    }

    private void RebuildWorkspaces()
    {
        workspaceRows.Children.Clear();
        rows.Clear();
        ReleaseWorkspaceHandlers();
        foreach (var workspace in model.Workspaces) workspaceRows.Children.Add(WorkspaceRow(workspace));
        RefreshCounts();
    }

    private Border WorkspaceRow(Workspace workspace)
    {
        var icon = new ContentControl { Content = WorkspaceIcon(workspace) };
        var iconMenu = new MenuFlyout();
        foreach (var symbol in Workspace.Symbols)
        {
            var item = new MenuFlyoutItem { Text = symbol };
            if (TabsStyle.HasIcon(symbol)) item.Icon = new FontIcon { Glyph = Mosaic.App.Features.System.Icons.Glyph(symbol), FontFamily = Mosaic.App.Features.System.Icons.Font };
            item.Click += (_, _) => model.SetWorkspaceSymbol(workspace.Id, symbol);
            iconMenu.Items.Add(item);
        }
        TabsMenus.CoverWhileOpen(iconMenu, "tabs.settings.icon");
        var iconButton = new DropDownButton { Content = icon, Flyout = iconMenu };
        ToolTipService.SetToolTip(iconButton, "Choose an icon");
        AutomationProperties.SetName(iconButton, $"Icon for {workspace.Name}");

        var name = new TextBox { Text = workspace.Name, MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(name, $"Name of workspace {workspace.Name}");
        void Commit()
        {
            var trimmed = name.Text.Trim();
            if (trimmed.Length == 0) name.Text = workspace.Name;
            else if (trimmed != workspace.Name) model.RenameWorkspace(workspace.Id, trimmed);
        }
        name.LostFocus += (_, _) => Commit();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                name.Text = workspace.Name;
                e.Handled = true;
            }
        };
        PropertyChangedEventHandler changed = (_, e) =>
        {
            if (e.PropertyName == nameof(Workspace.Name) && name.FocusState == FocusState.Unfocused) name.Text = workspace.Name;
            if (e.PropertyName == nameof(Workspace.Symbol)) icon.Content = WorkspaceIcon(workspace);
        };
        workspace.PropertyChanged += changed;
        workspaceHandlers.Add((workspace, changed));

        var count = new TextBlock { Foreground = TabsStyle.Secondary, VerticalAlignment = VerticalAlignment.Center, MinWidth = 56 };
        var current = new TextBlock { Text = "Current", FontSize = 12, Foreground = TabsStyle.Secondary, VerticalAlignment = VerticalAlignment.Center };
        var switchButton = new Button { Content = "Switch" };
        AutomationProperties.SetName(switchButton, $"Switch to {workspace.Name}");
        switchButton.Click += (_, _) => model.SelectWorkspace(workspace.Id);
        var delete = new Button { Content = TabsStyle.Icon("trash", "Delete") };
        AutomationProperties.SetName(delete, $"Delete {workspace.Name}");
        delete.Click += async (_, _) => await TabsMenus.ConfirmAndDeleteWorkspaceAsync(model, workspace, XamlRoot);

        var grid = new Grid { ColumnSpacing = 10, MinHeight = 48 };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }
        UIElement[] cells = [iconButton, name, count, new Grid { Children = { current, switchButton } }, delete];
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i] is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn((FrameworkElement)cells[i], i);
            grid.Children.Add(cells[i]);
        }
        rows[workspace.Id] = (name, count, switchButton, current, delete, icon);
        return TabsStyle.CardOf(grid);
    }

    private static FrameworkElement WorkspaceIcon(Workspace workspace) =>
        TabsStyle.Icon(workspace.Symbol, workspace.Name.Length > 0 ? workspace.Name[..1].ToUpperInvariant() : "•");

    private void RefreshCounts()
    {
        var canDelete = model.Workspaces.Count > 1;
        foreach (var workspace in model.Workspaces)
        {
            if (!rows.TryGetValue(workspace.Id, out var row)) continue;
            row.Count.Text = TabsStyle.Plural(model.TabsIn(workspace.Id).Count, "tab", "tabs");
            var isCurrent = workspace.Id == model.SelectedWorkspaceId;
            row.Current.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
            row.Switch.Visibility = isCurrent ? Visibility.Collapsed : Visibility.Visible;
            row.Delete.IsEnabled = canDelete;
            ToolTipService.SetToolTip(row.Delete, canDelete ? "Delete workspace" : "The last workspace can’t be deleted");
        }
    }
}
