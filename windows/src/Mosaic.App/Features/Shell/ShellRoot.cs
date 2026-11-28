using System.ComponentModel;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;
using Windows.Foundation;
using Windows.System;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// The whole window, composed from the active Mosaic (port of macOS's <c>ShellView</c>):
/// title row, main header rows (title strip, toolbar, tab bar, bookmarks bar), Opera's rail,
/// the sidebar, Edge's right-hand sidebar, and the page area with Settings, Mosaic's own pages,
/// the overlays and notices on top of it. Registered as <c>FeatureMounts.Shell</c>.
/// </summary>
/// <remarks>
/// The grid's structure never changes and the page area (the winui area's element, holding the
/// engines' views) is never removed from it: switching Mosaics only changes column widths,
/// margins and the chrome around it, so pages keep running and nothing reloads. The chrome is
/// rebuilt (cheaply, coalesced) when the layout or a shell setting changes; Settings stay put.
/// <code>
///            col 0 rail | 1 leading sidebar | 2 main            | 3 right rail | 4 trailing sidebar
/// row 0      title row (all columns)
/// row 1      rail       | sidebar           | header (2…3)                    | sidebar
/// row 2      rail       | sidebar           | page + Settings…  | right rail   | sidebar
/// </code>
/// </remarks>
public sealed class ShellRoot : Grid
{
    private const int RailColumn = 0, LeadingSidebarColumn = 1, MainColumn = 2, TrailingRailColumn = 3, TrailingSidebarColumn = 4;

    private readonly Border backgroundLayer = new();
    private readonly Grid titleRowHost = new();
    private readonly StackPanel headerHost = new();
    private readonly Border railHost = new();
    private readonly Border leadingSidebarHost = new();
    private readonly Border trailingRailHost = new();
    private readonly Border trailingSidebarHost = new();
    private readonly Border pageHost = new();
    private readonly Border glow = new() { IsHitTestVisible = false };
    private readonly Border settingsHost = new() { Visibility = Visibility.Collapsed };
    private readonly Border internalHost = new() { Visibility = Visibility.Collapsed };
    private readonly Grid overlaysHost = new();
    private readonly Border noticeHost = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock noticeText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
    private readonly Grid peekHost = new();
    private readonly TitleBarBridge titleBar;
    private readonly List<WeakReference<AddressField>> addressFields = [];
    private Button? menuButton;
    private SettingsView? settingsView;
    private string? internalHostShown;
    private bool rebuildScheduled;
    private string? backdropKind;
    private ShellComponents components;

    public BrowserModel Model { get; }
    public Window Window { get; }
    public ShellTheme Theme { get; } = new();
    public ShellPlan Plan { get; private set; }

    /// <summary>Shown instead of the Mosaic (screenshots of every preset without touching the user's layouts).</summary>
    internal MosaicLayout? LayoutOverride { get; set; }

    /// <summary>Light or dark instead of the layout's own (screenshots).</summary>
    internal ColorSchemePreference? SchemeOverride { get; set; }

    /// <summary>Components drawn as placeholders (no other area has provided them yet).</summary>
    public IReadOnlyCollection<string> Placeholders => components.Placeholders;

    public int RebuildCount { get; private set; }

    public double TitleBarCaptionHeight => titleBar.CaptionHeight;

    public int TitleBarPassthroughCount => titleBar.PassthroughCount;

    public string BackdropKind => window.EffectiveMaterial ?? backdropKind ?? "Solid";

    private readonly WindowApi window;

    public ShellRoot(BrowserModel model, Window window)
    {
        Model = model;
        Window = window;
        titleBar = new TitleBarBridge(window);
        this.window = new WindowApi(window);
        this.window.OnMaterialChanged(ScheduleRebuild);
        components = new ShellComponents(this);
        ShellUi.RegisterGlyphs();
        AutomationProperties.SetName(this, "Mosaic");

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Place(backgroundLayer, 0, 0, rowSpan: 3, columnSpan: 5);
        Place(titleRowHost, 0, 0, columnSpan: 5);
        Place(railHost, 1, RailColumn, rowSpan: 2);
        Place(leadingSidebarHost, 1, LeadingSidebarColumn, rowSpan: 2);
        Place(headerHost, 1, MainColumn, columnSpan: 2);
        Place(trailingRailHost, 2, TrailingRailColumn);
        Place(trailingSidebarHost, 1, TrailingSidebarColumn, rowSpan: 2);
        Place(glow, 2, MainColumn);
        pageHost.Child = PageArea.Resolve(window, model);
        AutomationProperties.SetName(pageHost, "Page");
        AutomationProperties.SetLandmarkType(pageHost, AutomationLandmarkType.Main);
        Place(pageHost, 2, MainColumn);
        Place(settingsHost, 2, MainColumn);
        Place(internalHost, 2, MainColumn);
        Place(overlaysHost, 2, MainColumn);
        BuildNotice();
        Place(noticeHost, 2, MainColumn);
        Place(peekHost, 1, 0, rowSpan: 2, columnSpan: 5);
        peekHost.IsHitTestVisible = true;
        BuildOverlays();

        titleRowHost.Name = "TitleRow";
        AutomationProperties.SetName(titleRowHost, "Title bar");

        Plan = Rebuild();

        Model.ShellChanged += ScheduleRebuild;
        Model.PropertyChanged += OnModelChanged;
        Model.AddressFocusRequested += FocusAddressField;
        Model.Tabs.CollectionChanged += (_, _) => titleBar.Schedule();
        Theme.SystemSettingsChanged += () => DispatcherQueue.TryEnqueue(ScheduleRebuild);
        ActualThemeChanged += (_, _) => ScheduleRebuild();
        Loaded += (_, _) =>
        {
            ScheduleRebuild();
            UpdateOverlays();
        };
        KeyDown += OnKeyDown;
        AddAccelerators();
    }

    private void Place(FrameworkElement element, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        SetRow(element, row);
        SetColumn(element, column);
        SetRowSpan(element, rowSpan);
        SetColumnSpan(element, columnSpan);
        Children.Add(element);
    }

    // MARK: Rebuilding

    /// <summary>Rebuilds the chrome once, soon, however many changes arrive meanwhile.</summary>
    public void ScheduleRebuild()
    {
        if (rebuildScheduled) return;
        rebuildScheduled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            rebuildScheduled = false;
            Plan = Rebuild();
        });
    }

    /// <summary>The layout shown now: a screenshot override, a Settings preview, or the active Mosaic.</summary>
    public MosaicLayout EffectiveLayout => LayoutOverride ?? Model.Mosaics.EffectiveLayout;

    public bool IsDark { get; private set; }

    private ShellPlan Rebuild()
    {
        RebuildCount++;
        var layout = EffectiveLayout;
        var settings = Model.ShellSettings;
        var scheme = SchemeOverride ?? layout.Appearance.ColorScheme;
        RequestedTheme = scheme switch
        {
            ColorSchemePreference.Light => ElementTheme.Light,
            ColorSchemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        IsDark = scheme switch
        {
            ColorSchemePreference.Light => false,
            ColorSchemePreference.Dark => true,
            _ => IsLoaded ? ActualTheme == ElementTheme.Dark : Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
        Theme.Update(layout, settings, IsDark);
        Theme.ApplyResourceOverrides(Resources);
        components = new ShellComponents(this);
        addressFields.Clear();
        menuButton = null;

        var plan = new ShellPlan(layout, settings.ShowsHomeButton, Model.IsSidebarVisible, Theme.Metrics);
        ApplyBackdrop(plan);
        ApplyBackground(layout);

        var caption = titleBar.CaptionInset;
        ColumnDefinitions[RailColumn].Width = new GridLength(plan.RailWidth);
        var leading = plan.SidebarVisible && !plan.SidebarTrailing ? plan.SidebarWidth
            : plan.SidebarIconStrip && !plan.SidebarTrailing ? Theme.Metrics.IconStripWidth : 0;
        var trailing = plan.SidebarVisible && plan.SidebarTrailing ? plan.SidebarWidth
            : plan.SidebarIconStrip && plan.SidebarTrailing ? Theme.Metrics.IconStripWidth : 0;
        ColumnDefinitions[LeadingSidebarColumn].Width = new GridLength(leading);
        ColumnDefinitions[TrailingSidebarColumn].Width = new GridLength(trailing);
        ColumnDefinitions[TrailingRailColumn].Width = new GridLength(plan.TrailingRailWidth);

        FrameworkElement captionRow;
        // Title row.
        titleRowHost.Children.Clear();
        titleRowHost.Background = null;
        if (plan.TitleItems.Count > 0)
        {
            var isTabRow = plan.TitleItems.Any(i => i.Component == ShellComponent.TabStrip) && layout.TabStyle == TabStyle.Chrome;
            var row = RegionRow(ShellRegion.TitleBar, plan.TitleItems);
            row.Margin = new Thickness(isTabRow ? 8 : 6, 0, caption + 6, 0);
            row.VerticalAlignment = isTabRow ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            titleRowHost.Children.Add(row);
            titleRowHost.Height = plan.TitleRowHeight;
            if (layout.Appearance.ToolbarSurface && layout.Appearance.Background.Kind == BackgroundKind.System) titleRowHost.Background = Theme.Frame;
            else titleRowHost.Background = Theme.Transparent;
            titleRowHost.Visibility = Visibility.Visible;
            captionRow = titleRowHost;
        }
        else
        {
            titleRowHost.Visibility = Visibility.Collapsed;
            captionRow = titleRowHost;
        }

        // Header rows of the main column.
        headerHost.Children.Clear();
        if (plan.ShowsTitleStrip)
        {
            var strip = new Grid { Height = plan.TitleStripHeight, Background = Theme.Transparent };
            AutomationProperties.SetName(strip, "Title bar");
            if (plan.CollapsedStripItems.Count > 0)
            {
                var row = RegionRow(ShellRegion.SidebarMiddle, plan.CollapsedStripItems);
                row.MaxWidth = 900;
                row.HorizontalAlignment = HorizontalAlignment.Left;
                row.Margin = new Thickness(8, 0, caption + 8, 0);
                strip.Children.Add(row);
            }
            headerHost.Children.Add(strip);
            if (plan.CaptionHost == CaptionHost.TitleStrip) captionRow = strip;
        }
        if (plan.ToolbarItems.Count > 0)
        {
            var toolbar = new Grid { Height = Theme.Metrics.RowHeight };
            var row = RegionRow(ShellRegion.Toolbar, plan.ToolbarItems);
            var holdsCaptions = plan.CaptionHost == CaptionHost.Toolbar;
            row.Margin = new Thickness(8, 0, holdsCaptions ? caption + 8 : 8, 0);
            toolbar.Children.Add(row);
            toolbar.Background = layout.Appearance.ToolbarSurface ? Theme.Surface : Theme.Transparent;
            AutomationProperties.SetName(toolbar, "Toolbar");
            AutomationProperties.SetLandmarkType(toolbar, AutomationLandmarkType.Navigation);
            headerHost.Children.Add(toolbar);
            if (holdsCaptions) captionRow = toolbar;
        }
        if (plan.TabBarItems.Count > 0)
        {
            var bar = new Grid { Height = plan.TabBarHeight, Padding = new Thickness(8, 0, 8, 4) };
            bar.Children.Add(RegionRow(ShellRegion.TabBar, plan.TabBarItems));
            headerHost.Children.Add(bar);
        }
        if (layout.BookmarksBar)
        {
            var bookmarks = BookmarksBar();
            if (layout.Appearance.ToolbarSurface && bookmarks is Panel p) p.Background = Theme.Surface;
            headerHost.Children.Add(bookmarks);
        }
        var inset = layout.Appearance.ContentInset;
        if (inset == 0 && (plan.HasRowAbovePage || layout.BookmarksBar)) headerHost.Children.Add(ShellUi.Divider(Theme, vertical: false));

        // Rails.
        railHost.Child = plan.RailItems.Count > 0 ? RegionColumn(ShellRegion.Rail, plan.RailItems) : null;
        trailingRailHost.Child = plan.TrailingRailItems.Count > 0 ? RegionColumn(ShellRegion.TrailingRail, plan.TrailingRailItems) : null;
        trailingRailHost.Padding = new Thickness(0, 0, 0, inset);

        // Sidebar.
        leadingSidebarHost.Child = null;
        trailingSidebarHost.Child = null;
        var sidebarHost = plan.SidebarTrailing ? trailingSidebarHost : leadingSidebarHost;
        if (plan.SidebarVisible)
        {
            var holdsCaptions = plan.CaptionHost == CaptionHost.TrailingSidebar;
            var sidebar = Sidebar(plan, holdsCaptions ? caption : 0, out var topRow);
            sidebarHost.Child = Divided(sidebar, plan.SidebarTrailing, inset == 0);
            if (holdsCaptions) captionRow = topRow;
        }
        else if (plan.SidebarIconStrip)
        {
            sidebarHost.Child = Divided(new TabIconStrip(this), plan.SidebarTrailing, inset == 0);
        }

        // Page card.
        var top = plan.HasRowAbovePage || layout.BookmarksBar ? (inset > 0 ? Math.Max(2, inset / 2) : 0) : inset;
        var margin = new Thickness(inset, top, inset, inset);
        var radius = new CornerRadius(layout.Appearance.ContentCornerRadius);
        foreach (var host in new[] { pageHost, settingsHost, internalHost, glow })
        {
            host.Margin = margin;
            host.CornerRadius = radius;
        }
        glow.BorderThickness = new Thickness(layout.Appearance.Glow && !Theme.HighContrast ? 2 : 0);
        glow.BorderBrush = new SolidColorBrush(ShellTheme.WithAlpha(Theme.AccentColor, 0.7));
        glow.Margin = new Thickness(margin.Left - 2, margin.Top - 2, margin.Right - 2, margin.Bottom - 2);
        glow.CornerRadius = new CornerRadius(layout.Appearance.ContentCornerRadius + 2);
        pageHost.Background = Theme.PageBackground;
        settingsHost.Background = Theme.PageBackground;
        internalHost.Background = Theme.PageBackground;

        BuildPeek(plan);
        titleBar.Attach(captionRow, plan.CaptionRowHeight, Theme);
        return plan;
    }

    private void ApplyBackdrop(ShellPlan plan)
    {
        var kind = !Theme.Translucent ? "Solid" : plan.TitleItems.Any(i => i.Component == ShellComponent.TabStrip) ? "MicaAlt" : "Mica";
        if (window.HasMaterial)
        {
            // The window owns the backdrop and its fallbacks (Windows 10, transparency effects off).
            if (kind != backdropKind) window.SetMaterial(kind);
            backdropKind = kind;
            var transparent = window.IsBackdropTransparent ?? Theme.Translucent;
            Background = transparent ? Theme.Transparent : Theme.WindowSolid;
            return;
        }
        Background = Theme.Translucent ? Theme.Transparent : Theme.WindowSolid;
        if (kind == backdropKind) return;
        backdropKind = kind;
        try
        {
            Window.SystemBackdrop = kind switch
            {
                "Mica" => new MicaBackdrop { Kind = MicaKind.Base },
                "MicaAlt" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                _ => null,
            };
        }
        catch
        {
            backdropKind = "Solid";
            Background = Theme.WindowSolid;
        }
    }

    private void ApplyBackground(MosaicLayout layout)
    {
        var spec = layout.Appearance.Background;
        backgroundLayer.Child = null;
        if (Theme.HighContrast || spec.Kind == BackgroundKind.System)
        {
            backgroundLayer.Background = null;
            return;
        }
        if (ShellBackgrounds.Renderer?.Invoke(spec, IsDark) is { } custom)
        {
            backgroundLayer.Background = custom;
            return;
        }
        backgroundLayer.Background = ShellBackgrounds.Fill(spec, IsDark, Theme.Translucent);
    }

    // MARK: Regions

    /// <summary>One region drawn as a row: flexible components share the spare width.</summary>
    internal Grid RegionRow(ShellRegion region, IReadOnlyList<RegionItem> items)
    {
        var grid = new Grid { ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Stretch };
        var unified = Theme.Layout.AddressBar == AddressBarStyle.Unified;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var flexible = item.Component.IsFlexible();
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = flexible
                    ? new GridLength(item.Component == ShellComponent.FlexibleSpace && !unified ? 0.0001 : 1, GridUnitType.Star)
                    : GridLength.Auto,
            });
            var view = components.Make(item, region);
            view.VerticalAlignment = item.Component == ShellComponent.TabStrip ? view.VerticalAlignment : VerticalAlignment.Center;
            SetColumn(view, i);
            grid.Children.Add(view);
        }
        // A gap in a row with a wide field stays small; on its own it takes the spare room.
        if (!items.Any(i => i.Component is ShellComponent.AddressField or ShellComponent.TabStrip))
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Component == ShellComponent.FlexibleSpace) grid.ColumnDefinitions[i].Width = new GridLength(1, GridUnitType.Star);
            }
        }
        return grid;
    }

    /// <summary>One region drawn as a column (Opera's rail, Edge's sidebar).</summary>
    private Grid RegionColumn(ShellRegion region, IReadOnlyList<RegionItem> items)
    {
        var grid = new Grid { RowSpacing = 6, Padding = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        for (var i = 0; i < items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = items[i].Component == ShellComponent.FlexibleSpace ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
            var view = components.Make(items[i], region);
            view.HorizontalAlignment = HorizontalAlignment.Center;
            SetRow(view, i);
            grid.Children.Add(view);
        }
        AutomationProperties.SetName(grid, region == ShellRegion.Rail ? "Quick access" : "Sidebar");
        return grid;
    }

    private Grid Sidebar(ShellPlan plan, double captionInset, out FrameworkElement topRow)
    {
        var m = Theme.Metrics;
        var grid = new Grid();
        AutomationProperties.SetName(grid, "Sidebar");
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new Grid { Height = captionInset > 0 ? Math.Max(m.TabRowHeight, 40) : m.TabRowHeight, Background = Theme.Transparent };
        if (plan.SidebarTopItems.Count > 0)
        {
            var row = RegionRow(ShellRegion.SidebarTop, plan.SidebarTopItems);
            row.Margin = new Thickness(8, 0, 8 + captionInset, 0);
            top.Children.Add(row);
        }
        grid.Children.Add(top);
        topRow = top;
        if (plan.SidebarMiddleItems.Count > 0)
        {
            var middle = RegionRow(ShellRegion.SidebarMiddle, plan.SidebarMiddleItems);
            middle.Margin = new Thickness(8, 4, 8, 6);
            SetRow(middle, 1);
            grid.Children.Add(middle);
        }
        var tabs = new VerticalTabList(this);
        SetRow(tabs, 2);
        grid.Children.Add(tabs);
        if (plan.SidebarBottomItems.Count > 0)
        {
            var bottom = RegionRow(ShellRegion.SidebarBottom, plan.SidebarBottomItems);
            bottom.Margin = new Thickness(8, 4, 8, 8);
            bottom.Height = m.RowHeight;
            SetRow(bottom, 3);
            grid.Children.Add(bottom);
        }
        return grid;
    }

    private Grid Divided(FrameworkElement content, bool trailing, bool line)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = trailing ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = trailing ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        SetColumn(content, trailing ? 1 : 0);
        grid.Children.Add(content);
        if (line)
        {
            var divider = ShellUi.Divider(Theme, vertical: true);
            SetColumn(divider, trailing ? 0 : 1);
            grid.Children.Add(divider);
        }
        return grid;
    }

    private FrameworkElement BookmarksBar()
    {
        var m = Theme.Metrics;
        if (FeatureMounts.Components.TryGetValue("bookmarksBar", out var mounted))
        {
            try
            {
                if (mounted(Model) is FrameworkElement view) return view;
            }
            catch
            {
                // Falls back to the empty bar below.
            }
        }
        components.Placeholders.Add("bookmarksBar");
        var bar = new Grid { Height = m.BookmarksBarHeight, Padding = new Thickness(14, 0, 14, 0) };
        bar.Children.Add(ShellUi.Text("For quick access, put your bookmarks here on the bookmarks bar.", m.SmallFont, Theme.SecondaryText));
        AutomationProperties.SetName(bar, "Bookmarks bar");
        return bar;
    }

    // MARK: Auto-hiding sidebar

    private void BuildPeek(ShellPlan plan)
    {
        peekHost.Children.Clear();
        peekHost.Background = null;
        FeatureMounts.CoverPage("shell.sidebarPeek", false);
        if (!plan.SidebarAutoHides) return;
        var edge = plan.SidebarTrailing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        // Nearly transparent rather than clear, so the pointer is noticed at the edge.
        var hot = new Border
        {
            Width = 8,
            HorizontalAlignment = edge,
            Background = new SolidColorBrush(ShellTheme.C(0, 0, 0, 2)),
        };
        AutomationProperties.SetAccessibilityView(hot, AccessibilityView.Raw);
        hot.PointerEntered += (_, _) => Model.ShellSettings.SidebarPeek = true;
        peekHost.Children.Add(hot);
        if (!Model.ShellSettings.SidebarPeek) return;
        var sidebar = Sidebar(plan, 0, out _);
        var panel = new Border
        {
            Child = sidebar,
            Width = plan.SidebarWidth,
            HorizontalAlignment = edge,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(12),
            Background = Theme.Translucent
                ? new AcrylicBrush { TintColor = Theme.OverlayPanel.Color, TintOpacity = 0.8, FallbackColor = Theme.OverlayPanel.Color }
                : Theme.OverlayPanel,
            BorderBrush = Theme.CardStroke,
            BorderThickness = new Thickness(1),
            Shadow = new ThemeShadow(),
            Translation = new global::System.Numerics.Vector3(0, 0, 32),
        };
        panel.PointerExited += (_, e) =>
        {
            var point = e.GetCurrentPoint(panel).Position;
            if (point.X < 0 || point.Y < 0 || point.X > panel.ActualWidth || point.Y > panel.ActualHeight) Model.ShellSettings.SidebarPeek = false;
        };
        peekHost.Children.Add(panel);
        FeatureMounts.CoverPage("shell.sidebarPeek", true);
    }

    // MARK: Settings, Mosaic's own pages, overlays, notices

    private void BuildOverlays()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Spacing = 8, Margin = new Thickness(12) };
        foreach (var (id, make) in FeatureMounts.Overlays)
        {
            try
            {
                stack.Children.Add(make(Model));
            }
            catch (Exception error)
            {
                stack.Children.Add(ShellUi.Text($"{id}: {error.Message}", 12, Theme.SecondaryText));
            }
        }
        overlaysHost.Children.Add(stack);
        overlaysHost.IsHitTestVisible = true;
        overlaysHost.Background = null;
    }

    private void BuildNotice()
    {
        noticeText.FontSize = 13;
        noticeHost.Child = noticeText;
        noticeHost.HorizontalAlignment = HorizontalAlignment.Center;
        noticeHost.VerticalAlignment = VerticalAlignment.Bottom;
        noticeHost.Margin = new Thickness(0, 0, 0, 22);
        noticeHost.Padding = new Thickness(16, 8, 16, 8);
        noticeHost.CornerRadius = new CornerRadius(18);
        noticeHost.Background = Theme.OverlayPanel;
        noticeHost.BorderBrush = Theme.CardStroke;
        noticeHost.BorderThickness = new Thickness(1);
        noticeHost.Shadow = new ThemeShadow();
        noticeHost.Translation = new global::System.Numerics.Vector3(0, 0, 16);
        noticeHost.IsHitTestVisible = false;
        AutomationProperties.SetLiveSetting(noticeText, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(noticeHost, "Notice");
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserModel.SettingsPage):
            case nameof(BrowserModel.SelectedTab):
            case nameof(BrowserModel.AddressText):
            case nameof(BrowserModel.Content):
            case null:
                UpdateOverlays();
                break;
            case nameof(BrowserModel.Notice):
                UpdateNotice();
                break;
            case nameof(BrowserModel.ShowExtensions) when Model.ShowExtensions:
                // The extensions window is gone: Settings › Extensions replaces it (as on macOS).
                Model.ShowExtensions = false;
                Model.ShowSettings(SettingsCategory.Extensions);
                break;
        }
    }

    private void UpdateNotice()
    {
        var notice = Model.Notice;
        noticeHost.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        noticeText.Text = notice ?? "";
        if (notice is not null)
        {
            var peer = FrameworkElementAutomationPeer.FromElement(noticeText) ?? FrameworkElementAutomationPeer.CreatePeerForElement(noticeText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>Shows Settings or Mosaic's own page over the page area (and hides the Gecko page while they show).</summary>
    public void UpdateOverlays()
    {
        if (Model.SettingsPage is { } category)
        {
            settingsView ??= new SettingsView(this);
            settingsHost.Child ??= settingsView;
            settingsView.Show(category);
            settingsHost.Visibility = Visibility.Visible;
            FeatureMounts.CoverPage("shell.settings", true);
        }
        else
        {
            settingsHost.Visibility = Visibility.Collapsed;
            settingsView?.Hidden();
            FeatureMounts.CoverPage("shell.settings", false);
        }

        var host = Model.ShellInternalPage;
        if (host == "settings" && Model.SelectedTab?.Url is { } url)
        {
            // mosaic://settings/<category> opens Settings in the window.
            var id = url.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2) ?? "";
            if (Model.SettingsPage is null) Model.ShowSettings(SettingsCategories.FromId(id));
            host = null;
        }
        // The winui area's page area shows mosaic:// pages itself; the shell only does it in
        // front of the skeleton window's placeholder.
        if (!PageArea.IsPlaceholder) host = null;
        if (host is not null)
        {
            if (host != internalHostShown)
            {
                internalHostShown = host;
                internalHost.Child = InternalPage(host);
            }
            internalHost.Visibility = Visibility.Visible;
            FeatureMounts.CoverPage("shell.internalPage", true);
        }
        else
        {
            internalHostShown = null;
            internalHost.Child = null;
            internalHost.Visibility = Visibility.Collapsed;
            FeatureMounts.CoverPage("shell.internalPage", false);
        }
    }

    private UIElement InternalPage(string host)
    {
        if (FeatureMounts.InternalPages.TryGetValue(host, out var make))
        {
            try
            {
                return make(Model);
            }
            catch (Exception error)
            {
                return Pending($"mosaic://{host}", $"This page could not be shown: {error.Message}");
            }
        }
        return Pending($"mosaic://{host}", "This page is being built.");
    }

    internal FrameworkElement Pending(string title, string detail, string symbol = "window")
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 10 };
        var icon = ShellUi.Icon(symbol, 40, Theme.SecondaryText);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(icon);
        var heading = ShellUi.Text(title, 20, semibold: true);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        panel.Children.Add(heading);
        var text = ShellUi.Text(detail, 13, Theme.SecondaryText);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(text);
        return panel;
    }

    // MARK: Keyboard

    private void AddAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) =>
            {
                e.Handled = true;
                action();
            };
            KeyboardAccelerators.Add(accelerator);
        }
        const VirtualKey comma = (VirtualKey)0xBC;
        Add(comma, VirtualKeyModifiers.Control, Model.ToggleSettings);
        Add(comma, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, Model.ToggleVerticalTabs);
        Add(VirtualKey.L, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, Model.ToggleSidebar);
        Add(VirtualKey.D, VirtualKeyModifiers.Menu, () => Model.FocusAddressBar());
        Add(VirtualKey.F6, VirtualKeyModifiers.None, () => Model.FocusAddressBar());
        Add(VirtualKey.Home, VirtualKeyModifiers.Menu, Model.GoHome);
        Add(VirtualKey.F, VirtualKeyModifiers.Menu, OpenMenu);
        Add(VirtualKey.E, VirtualKeyModifiers.Menu, OpenMenu);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && Model.IsSettingsOpen && !e.Handled)
        {
            e.Handled = true;
            Model.DismissSettings();
        }
    }

    private void OpenMenu()
    {
        if (menuButton is { IsLoaded: true } button) button.Flyout?.ShowAt(button);
    }

    internal void RegisterMenuButton(Button button) => menuButton = button;

    internal void RegisterAddressField(AddressField field) => addressFields.Add(new WeakReference<AddressField>(field));

    private void FocusAddressField()
    {
        if (!Model.AllowsFocusChanges) return;
        foreach (var reference in addressFields)
        {
            if (reference.TryGetTarget(out var field) && field.IsShown)
            {
                field.FocusField();
                return;
            }
        }
    }

    // MARK: Diagnostics

    /// <summary>Focusable controls without an accessible name (should be none).</summary>
    public List<string> UnnamedControls()
    {
        var result = new List<string>();
        void Walk(DependencyObject element)
        {
            var count = VisualTreeHelper.GetChildrenCount(element);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                if (child is UIElement { Visibility: not Visibility.Visible }) continue;
                if (child is Control { IsTabStop: true } control && control is not ScrollViewer and not ListView)
                {
                    var name = AutomationProperties.GetName(control);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        var peer = FrameworkElementAutomationPeer.FromElement(control) ?? FrameworkElementAutomationPeer.CreatePeerForElement(control);
                        name = peer?.GetName();
                    }
                    if (string.IsNullOrWhiteSpace(name)) result.Add(control.GetType().Name);
                    if (control is not (ContentControl or UserControl)) continue;
                }
                Walk(child);
            }
        }
        Walk(this);
        return result;
    }

    internal Rect Bounds(FrameworkElement element) =>
        element.TransformToVisual(this).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    internal Border PageHost => pageHost;
}
