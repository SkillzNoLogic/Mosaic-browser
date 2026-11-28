namespace Mosaic.Core.Shell;

/// <summary>One component as drawn: adjacent pairs attach (tab strip + new tab, address field + star).</summary>
public sealed record RegionItem(int Id, ShellComponent Component, bool AttachesNewTab = false, bool EmbedsStar = false);

/// <summary>
/// Sizes for the current density and text size, in DIPs (port of macOS's <c>ShellMetrics</c>).
/// Primary targets stay at least 32×32, and toolbar buttons span the full height of their row.
/// </summary>
public sealed record ShellMetrics(double Scale, ShellDensity Density)
{
    private double Step => Density switch { ShellDensity.Compact => -1, ShellDensity.Comfortable => 1, _ => 0 };

    public double RowHeight => (44 + Step * 6) * Scale;
    public double UnifiedRowHeight => (52 + Step * 6) * Scale;
    public double TabRowHeight => (40 + Step * 4) * Scale;
    public double SegmentedTabBarHeight => (32 + Step * 3) * Scale;
    public double Button => (32 + Step * 3) * Scale;
    public double Icon => (15 + Step) * Scale;
    public double Font => (13 + Step * 0.5) * Scale;
    public double SmallFont => (11.5 + Step * 0.5) * Scale;
    public double FieldHeight => (32 + Step * 3) * Scale;
    public double TabHeight => (32 + Step * 3) * Scale;
    public double SidebarRowHeight => (34 + Step * 3) * Scale;
    public double BookmarksBarHeight => (30 + Step * 2) * Scale;
    public double RailWidth => (52 + Step * 4) * Scale;
    /// <summary>Edge's collapsed vertical tab pane: a column of tab icons.</summary>
    public double IconStripWidth => (48 + Step * 4) * Scale;
    /// <summary>The thin drag strip that holds the caption buttons when nothing else is at the top (Windows).</summary>
    public double CaptionStripHeight => 32;
}

/// <summary>Which part of the window holds the caption buttons (minimise, maximise, close) at the top right.</summary>
public enum CaptionHost
{
    /// <summary>The full-width title row (Chrome, Opera, Edge tabs).</summary>
    TitleRow,
    /// <summary>A thin strip above the page in the main column (Zen / Arc).</summary>
    TitleStrip,
    /// <summary>The toolbar row (Edge with vertical tabs).</summary>
    Toolbar,
    /// <summary>The top of a sidebar on the right.</summary>
    TrailingSidebar,
}

public static class ShellLayoutRules
{
    /// <summary>
    /// What the shell actually draws for <paramref name="region"/>, after the attachment rules and
    /// the components that do not apply (tab strip with vertical tabs, hidden Home button, a
    /// sidebar button without a sidebar). A row of nothing but gaps draws nothing.
    /// </summary>
    public static List<RegionItem> DrawnItems(this MosaicLayout layout, ShellRegion region, bool showsHome)
    {
        var list = layout.Components(region);
        var items = new List<RegionItem>();
        for (var index = 0; index < list.Count; index++)
        {
            var component = list[index];
            ShellComponent? next = index + 1 < list.Count ? list[index + 1] : null;
            switch (component)
            {
                case ShellComponent.TabStrip when layout.TabPlacement.IsVertical():
                case ShellComponent.Home when !showsHome:
                case ShellComponent.SidebarToggle when !layout.HasSidebar:
                    continue;
                case ShellComponent.TabStrip:
                    var attach = next == ShellComponent.NewTab;
                    items.Add(new RegionItem(index, component, AttachesNewTab: attach));
                    if (attach) index++;
                    break;
                case ShellComponent.AddressField:
                    var embed = next == ShellComponent.BookmarkStar;
                    items.Add(new RegionItem(index, component, EmbedsStar: embed));
                    if (embed) index++;
                    break;
                default:
                    items.Add(new RegionItem(index, component));
                    break;
            }
        }
        return items.Any(i => i.Component != ShellComponent.FlexibleSpace) ? items : [];
    }
}

/// <summary>
/// Which rows and columns the window has for a layout, decided once per render (port of
/// macOS's <c>ShellPlan</c>, with Windows's caption buttons at the top right instead of the
/// traffic lights at the top left). Pure: the WinUI shell draws what this says.
/// </summary>
/// <remarks>
/// Grid: columns rail | leading sidebar | main | trailing sidebar; rows title row | main header
/// (title strip, toolbar, tab bar, bookmarks bar) | page. The trailing rail (Edge's sidebar)
/// sits beside the page under the header rows, as in Edge.
/// </remarks>
public sealed class ShellPlan
{
    public MosaicLayout Layout { get; }
    public ShellMetrics Metrics { get; }
    public List<RegionItem> TitleItems { get; }
    public List<RegionItem> ToolbarItems { get; }
    public List<RegionItem> TabBarItems { get; }
    public List<RegionItem> RailItems { get; }
    public List<RegionItem> TrailingRailItems { get; }
    public List<RegionItem> SidebarTopItems { get; }
    public List<RegionItem> SidebarMiddleItems { get; }
    public List<RegionItem> SidebarBottomItems { get; }
    /// <summary>The sidebar's top and middle rows, shown in a strip above the page while a vertical sidebar holding the address field is hidden.</summary>
    public List<RegionItem> CollapsedStripItems { get; }
    public bool SidebarVisible { get; }
    public bool SidebarAutoHides { get; }
    public bool SidebarTrailing { get; }
    /// <summary>Edge: a collapsed vertical tab pane stays as a narrow column of tab icons.</summary>
    public bool SidebarIconStrip { get; }
    public bool ShowsTitleStrip { get; }
    public CaptionHost CaptionHost { get; }

    public ShellPlan(MosaicLayout layout, bool showsHome, bool sidebarVisible, ShellMetrics metrics)
    {
        Layout = layout;
        Metrics = metrics;
        TitleItems = layout.DrawnItems(ShellRegion.TitleBar, showsHome);
        ToolbarItems = layout.DrawnItems(ShellRegion.Toolbar, showsHome);
        TabBarItems = layout.DrawnItems(ShellRegion.TabBar, showsHome);
        RailItems = layout.DrawnItems(ShellRegion.Rail, showsHome);
        TrailingRailItems = layout.DrawnItems(ShellRegion.TrailingRail, showsHome);
        SidebarTopItems = layout.DrawnItems(ShellRegion.SidebarTop, showsHome);
        SidebarMiddleItems = layout.DrawnItems(ShellRegion.SidebarMiddle, showsHome);
        SidebarBottomItems = layout.DrawnItems(ShellRegion.SidebarBottom, showsHome);
        SidebarVisible = sidebarVisible;
        SidebarAutoHides = layout.TabPlacement.IsVertical() && layout.Sidebar == SidebarBehavior.AutoHide;
        SidebarTrailing = layout.TabPlacement == TabPlacement.SidebarTrailing;
        SidebarIconStrip = layout.Preset == MosaicPresetID.Edge && layout.TabPlacement.IsVertical()
            && !sidebarVisible && layout.Sidebar == SidebarBehavior.Collapsible;

        var sidebarHidden = layout.TabPlacement.IsVertical() && !sidebarVisible;
        if (sidebarHidden && layout.RegionOf(ShellComponent.AddressField)?.IsSidebar() == true)
        {
            var items = SidebarTopItems.Where(i => i.Component != ShellComponent.FlexibleSpace).ToList();
            items.AddRange(SidebarMiddleItems.Select(i => i with { Id = i.Id + 1000 }));
            CollapsedStripItems = items;
        }
        else
        {
            CollapsedStripItems = [];
        }

        var trailingSidebarHoldsCaptions = sidebarVisible && SidebarTrailing && TitleItems.Count == 0;
        ShowsTitleStrip = TitleItems.Count == 0 && !trailingSidebarHoldsCaptions
            && (ToolbarItems.Count == 0 || CollapsedStripItems.Count > 0);
        CaptionHost = TitleItems.Count > 0 ? CaptionHost.TitleRow
            : trailingSidebarHoldsCaptions ? CaptionHost.TrailingSidebar
            : ShowsTitleStrip ? CaptionHost.TitleStrip
            : CaptionHost.Toolbar;
    }

    public double TitleRowHeight
    {
        get
        {
            if (TitleItems.Any(i => i.Component == ShellComponent.AddressField) && Layout.AddressBar == AddressBarStyle.Unified)
            {
                return Metrics.UnifiedRowHeight;
            }
            if (TitleItems.Any(i => i.Component == ShellComponent.TabStrip)) return Metrics.TabRowHeight;
            return Metrics.RowHeight;
        }
    }

    public double TabBarHeight =>
        TabBarItems.Any(i => i.Component == ShellComponent.TabStrip) ? Metrics.SegmentedTabBarHeight : Metrics.RowHeight;

    public double TitleStripHeight => CollapsedStripItems.Count == 0 ? Metrics.CaptionStripHeight : Metrics.RowHeight;

    public double RailWidth => RailItems.Count == 0 ? 0 : Metrics.RailWidth;

    public double TrailingRailWidth => TrailingRailItems.Count == 0 ? 0 : Metrics.RailWidth;

    /// <summary>The sidebar grows with the Accessibility interface size so its controls still fit.</summary>
    public double SidebarWidth => Layout.SidebarWidth * Metrics.Scale;

    /// <summary>Height of the row holding the caption buttons.</summary>
    public double CaptionRowHeight => CaptionHost switch
    {
        CaptionHost.TitleRow => TitleRowHeight,
        CaptionHost.TitleStrip => TitleStripHeight,
        CaptionHost.TrailingSidebar => Metrics.TabRowHeight,
        _ => Metrics.RowHeight,
    };

    /// <summary>Whether a row sits between the top of the main column and the page.</summary>
    public bool HasRowAbovePage => TitleItems.Count > 0 || ShowsTitleStrip || ToolbarItems.Count > 0 || TabBarItems.Count > 0;

    /// <summary>The rows and columns present, for diagnostics (<c>dump</c>) and tests.</summary>
    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>();
            if (TitleItems.Count > 0) parts.Add("titleRow");
            if (ShowsTitleStrip) parts.Add("titleStrip");
            if (ToolbarItems.Count > 0) parts.Add("toolbar");
            if (TabBarItems.Count > 0) parts.Add("tabBar");
            if (Layout.BookmarksBar) parts.Add("bookmarksBar");
            if (RailItems.Count > 0) parts.Add("rail");
            if (TrailingRailItems.Count > 0) parts.Add("trailingRail");
            if (SidebarVisible) parts.Add(SidebarTrailing ? "sidebarTrailing" : "sidebarLeading");
            if (SidebarIconStrip) parts.Add("sidebarIcons");
            if (SidebarAutoHides) parts.Add("sidebarAutoHide");
            return parts;
        }
    }
}
