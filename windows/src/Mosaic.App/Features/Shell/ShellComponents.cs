using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.Core.Browser;
using Mosaic.Core.Search;
using Mosaic.Core.Shell;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// Draws one <see cref="ShellComponent"/> (port of macOS's <c>ShellComponentView</c>). Another
/// area's view registered in <c>FeatureMounts.Components</c> under the component's id is used
/// when present; otherwise the shell's own view, or a placeholder for components only another
/// area can provide.
/// </summary>
internal sealed class ShellComponents(ShellRoot shell)
{
    private BrowserModel Model => shell.Model;
    private ShellTheme Theme => shell.Theme;

    /// <summary>Components drawn as placeholders in the current build (for <c>dump</c>).</summary>
    public HashSet<string> Placeholders { get; } = [];

    public FrameworkElement Make(RegionItem item, ShellRegion region)
    {
        var component = item.Component;
        if (component != ShellComponent.FlexibleSpace
            && FeatureMounts.Components.TryGetValue(component.MountId(), out var mounted))
        {
            try
            {
                if (mounted(Model) is FrameworkElement view) return view;
            }
            catch (Exception error)
            {
                Placeholders.Add(component.MountId());
                return Placeholder(component, $"{component.Title()} failed to load: {error.Message}");
            }
        }
        var m = Theme.Metrics;
        var tab = Model.SelectedTab;
        switch (component)
        {
            case ShellComponent.Back:
                return Bound(ShellUi.IconButton(Theme, "chevron.left", "Back", "Alt+Left", Model.GoBack),
                    b => b.IsEnabled = Model.CanGoBack, nameof(BrowserModel.CanGoBack));
            case ShellComponent.Forward:
                return Bound(ShellUi.IconButton(Theme, "chevron.right", "Forward", "Alt+Right", Model.GoForward),
                    b => b.IsEnabled = Model.CanGoForward, nameof(BrowserModel.CanGoForward));
            case ShellComponent.Reload:
                return ReloadButton();
            case ShellComponent.Home:
                return ShellUi.IconButton(Theme, "house",
                    Model.ShellSettings.Homepage.Length == 0 ? "Home (New Tab page)" : "Home", "Alt+Home", Model.GoHome);
            case ShellComponent.AddressField:
                var style = region.IsSidebar() ? AddressBarStyle.Compact : Theme.Layout.AddressBar;
                var field = new AddressField(shell, style, item.EmbedsStar);
                shell.RegisterAddressField(field);
                return field;
            case ShellComponent.TabStrip:
                return new HorizontalTabStrip(shell, Theme.Layout.TabStyle, item.AttachesNewTab);
            case ShellComponent.SidebarToggle:
                var trailing = Theme.Layout.TabPlacement == TabPlacement.SidebarTrailing;
                var label = Model.IsSidebarVisible ? "Hide Sidebar" : "Show Sidebar";
                return ShellUi.IconButton(Theme, trailing ? "sidebar.right" : "sidebar.left", label, "Ctrl+Shift+L", Model.ToggleSidebar,
                    enabled: Theme.Layout.Sidebar != SidebarBehavior.Shown || !Theme.Layout.TabPlacement.IsVertical());
            case ShellComponent.VerticalTabs:
                var vertical = Theme.Layout.TabPlacement.IsVertical();
                var toggle = new ToggleButton
                {
                    Content = ShellUi.Icon("sidebar.squares.left", m.Icon),
                    Width = m.Button,
                    Height = m.Button,
                    IsChecked = vertical,
                }.Subtle(Theme, Math.Clamp(Theme.CornerRadius, 4, 10));
                toggle.Click += (_, _) => Model.ToggleVerticalTabs();
                return toggle.Named(vertical ? "Turn off vertical tabs" : "Turn on vertical tabs",
                    (vertical ? "Turn off vertical tabs" : "Turn on vertical tabs") + " (Ctrl+Shift+,)");
            case ShellComponent.NewTab:
                return ShellUi.IconButton(Theme, "plus", "New Tab", "Ctrl+T", Model.OpenNewTabFromShell);
            case ShellComponent.Downloads:
                return Placeholder(component, "Downloads", () => Model.ShowSettings(SettingsCategory.Downloads));
            case ShellComponent.Blocker:
                return BlockerButton();
            case ShellComponent.Extensions:
                return ExtensionsButton(region);
            case ShellComponent.Settings:
                return ShellUi.IconButton(Theme, "gearshape", "Settings", "Ctrl+,", Model.ToggleSettings);
            case ShellComponent.BookmarkStar:
                return Placeholder(component, "Bookmark this page");
            case ShellComponent.EngineSwitch:
                return EngineSwitch(showsName: !region.IsSidebar() && !region.IsRail() && m.Density != ShellDensity.Compact);
            case ShellComponent.SearchEngine:
                return SearchEnginePicker();
            case ShellComponent.Workspaces:
                return Placeholder(component, "Workspaces", () => Model.ShowSettings(SettingsCategory.TabsAndWorkspaces));
            case ShellComponent.Menu:
                return MenuButton();
            case ShellComponent.FlexibleSpace:
                return ShellUi.Gap();
            case ShellComponent.BookmarksPage:
                return PageButton("book", "Bookmarks", "bookmarks", "Ctrl+Shift+O");
            case ShellComponent.HistoryPage:
                return PageButton("clock", "History", "history", "Ctrl+H");
            case ShellComponent.ReadingListPage:
                return PageButton("eyeglasses", "Reading List", "readinglist", null);
            case ShellComponent.DownloadsPage:
                return PageButton("arrow.down.doc", "Downloads", "downloads", "Ctrl+J");
            default:
                _ = tab;
                return Placeholder(component, component.Title());
        }
    }

    /// <summary>Keeps a view in step with model properties while it is in the window.</summary>
    private T Bound<T>(T element, Action<T> update, params string[] properties) where T : FrameworkElement
    {
        update(element);
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName is null || properties.Contains(e.PropertyName)) update(element);
        };
        element.Loaded += (_, _) => Model.PropertyChanged += handler;
        element.Unloaded += (_, _) => Model.PropertyChanged -= handler;
        if (element.IsLoaded) Model.PropertyChanged += handler;
        return element;
    }

    /// <summary>
    /// Stands in for a component another area provides (registered in <c>FeatureMounts.Components</c>):
    /// its icon, and its settings page if it has one, so the layout still reads correctly.
    /// </summary>
    private FrameworkElement Placeholder(ShellComponent component, string label, Action? action = null)
    {
        Placeholders.Add(component.MountId());
        var button = ShellUi.IconButton(Theme, component.Symbol(), label, null, action ?? (() => { }), enabled: action is not null);
        if (action is null) ToolTipService.SetToolTip(button, $"{label} (not available yet)");
        return button;
    }

    private FrameworkElement ReloadButton()
    {
        var button = ShellUi.IconButton(Theme, "arrow.clockwise", "Reload", "F5", Model.ReloadOrStop);
        return Bound(button, b =>
        {
            var loading = Model.IsLoading;
            b.Content = ShellUi.Icon(loading ? "xmark" : "arrow.clockwise", Theme.Metrics.Icon);
            b.IsEnabled = loading || Model.CanReload;
            var text = loading ? "Stop (Esc)" : "Reload (F5)";
            AutomationProperties.SetName(b, loading ? "Stop" : "Reload");
            ToolTipService.SetToolTip(b, text);
        }, nameof(BrowserModel.IsLoading), nameof(BrowserModel.CanReload));
    }

    private FrameworkElement PageButton(string symbol, string title, string host, string? shortcut) =>
        ShellUi.IconButton(Theme, symbol, title, shortcut, () => Model.OpenInternalPage(host));

    private FrameworkElement EngineSwitch(bool showsName)
    {
        var m = Theme.Metrics;
        var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center };
        var name = ShellUi.Text("", m.SmallFont + 0.5);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(dot);
        if (showsName) panel.Children.Add(name);
        var button = new Button
        {
            Content = panel,
            Height = m.Button,
            MinWidth = m.Button,
        }.Subtle(Theme, Math.Clamp(Theme.CornerRadius, 4, 10));
        button.Padding = new Thickness(showsName ? 10 : 0, 0, showsName ? 10 : 0, 0);
        button.Click += (_, _) => Model.ToggleEngine();
        return Bound(button, b =>
        {
            var engine = Model.SelectedTab?.Engine ?? Model.Engine;
            dot.Fill = new SolidColorBrush(ShellTheme.EngineTint(engine));
            name.Text = engine.DisplayName();
            var other = engine.Other().DisplayName();
            AutomationProperties.SetName(b, $"Engine: {engine.DisplayName()}");
            AutomationProperties.SetHelpText(b, $"Switch this tab to {other}");
            ToolTipService.SetToolTip(b, $"Rendered by {engine.DisplayName()} ({engine.EngineName()}). Switch to {other} (Ctrl+Shift+E)");
        }, nameof(BrowserModel.Engine), nameof(BrowserModel.SelectedTab), nameof(BrowserModel.Content));
    }

    private FrameworkElement SearchEnginePicker()
    {
        var m = Theme.Metrics;
        var flyout = new MenuFlyout().CoversPage("shell.searchEngine");
        var button = new Button
        {
            Content = ShellUi.Icon("text.magnifyingglass", m.Icon),
            Width = m.Button,
            Height = m.Button,
            Flyout = flyout,
        }.Subtle(Theme, Math.Clamp(Theme.CornerRadius, 4, 10));
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            flyout.Items.Add(new MenuFlyoutItem { Text = "Search with", IsEnabled = false });
            foreach (var engine in SearchEngine.BuiltIn)
            {
                var item = new RadioMenuFlyoutItem { Text = engine.Name, GroupName = "shell.searchEngine", IsChecked = engine.Id == Model.SearchEngine.Id };
                item.Click += (_, _) => Model.SearchEngine = engine;
                flyout.Items.Add(item);
            }
            flyout.Items.Add(new MenuFlyoutSeparator());
            var more = new MenuFlyoutItem { Text = "Search settings…" };
            more.Click += (_, _) => Model.ShowSettings(SettingsCategory.Search);
            flyout.Items.Add(more);
        };
        return Bound(button, b =>
        {
            AutomationProperties.SetName(b, $"Search engine: {Model.SearchEngine.Name}");
            ToolTipService.SetToolTip(b, $"Search engine: {Model.SearchEngine.Name}");
        }, nameof(BrowserModel.SearchEngine));
    }

    private FrameworkElement BlockerButton()
    {
        var m = Theme.Metrics;
        var icon = ShellUi.Icon("shield.lefthalf.filled", m.Icon);
        var badge = ShellUi.Text("", 9.5 * m.Scale, Theme.AccentText, semibold: true);
        badge.Margin = new Thickness(0, 0, 0, 0);
        var grid = new Grid();
        grid.Children.Add(icon);
        var badgeHost = new Border
        {
            Child = badge,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -6, -5),
        };
        grid.Children.Add(badgeHost);

        var enabled = new ToggleSwitch { Header = "Content blocker", OnContent = "On", OffContent = "Off" }.Named("Content blocker");
        var site = new ToggleSwitch { OnContent = "On", OffContent = "Off" };
        var count = ShellUi.Text("", 12, Theme.SecondaryText);
        count.TextWrapping = TextWrapping.Wrap;
        var panel = new StackPanel { Spacing = 10, Width = 260 };
        panel.Children.Add(ShellUi.Text("Content blocker", 15, semibold: true));
        panel.Children.Add(enabled);
        panel.Children.Add(site);
        panel.Children.Add(count);
        var settings = new HyperlinkButton { Content = "Privacy & Security settings…" };
        settings.Click += (_, _) => Model.ShowSettings(SettingsCategory.Privacy);
        panel.Children.Add(settings);
        var flyout = new Flyout { Content = panel }.CoversPage("shell.blocker");
        var updating = false;
        enabled.Toggled += (_, _) => { if (!updating) Model.SetBlockerEnabled(enabled.IsOn); };
        site.Toggled += (_, _) =>
        {
            if (!updating && Model.SelectedTab is { } tab) Model.SetBlocking(site.IsOn, tab);
        };
        flyout.Opening += (_, _) =>
        {
            updating = true;
            enabled.IsOn = Model.Blocker.IsEnabled;
            site.Visibility = Model.HasBlockerSite ? Visibility.Visible : Visibility.Collapsed;
            site.Header = Model.HasBlockerSite ? Model.BlockerSiteLabel : "";
            AutomationProperties.SetName(site, site.Header?.ToString() ?? "This site");
            site.IsOn = Model.IsBlockingSite;
            site.IsEnabled = Model.Blocker.IsEnabled;
            count.Text = Model.BlockedCountText;
            updating = false;
        };
        var button = new Button { Content = grid, Width = m.Button, Height = m.Button, Flyout = flyout }
            .Subtle(Theme, Math.Clamp(Theme.CornerRadius, 4, 10));
        return Bound(button, b =>
        {
            icon.Opacity = Model.BlockerShield == BlockerShield.Off ? 0.45 : 1;
            icon.Foreground = Model.BlockerShield == BlockerShield.Filtering ? Theme.AccentText : null;
            badge.Text = Model.BlockerBadge;
            var help = Model.BlockerHelp.Length > 0 ? Model.BlockerHelp : "Content blocker";
            AutomationProperties.SetName(b, "Content blocker");
            AutomationProperties.SetHelpText(b, help);
            ToolTipService.SetToolTip(b, help + " (Ctrl+Shift+B)");
        }, nameof(BrowserModel.BlockerShield), nameof(BrowserModel.BlockerBadge), nameof(BrowserModel.BlockerHelp));
    }

    private FrameworkElement ExtensionsButton(ShellRegion region)
    {
        var panel = new StackPanel { Orientation = region.IsRail() ? Orientation.Vertical : Orientation.Horizontal, Spacing = 2 };
        // Platform area's per-extension buttons, then the manager.
        if (!region.IsRail() && FeatureMounts.Components.TryGetValue("extensionButtons", out var buttons))
        {
            try
            {
                if (buttons(Model) is FrameworkElement view) panel.Children.Add(view);
            }
            catch
            {
                // The manager button below still works.
            }
        }
        panel.Children.Add(ShellUi.IconButton(Theme, "puzzlepiece.extension", "Extensions", "Ctrl+Shift+X",
            () => Model.ShowSettings(SettingsCategory.Extensions)));
        return panel;
    }

    private FrameworkElement MenuButton()
    {
        var m = Theme.Metrics;
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight }.CoversPage("shell.menu");
        var edge = Theme.Layout.Preset == MosaicPresetID.Edge;
        var button = new Button
        {
            Content = ShellUi.Icon("ellipsis", m.Icon),
            Width = m.Button,
            Height = m.Button,
            Flyout = flyout,
        }.Subtle(Theme, Math.Clamp(Theme.CornerRadius, 4, 10));
        var label = edge ? "Settings and more" : "Customise and control Mosaic";
        button.Named(label, $"{label} (Alt+F)");
        flyout.Opening += (_, _) => FillMenu(flyout.Items);
        shell.RegisterMenuButton(button);
        return button;
    }

    /// <summary>Everything in one menu (Chrome's ⋮, Edge's …).</summary>
    internal void FillMenu(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        MenuFlyoutItem Item(string text, string? symbol, Action action, string? shortcut = null, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            if (symbol is not null) item.Icon = ShellUi.Icon(symbol, 16);
            if (shortcut is not null) item.KeyboardAcceleratorTextOverride = shortcut;
            item.Click += (_, _) => action();
            return item;
        }
        var store = Model.Mosaics;
        var layout = store.Layout;
        items.Add(Item("New tab", "plus", Model.OpenNewTabFromShell, "Ctrl+T"));
        items.Add(new MenuFlyoutSeparator());

        var mosaics = new MenuFlyoutSubItem { Text = "Mosaic", Icon = ShellUi.Icon("square.grid.2x2", 16) };
        foreach (var preset in Enum.GetValues<MosaicPresetID>())
        {
            var item = new RadioMenuFlyoutItem { Text = preset.Title(), GroupName = "shell.mosaic", IsChecked = store.ActivePreset == preset };
            item.Click += (_, _) => Model.ApplyMosaic(preset);
            mosaics.Items.Add(item);
        }
        mosaics.Items.Add(new MenuFlyoutSeparator());
        var gx = new ToggleMenuFlyoutItem { Text = "Opera GX styling", IsChecked = layout.HasGXStyling };
        gx.Click += (_, _) => store.ReplaceActiveLayout(gx.IsChecked ? store.Layout.WithGXStyling() : store.Layout.WithoutGXStyling());
        mosaics.Items.Add(gx);
        mosaics.Items.Add(Item("Customise…", "paintbrush", () => Model.ShowSettings(SettingsCategory.Mosaics)));
        items.Add(mosaics);

        var vertical = new ToggleMenuFlyoutItem { Text = "Vertical tabs", IsChecked = layout.TabPlacement.IsVertical(), KeyboardAcceleratorTextOverride = "Ctrl+Shift+," };
        vertical.Click += (_, _) => Model.ToggleVerticalTabs();
        items.Add(vertical);
        if (layout.HasSidebar)
        {
            items.Add(Item(Model.IsSidebarVisible ? "Hide sidebar" : "Show sidebar", "sidebar.left", Model.ToggleSidebar, "Ctrl+Shift+L"));
        }
        var bar = new ToggleMenuFlyoutItem { Text = "Bookmarks bar in this Mosaic", IsChecked = layout.BookmarksBar };
        bar.Click += (_, _) => store.Update(l => l.BookmarksBar = bar.IsChecked);
        items.Add(bar);
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item("Bookmarks", "book", () => Model.OpenInternalPage("bookmarks"), "Ctrl+Shift+O"));
        items.Add(Item("History", "clock", () => Model.OpenInternalPage("history"), "Ctrl+H"));
        items.Add(Item("Reading list", "eyeglasses", () => Model.OpenInternalPage("readinglist")));
        items.Add(Item("Downloads", "arrow.down.circle", () => Model.OpenInternalPage("downloads"), "Ctrl+J"));
        items.Add(new MenuFlyoutSeparator());
        var engine = Model.SelectedTab?.Engine ?? Model.Engine;
        items.Add(Item($"Open this tab in {engine.Other().DisplayName()}", "circle.lefthalf.filled", Model.ToggleEngine, "Ctrl+Shift+E"));
        var search = new MenuFlyoutSubItem { Text = "Search engine", Icon = ShellUi.Icon("magnifyingglass", 16) };
        foreach (var candidate in SearchEngine.BuiltIn)
        {
            var item = new RadioMenuFlyoutItem { Text = candidate.Name, GroupName = "shell.menuSearch", IsChecked = candidate.Id == Model.SearchEngine.Id };
            item.Click += (_, _) => Model.SearchEngine = candidate;
            search.Items.Add(item);
        }
        items.Add(search);
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item("Extensions", "puzzlepiece.extension", () => Model.ShowSettings(SettingsCategory.Extensions), "Ctrl+Shift+X"));
        items.Add(Item("Settings", "gearshape", () => Model.ShowSettings(), "Ctrl+,"));
    }
}
