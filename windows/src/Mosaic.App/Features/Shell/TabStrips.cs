using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;

namespace Mosaic.App.Features.Shell;

/// <summary>How a tab is drawn.</summary>
internal enum TabLook
{
    /// <summary>Chrome: shaped, the selected one merges into the toolbar below.</summary>
    Shaped,
    /// <summary>Opera and Edge: separate rounded tabs.</summary>
    Rounded,
    /// <summary>Equal segments filling the bar.</summary>
    Segmented,
    /// <summary>A row in the vertical tab list.</summary>
    Row,
    /// <summary>Just the icon (Edge's collapsed tab pane).</summary>
    Icon,
}

/// <summary>
/// One tab: its icon (the site's initial with the engine's dot, or a spinner while loading),
/// title and close button. The tab and its close button are separate controls so both are
/// reachable with the keyboard and named for Narrator. Middle-click closes; right-click opens
/// the tab menu.
/// </summary>
internal sealed class TabItemView : Grid
{
    private readonly ShellRoot shell;
    private readonly TabLook look;
    private readonly Border background = new();
    private readonly Button main;
    private readonly Button? close;
    private readonly TextBlock title;
    private readonly Border tile;
    private readonly TextBlock initial;
    private readonly Ellipse dot;
    private readonly ProgressRing spinner;
    private bool hovering;

    public BrowserTab Tab { get; }

    public TabItemView(ShellRoot shell, BrowserTab tab, TabLook look)
    {
        this.shell = shell;
        this.look = look;
        Tab = tab;
        var theme = shell.Theme;
        var m = theme.Metrics;
        var r = theme.CornerRadius;
        background.CornerRadius = look switch
        {
            TabLook.Shaped => new CornerRadius(Math.Max(r, 6), Math.Max(r, 6), 0, 0),
            TabLook.Row => new CornerRadius(Math.Max(r, 6)),
            _ => new CornerRadius(r),
        };
        Children.Add(background);

        initial = ShellUi.Text("", 10 * m.Scale, theme.AccentText, semibold: true);
        initial.HorizontalAlignment = HorizontalAlignment.Center;
        initial.TextTrimming = TextTrimming.None;
        tile = new Border
        {
            Width = 16 * m.Scale,
            Height = 16 * m.Scale,
            CornerRadius = new CornerRadius(4),
            Background = theme.AccentSubtle,
            Child = initial,
        };
        dot = new Ellipse { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2) };
        spinner = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed };
        var iconHost = new Grid { Width = 18 * m.Scale, Height = 18 * m.Scale, VerticalAlignment = VerticalAlignment.Center };
        iconHost.Children.Add(tile);
        iconHost.Children.Add(dot);
        iconHost.Children.Add(spinner);
        AutomationProperties.SetAccessibilityView(iconHost, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        title = ShellUi.Text("", m.Font - (look == TabLook.Row ? 0 : 0.5));
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(iconHost);
        if (look != TabLook.Icon) content.Children.Add(title);

        main = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = look is TabLook.Icon or TabLook.Segmented ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        }.Subtle(theme, background.CornerRadius.TopLeft);
        // The tab draws its own hover and selection.
        main.Resources["ButtonBackgroundPointerOver"] = theme.Transparent;
        main.Resources["ButtonBackgroundPressed"] = theme.Transparent;
        main.Padding = new Thickness(look == TabLook.Icon ? 0 : 10, 0, look == TabLook.Icon ? 0 : 30, 0);
        main.Click += (_, _) => shell.Model.Select(tab);
        Children.Add(main);

        if (look != TabLook.Icon)
        {
            close = new Button
            {
                Content = ShellUi.Icon("xmark", 10 * m.Scale),
                Width = 22 * m.Scale,
                Height = 22 * m.Scale,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            }.Subtle(theme, 5);
            close.Click += (_, _) => shell.Model.Close(tab);
            Children.Add(close);
        }

        PointerEntered += (_, _) => { hovering = true; Refresh(); };
        PointerExited += (_, _) => { hovering = false; Refresh(); };
        PointerPressed += OnPointerPressed;
        main.ContextFlyout = TabMenu();

        PropertyChangedEventHandler handler = (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            tab.PropertyChanged += handler;
            theme.SystemSettingsChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            tab.PropertyChanged -= handler;
            theme.SystemSettingsChanged -= Refresh;
            spinner.IsActive = false;
        };
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            e.Handled = true;
            shell.Model.Close(Tab);
        }
    }

    private MenuFlyout TabMenu()
    {
        var menu = new MenuFlyout().CoversPage("shell.tabMenu");
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var model = shell.Model;
            void Add(string text, string symbol, Action action, string? shortcut = null)
            {
                var item = new MenuFlyoutItem { Text = text, Icon = ShellUi.Icon(symbol, 16) };
                if (shortcut is not null) item.KeyboardAcceleratorTextOverride = shortcut;
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
            Add("New tab", "plus", model.OpenNewTabFromShell, "Ctrl+T");
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Reload", "arrow.clockwise", () => { model.Select(Tab); model.Reload(); }, "F5");
            Add($"Open in {Tab.Engine.Other().DisplayName()}", "circle.lefthalf.filled", () => { model.Select(Tab); model.ToggleEngine(); }, "Ctrl+Shift+E");
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Close tab", "xmark", () => model.Close(Tab), "Ctrl+W");
            Add("Close other tabs", "xmark", () =>
            {
                foreach (var other in model.Tabs.Where(t => !ReferenceEquals(t, Tab)).ToList()) model.Close(other);
            });
        };
        return menu;
    }

    public void Refresh()
    {
        var theme = shell.Theme;
        var tab = Tab;
        var selected = tab.IsSelected;
        title.Text = tab.DisplayTitle;
        title.Foreground = selected ? theme.PrimaryText : theme.SecondaryText;
        title.FontWeight = selected && look == TabLook.Row ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        var first = tab.DisplayTitle.FirstOrDefault(char.IsLetterOrDigit);
        initial.Text = tab.IsStartPage ? "+" : first != default ? char.ToUpperInvariant(first).ToString() : "\u2022";
        dot.Fill = new SolidColorBrush(ShellTheme.EngineTint(tab.Engine));
        spinner.IsActive = tab.IsLoading;
        spinner.Visibility = tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        tile.Visibility = tab.IsLoading ? Visibility.Collapsed : Visibility.Visible;
        background.Background = selected
            ? (look == TabLook.Shaped && theme.Layout.Appearance.ToolbarSurface ? theme.Surface : look == TabLook.Row ? theme.SidebarSelection : theme.SelectedTab)
            : hovering ? theme.Hover : theme.Transparent;
        if (look == TabLook.Shaped && selected && !theme.Layout.Appearance.ToolbarSurface) background.Background = theme.SelectedTab;
        if (close is not null)
        {
            close.Visibility = selected || hovering || theme.AlwaysShowTabCloseButtons || look == TabLook.Row && hovering
                ? Visibility.Visible : Visibility.Collapsed;
            if (ActualWidth > 0 && ActualWidth < 76 && !selected) close.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(close, $"Close {tab.DisplayTitle}");
            ToolTipService.SetToolTip(close, "Close tab (Ctrl+W)");
        }
        AutomationProperties.SetName(main, tab.AccessibleName);
        AutomationProperties.SetItemStatus(main, selected ? "Selected" : "");
        ToolTipService.SetToolTip(main, $"{tab.DisplayTitle}\n{tab.EngineHint}");
    }
}

/// <summary>
/// Horizontal tabs (port of macOS's <c>HorizontalTabStrip</c>): shaped, rounded or segmented,
/// with the new-tab button right after the last tab when the layout attaches it. Tabs share
/// the width, between a minimum and a maximum, and scroll when there are too many.
/// </summary>
internal sealed class HorizontalTabStrip : Grid
{
    private readonly ShellRoot shell;
    private readonly TabStyle style;
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer scroller;
    private readonly Button? newTab;
    private readonly Dictionary<BrowserTab, TabItemView> views = [];

    public HorizontalTabStrip(ShellRoot shell, TabStyle style, bool newTabAttached)
    {
        this.shell = shell;
        this.style = style;
        var m = shell.Theme.Metrics;
        tabs.Spacing = style == TabStyle.Chrome ? 0 : 4;
        scroller = new ScrollViewer
        {
            Content = tabs,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(this, "Tabs");
        AutomationProperties.SetLandmarkType(this, AutomationLandmarkType.Navigation);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        Children.Add(scroller);
        if (newTabAttached)
        {
            newTab = ShellUi.IconButton(shell.Theme, "plus", "New Tab", "Ctrl+T", shell.Model.OpenNewTabFromShell);
            newTab.Margin = new Thickness(4, 0, 0, 0);
            newTab.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(newTab, 1);
            Children.Add(newTab);
        }
        VerticalAlignment = style == TabStyle.Chrome ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        Height = style == TabStyle.Chrome ? m.TabHeight + 2 : m.TabHeight;

        NotifyCollectionChangedEventHandler changed = (_, _) => Rebuild();
        Loaded += (_, _) =>
        {
            shell.Model.Tabs.CollectionChanged += changed;
            Rebuild();
        };
        Unloaded += (_, _) => shell.Model.Tabs.CollectionChanged -= changed;
        SizeChanged += (_, _) => Size();
    }

    private void Rebuild()
    {
        var look = style switch { TabStyle.Chrome => TabLook.Shaped, TabStyle.Safari => TabLook.Segmented, _ => TabLook.Rounded };
        var current = shell.Model.Tabs.ToList();
        foreach (var gone in views.Keys.Except(current).ToList()) views.Remove(gone);
        tabs.Children.Clear();
        for (var i = 0; i < current.Count; i++)
        {
            if (!views.TryGetValue(current[i], out var view)) views[current[i]] = view = new TabItemView(shell, current[i], look);
            AutomationProperties.SetPositionInSet(view, i + 1);
            AutomationProperties.SetSizeOfSet(view, current.Count);
            tabs.Children.Add(view);
        }
        Size();
    }

    private void Size()
    {
        var count = Math.Max(1, tabs.Children.Count);
        var newTabWidth = newTab is null ? 0 : newTab.Width + 4;
        var available = Math.Max(0, ActualWidth - newTabWidth - (count - 1) * tabs.Spacing);
        var m = shell.Theme.Metrics;
        var (min, max) = style switch
        {
            TabStyle.Chrome => (56.0, 240.0),
            TabStyle.Safari => (90.0, double.PositiveInfinity),
            _ => (64.0, 228.0),
        };
        var width = Math.Clamp(Math.Floor(available / count), min * m.Scale, max * m.Scale);
        foreach (var child in tabs.Children.OfType<FrameworkElement>())
        {
            child.Width = width;
            child.Height = Height - (style == TabStyle.Chrome ? 0 : 0);
        }
        // The strip takes what the tabs need, so the new-tab button sits right after them.
        scroller.MaxWidth = Math.Max(0, ActualWidth - newTabWidth);
        ColumnDefinitions[0].Width = width * count + (count - 1) * tabs.Spacing <= scroller.MaxWidth
            ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        foreach (var view in tabs.Children.OfType<TabItemView>()) view.Refresh();
    }
}

/// <summary>The sidebar's tab list (port of macOS's <c>VerticalTabList</c>): a New Tab row, then one row per tab.</summary>
internal sealed class VerticalTabList : Grid
{
    private readonly ShellRoot shell;
    private readonly StackPanel rows = new() { Spacing = 2, Padding = new Thickness(8, 4, 8, 8) };
    private readonly Dictionary<BrowserTab, TabItemView> views = [];
    private readonly Button newTab;

    public VerticalTabList(ShellRoot shell)
    {
        this.shell = shell;
        var theme = shell.Theme;
        var m = theme.Metrics;
        Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled });
        AutomationProperties.SetName(this, "Tabs");
        AutomationProperties.SetLandmarkType(this, AutomationLandmarkType.Navigation);
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        label.Children.Add(ShellUi.Icon("plus", m.Icon - 1, theme.SecondaryText));
        label.Children.Add(ShellUi.Text("New Tab", m.Font, theme.SecondaryText));
        newTab = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Height = m.SidebarRowHeight,
        }.Subtle(theme, Math.Max(theme.CornerRadius, 6));
        newTab.Padding = new Thickness(11, 0, 8, 0);
        newTab.Click += (_, _) => shell.Model.OpenNewTabFromShell();
        newTab.Named("New Tab", "New Tab (Ctrl+T)");
        NotifyCollectionChangedEventHandler changed = (_, _) => Rebuild();
        Loaded += (_, _) =>
        {
            shell.Model.Tabs.CollectionChanged += changed;
            Rebuild();
        };
        Unloaded += (_, _) => shell.Model.Tabs.CollectionChanged -= changed;
    }

    private void Rebuild()
    {
        var m = shell.Theme.Metrics;
        var current = shell.Model.Tabs.ToList();
        foreach (var gone in views.Keys.Except(current).ToList()) views.Remove(gone);
        rows.Children.Clear();
        rows.Children.Add(newTab);
        for (var i = 0; i < current.Count; i++)
        {
            if (!views.TryGetValue(current[i], out var view))
            {
                views[current[i]] = view = new TabItemView(shell, current[i], TabLook.Row) { Height = m.SidebarRowHeight };
            }
            AutomationProperties.SetPositionInSet(view, i + 1);
            AutomationProperties.SetSizeOfSet(view, current.Count);
            rows.Children.Add(view);
        }
    }
}

/// <summary>Edge's collapsed vertical tab pane: the expand button, New Tab, and one icon per tab.</summary>
internal sealed class TabIconStrip : Grid
{
    private readonly ShellRoot shell;
    private readonly StackPanel icons = new() { Spacing = 4, Padding = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };

    public TabIconStrip(ShellRoot shell)
    {
        this.shell = shell;
        var theme = shell.Theme;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        top.Children.Add(ShellUi.IconButton(theme, "sidebar.left", "Expand the tab pane", "Ctrl+Shift+L", shell.Model.ToggleSidebar));
        top.Children.Add(ShellUi.IconButton(theme, "plus", "New Tab", "Ctrl+T", shell.Model.OpenNewTabFromShell));
        Children.Add(top);
        var scroller = new ScrollViewer { Content = icons, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        SetRow(scroller, 1);
        Children.Add(scroller);
        AutomationProperties.SetName(this, "Tabs");
        AutomationProperties.SetLandmarkType(this, AutomationLandmarkType.Navigation);
        NotifyCollectionChangedEventHandler changed = (_, _) => Rebuild();
        Loaded += (_, _) =>
        {
            shell.Model.Tabs.CollectionChanged += changed;
            Rebuild();
        };
        Unloaded += (_, _) => shell.Model.Tabs.CollectionChanged -= changed;
    }

    private void Rebuild()
    {
        var m = shell.Theme.Metrics;
        icons.Children.Clear();
        foreach (var tab in shell.Model.Tabs)
        {
            icons.Children.Add(new TabItemView(shell, tab, TabLook.Icon) { Width = m.Button + 4, Height = m.Button + 4 });
        }
    }
}
