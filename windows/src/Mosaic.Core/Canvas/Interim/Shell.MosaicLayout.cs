// INTERIM: a verbatim copy of the shell area's MosaicLayout.cs (win2/shell f740658), namespace changed,
// so the canvas builds against the real model before it lands in win-next. Delete this folder
// then and change `using Mosaic.Core.Canvas.Interim;` to `using Mosaic.Core.Shell;`.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Canvas.Interim;

/// <summary>
/// The "Mosaic": a description of the whole window's chrome. The shell renders a
/// <see cref="MosaicLayout"/>; presets are starting values of it; the layout editor (canvas)
/// edits it by moving <see cref="ShellComponent"/>s between <see cref="ShellRegion"/>s with the
/// mutation API below.
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Shell/MosaicLayout.swift</c>; the JSON is byte-for-byte what
/// the macOS app writes (<see cref="JsonText"/>, checked against golden files from the Swift
/// sources). Decoding is forgiving: unknown components, regions and enum values are dropped or
/// replaced by the preset's value, so an imported or older file always gives a usable layout.
/// Windows differences: the Edge preset (and its <see cref="ShellComponent.VerticalTabs"/> and
/// <see cref="ShellRegion.TrailingRail"/>), and no Safari preset: a Safari layout from macOS
/// is turned into the Edge preset by <see cref="FromSafari"/>.
/// Owner: shell.
/// </remarks>
public sealed class MosaicLayout
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>The preset this layout started from (drives a few preset-specific touches).</summary>
    public MosaicPresetID Preset { get; set; }
    public string Name { get; set; } = "";
    public TabPlacement TabPlacement { get; set; }
    public TabStyle TabStyle { get; set; }
    public AddressBarStyle AddressBar { get; set; }
    /// <summary>Components per region, in order. Regions missing from the dictionary are empty.</summary>
    public Dictionary<ShellRegion, List<ShellComponent>> Regions { get; set; } = [];
    public bool BookmarksBar { get; set; }
    public SidebarBehavior Sidebar { get; set; }
    public double SidebarWidth { get; set; } = 240;
    public MosaicAppearance Appearance { get; set; } = new();

    public MosaicLayout Clone() => new()
    {
        SchemaVersion = SchemaVersion, Preset = Preset, Name = Name, TabPlacement = TabPlacement, TabStyle = TabStyle,
        AddressBar = AddressBar, Regions = Regions.ToDictionary(p => p.Key, p => p.Value.ToList()),
        BookmarksBar = BookmarksBar, Sidebar = Sidebar, SidebarWidth = SidebarWidth, Appearance = Appearance.Clone(),
    };

    // MARK: Reading

    public IReadOnlyList<ShellComponent> Components(ShellRegion region) =>
        Regions.TryGetValue(region, out var list) ? list : [];

    public bool Contains(ShellComponent component) => Regions.Values.Any(list => list.Contains(component));

    /// <summary>The region holding <paramref name="component"/> (the first one, for flexible space).</summary>
    public ShellRegion? RegionOf(ShellComponent component)
    {
        foreach (var region in Enum.GetValues<ShellRegion>())
        {
            if (Components(region).Contains(component)) return region;
        }
        return null;
    }

    /// <summary>Whether the window has a sidebar at all (vertical tabs, or a sidebar button to open one).</summary>
    public bool HasSidebar => TabPlacement.IsVertical() || Contains(ShellComponent.SidebarToggle);

    /// <summary>True when the layout carries GX styling (as applied by <see cref="WithGXStyling"/>).</summary>
    public bool HasGXStyling => Appearance.Glow && Appearance.ColorScheme == ColorSchemePreference.Dark;

    // MARK: Mutation (settings page and layout editor)

    /// <summary>
    /// Inserts <paramref name="component"/> into <paramref name="region"/> at <paramref name="index"/>
    /// (clamped; null = at the end). A component already elsewhere is moved (every component except
    /// flexible space appears once). False, and no change, when the region does not accept it.
    /// </summary>
    public bool Insert(ShellComponent component, ShellRegion region, int? index = null)
    {
        if (!region.Accepts(component)) return false;
        var target = index ?? Components(region).Count;
        if (component != ShellComponent.FlexibleSpace && RegionOf(component) is { } from)
        {
            var fromIndex = Regions[from].IndexOf(component);
            if (fromIndex >= 0)
            {
                Regions[from].RemoveAt(fromIndex);
                if (from == region && fromIndex < target) target -= 1;
            }
        }
        var list = Components(region).ToList();
        list.Insert(Math.Clamp(target, 0, list.Count), component);
        Regions[region] = list;
        return true;
    }

    /// <summary>Removes the component at <paramref name="index"/> of <paramref name="region"/>; null if out of range.</summary>
    public ShellComponent? RemoveAt(int index, ShellRegion region)
    {
        if (!Regions.TryGetValue(region, out var list) || index < 0 || index >= list.Count) return null;
        var removed = list[index];
        list.RemoveAt(index);
        return removed;
    }

    /// <summary>Removes every occurrence of <paramref name="component"/>.</summary>
    public void Remove(ShellComponent component)
    {
        foreach (var list in Regions.Values) list.RemoveAll(c => c == component);
    }

    /// <summary>
    /// Moves the component at <paramref name="index"/> of <paramref name="from"/> to
    /// <paramref name="toIndex"/> of <paramref name="to"/> (index after removal). False, and no
    /// change, if either side is invalid.
    /// </summary>
    public bool Move(ShellRegion from, int index, ShellRegion to, int toIndex)
    {
        var source = Components(from);
        if (index < 0 || index >= source.Count) return false;
        var component = source[index];
        if (!to.Accepts(component)) return false;
        Regions[from].RemoveAt(index);
        var list = Components(to).ToList();
        list.Insert(Math.Clamp(toIndex, 0, list.Count), component);
        Regions[to] = list;
        return true;
    }

    /// <summary>
    /// Changes where the tabs are, moving navigation so it stays reachable (macOS semantics).
    /// Edge layouts switch between Edge's own horizontal and vertical modes instead.
    /// </summary>
    public MosaicLayout WithTabPlacement(TabPlacement placement)
    {
        if (placement == TabPlacement) return Clone();
        if (Preset == MosaicPresetID.Edge) return WithEdgeTabPlacement(placement);
        var layout = Clone();
        var wasVertical = TabPlacement.IsVertical();
        layout.TabPlacement = placement;
        if (wasVertical && !placement.IsVertical())
        {
            var moving = Components(ShellRegion.SidebarTop).Concat(Components(ShellRegion.SidebarMiddle))
                .Where(c => c is not ShellComponent.FlexibleSpace and not ShellComponent.SidebarToggle).ToList();
            foreach (var c in moving) layout.Insert(c, ShellRegion.Toolbar);
            if (!layout.Contains(ShellComponent.TabStrip)) layout.Insert(ShellComponent.TabStrip, ShellRegion.TitleBar, 0);
            if (layout.Components(ShellRegion.TitleBar).SequenceEqual([ShellComponent.TabStrip]))
            {
                layout.Insert(ShellComponent.NewTab, ShellRegion.TitleBar);
            }
        }
        else if (!wasVertical && placement.IsVertical())
        {
            var toolbar = Components(ShellRegion.Toolbar).ToList();
            if (layout.Components(ShellRegion.SidebarTop).Count == 0 && layout.Components(ShellRegion.SidebarMiddle).Count == 0)
            {
                ShellComponent[] navigation = [ShellComponent.Back, ShellComponent.Forward, ShellComponent.Reload];
                layout.Regions[ShellRegion.SidebarTop] =
                    [ShellComponent.SidebarToggle, ShellComponent.FlexibleSpace, .. toolbar.Where(navigation.Contains)];
                if (layout.RegionOf(ShellComponent.AddressField) == ShellRegion.Toolbar)
                {
                    layout.Insert(ShellComponent.AddressField, ShellRegion.SidebarMiddle);
                    if (layout.RegionOf(ShellComponent.BookmarkStar) == ShellRegion.Toolbar)
                    {
                        layout.Insert(ShellComponent.BookmarkStar, ShellRegion.SidebarMiddle);
                    }
                }
                if (layout.Regions.TryGetValue(ShellRegion.Toolbar, out var bar)) bar.RemoveAll(navigation.Contains);
                // Whatever is left in the toolbar goes to the sidebar's bottom row.
                var rest = layout.Components(ShellRegion.Toolbar).Where(c => c != ShellComponent.FlexibleSpace).ToList();
                if (layout.Components(ShellRegion.SidebarBottom).Count == 0 && rest.Count > 0)
                {
                    layout.Regions[ShellRegion.Toolbar] = [];
                    layout.Regions[ShellRegion.SidebarBottom] =
                    [
                        ShellComponent.Workspaces, ShellComponent.FlexibleSpace,
                        .. rest.Where(c => c is not ShellComponent.Workspaces and not ShellComponent.NewTab),
                        ShellComponent.NewTab,
                    ];
                }
            }
            // The tab strip's title-bar row is not needed any more.
            var title = layout.Components(ShellRegion.TitleBar);
            if (title.All(c => c is ShellComponent.TabStrip or ShellComponent.NewTab or ShellComponent.FlexibleSpace))
            {
                if (title.Contains(ShellComponent.NewTab) && layout.RegionOf(ShellComponent.NewTab) == ShellRegion.TitleBar)
                {
                    layout.Insert(ShellComponent.NewTab, ShellRegion.SidebarBottom);
                }
                layout.Regions[ShellRegion.TitleBar] = [];
            }
        }
        return layout.Normalized();
    }

    /// <summary>
    /// Edge's tab modes. Vertical: the title row's buttons (workspaces, tab actions) move to the
    /// top of a collapsible tab pane, the toolbar stays where it is, and the title row goes.
    /// Horizontal: they move back in front of the tab strip.
    /// </summary>
    private MosaicLayout WithEdgeTabPlacement(TabPlacement placement)
    {
        var layout = Clone();
        layout.TabPlacement = placement;
        if (placement.IsVertical() && !TabPlacement.IsVertical())
        {
            var moving = Components(ShellRegion.TitleBar)
                .Where(c => c is not ShellComponent.TabStrip and not ShellComponent.NewTab and not ShellComponent.FlexibleSpace).ToList();
            var top = layout.Components(ShellRegion.SidebarTop)
                .Where(c => c is not ShellComponent.FlexibleSpace and not ShellComponent.SidebarToggle).ToList();
            layout.Regions[ShellRegion.TitleBar] = [];
            layout.Regions[ShellRegion.SidebarTop] =
                [.. moving.Where(c => !top.Contains(c)), .. top, ShellComponent.FlexibleSpace, ShellComponent.SidebarToggle];
            layout.Remove(ShellComponent.TabStrip);
            layout.Insert(ShellComponent.NewTab, ShellRegion.SidebarBottom);
            if (layout.Sidebar == SidebarBehavior.Shown) layout.Sidebar = SidebarBehavior.Collapsible;
        }
        else if (!placement.IsVertical() && TabPlacement.IsVertical())
        {
            static bool Skip(ShellComponent c) =>
                c is ShellComponent.FlexibleSpace or ShellComponent.SidebarToggle or ShellComponent.NewTab;
            static bool Button(ShellComponent c) => !c.NeedsWideRow() && c != ShellComponent.BookmarkStar;
            var title = Components(ShellRegion.TitleBar)
                .Where(c => c is not ShellComponent.TabStrip and not ShellComponent.NewTab and not ShellComponent.FlexibleSpace).ToList();
            var topButtons = Components(ShellRegion.SidebarTop).Where(c => !Skip(c) && Button(c) && !title.Contains(c)).ToList();
            var rest = Components(ShellRegion.SidebarTop).Where(c => !Skip(c) && !Button(c))
                .Concat(Components(ShellRegion.SidebarMiddle)).Concat(Components(ShellRegion.SidebarBottom))
                .Where(c => !Skip(c)).ToList();
            layout.Regions[ShellRegion.SidebarTop] = [];
            layout.Regions[ShellRegion.SidebarMiddle] = [];
            layout.Regions[ShellRegion.SidebarBottom] = [];
            layout.Regions[ShellRegion.TitleBar] =
                [.. title, .. topButtons, ShellComponent.TabStrip, ShellComponent.NewTab, ShellComponent.FlexibleSpace];
            // Anything else that lived in the pane joins the toolbar: the star after the address
            // field, the rest before the menu.
            foreach (var c in rest)
            {
                var toolbar = layout.Components(ShellRegion.Toolbar).ToList();
                var address = toolbar.IndexOf(ShellComponent.AddressField);
                var menu = toolbar.IndexOf(ShellComponent.Menu);
                int? at = c == ShellComponent.BookmarkStar && address >= 0 ? address + 1 : menu >= 0 ? menu : null;
                layout.Insert(c, ShellRegion.Toolbar, at);
            }
            layout.Remove(ShellComponent.SidebarToggle);
        }
        return layout.Normalized();
    }

    /// <summary>
    /// Repairs anything that would leave the window unusable or is out of range: components in
    /// regions that cannot hold them, duplicates, a missing address field or tab strip, invalid
    /// colours and numbers. Always applied before a layout is shown or saved.
    /// </summary>
    public MosaicLayout Normalized()
    {
        var layout = Clone();
        layout.SchemaVersion = CurrentSchemaVersion;
        var seen = new HashSet<ShellComponent>();
        var cleaned = new Dictionary<ShellRegion, List<ShellComponent>>();
        foreach (var region in Enum.GetValues<ShellRegion>())
        {
            var list = new List<ShellComponent>();
            foreach (var component in Components(region))
            {
                if (!region.Accepts(component)) continue;
                if (component != ShellComponent.FlexibleSpace && !seen.Add(component)) continue;
                // Two gaps in a row do nothing a single one doesn't.
                if (component == ShellComponent.FlexibleSpace && list.Count > 0 && list[^1] == ShellComponent.FlexibleSpace) continue;
                list.Add(component);
            }
            if (list.Count > 0) cleaned[region] = list;
        }
        layout.Regions = cleaned;

        // The address field and (for horizontal tabs) the tab strip must exist somewhere.
        if (!layout.Contains(ShellComponent.AddressField))
        {
            layout.Insert(ShellComponent.AddressField, layout.TabPlacement.IsVertical() ? ShellRegion.SidebarMiddle : ShellRegion.Toolbar);
        }
        if (layout.TabPlacement == TabPlacement.Top && layout.RegionOf(ShellComponent.AddressField)?.IsSidebar() == true)
        {
            // A horizontal layout's sidebar is hidden by default; the field must stay visible.
            layout.Insert(ShellComponent.AddressField, ShellRegion.Toolbar);
        }
        if (layout.TabPlacement == TabPlacement.Top)
        {
            var strip = layout.RegionOf(ShellComponent.TabStrip);
            if (strip is null || strip.Value.IsSidebar() || strip.Value.IsRail())
            {
                layout.Insert(ShellComponent.TabStrip, ShellRegion.TitleBar, 0);
            }
        }
        // A sidebar that is hidden by default needs a way to open it.
        if (layout.TabPlacement.IsVertical() && layout.Sidebar == SidebarBehavior.Collapsible && !layout.Contains(ShellComponent.SidebarToggle))
        {
            layout.Insert(ShellComponent.SidebarToggle, ShellRegion.SidebarTop, 0);
        }

        layout.SidebarWidth = Math.Clamp(double.IsFinite(layout.SidebarWidth) ? layout.SidebarWidth : 240, 180, 420);
        var a = layout.Appearance;
        static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
        a.CornerRadius = Clamp(a.CornerRadius, 0, 20, 8);
        a.ContentInset = Clamp(a.ContentInset, 0, 24, 0);
        a.ContentCornerRadius = Clamp(a.ContentCornerRadius, 0, 28, 0);
        if (!HexColor.IsValid(a.Accent)) a.Accent = HexColor.System;
        if (HexColor.Components(a.Background.Color) is null) a.Background.Color = BackgroundSpec.DefaultColor;
        a.Background.Gradient.Angle = double.IsFinite(a.Background.Gradient.Angle) ? a.Background.Gradient.Angle : 135;
        layout.Appearance = a.ClampedBackgrounds();
        return layout;
    }

    // MARK: Coding

    /// <summary>The JSON object (key order does not matter; <see cref="SwiftJson"/> sorts).</summary>
    public JsonObject ToJsonObject()
    {
        var regions = new JsonObject();
        foreach (var (region, list) in Regions)
        {
            if (list.Count == 0) continue;
            regions[region.Raw()] = new JsonArray([.. list.Select(c => (JsonNode)JsonValue.Create(c.Raw()))]);
        }
        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["preset"] = Preset.Raw(),
            ["name"] = Name,
            ["tabPlacement"] = TabPlacement.Raw(),
            ["tabStyle"] = TabStyle.Raw(),
            ["addressBar"] = AddressBar.Raw(),
            ["regions"] = regions,
            ["bookmarksBar"] = BookmarksBar,
            ["sidebar"] = Sidebar.Raw(),
            ["sidebarWidth"] = SidebarWidth,
            ["appearance"] = Appearance.ToJson(),
        };
    }

    /// <summary>The file text macOS writes for this layout (<c>jsonData(pretty:)</c>).</summary>
    public string JsonText(bool pretty = true) => SwiftJson.Write(ToJsonObject(), pretty);

    public byte[] JsonData(bool pretty = true) => new UTF8Encoding(false).GetBytes(JsonText(pretty));

    /// <summary>Reads a layout file (forgiving) and normalises it, as macOS's <c>MosaicLayout.from(json:)</c>.</summary>
    /// <exception cref="JsonException">The text is not JSON, or not a JSON object.</exception>
    public static MosaicLayout FromJson(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false });
        return Decode(node)?.Normalized() ?? throw new JsonException("A Mosaic layout must be a JSON object.");
    }

    public static MosaicLayout FromJson(byte[] utf8) => FromJson(Encoding.UTF8.GetString(utf8).TrimStart('﻿'));

    /// <summary>
    /// Decodes without normalising (Swift's <c>init(from:)</c>); null when <paramref name="node"/>
    /// is not an object. A Safari layout comes back as the Edge preset (<see cref="FromSafari"/>).
    /// </summary>
    public static MosaicLayout? Decode(JsonNode? node)
    {
        if (node is not JsonObject c) return null;
        var presetText = SwiftDecode.TryString(c["preset"]);
        var isSafari = presetText == "safari";
        var preset = JsonEnum.Parse<MosaicPresetID>(presetText) ?? MosaicPresetID.Chrome;
        var d = isSafari ? SafariLayout() : Factory(preset);
        var layout = new MosaicLayout
        {
            SchemaVersion = SwiftDecode.Int(c, "schemaVersion", CurrentSchemaVersion),
            Preset = isSafari ? MosaicPresetID.Chrome : preset,
            Name = SwiftDecode.String(c, "name", d.Name),
            TabPlacement = SwiftDecode.Enum(c, "tabPlacement", d.TabPlacement),
            TabStyle = SwiftDecode.Enum(c, "tabStyle", d.TabStyle),
            AddressBar = SwiftDecode.Enum(c, "addressBar", d.AddressBar),
            BookmarksBar = SwiftDecode.Bool(c, "bookmarksBar", d.BookmarksBar),
            Sidebar = SwiftDecode.Enum(c, "sidebar", d.Sidebar),
            SidebarWidth = SwiftDecode.Double(c, "sidebarWidth", d.SidebarWidth),
            Appearance = MosaicAppearance.FromJson(c["appearance"]) ?? d.Appearance,
            Regions = DecodeRegions(c["regions"]) ?? d.Regions,
        };
        return isSafari ? FromSafari(layout) : layout;
    }

    /// <summary>Regions as plain strings, so unknown names are skipped, not fatal.</summary>
    private static Dictionary<ShellRegion, List<ShellComponent>>? DecodeRegions(JsonNode? node)
    {
        if (node is not JsonObject raw) return null;
        var parsed = new List<(string Key, List<string> Names)>();
        foreach (var (key, value) in raw)
        {
            // [String: [String]]: one value of the wrong shape fails the whole dictionary.
            if (value is not JsonArray array) return null;
            var names = new List<string>();
            foreach (var item in array)
            {
                if (SwiftDecode.TryString(item) is not { } name) return null;
                names.Add(name);
            }
            parsed.Add((key, names));
        }
        var regions = new Dictionary<ShellRegion, List<ShellComponent>>();
        foreach (var (key, names) in parsed)
        {
            if (JsonEnum.Parse<ShellRegion>(key) is not { } region) continue;
            regions[region] = [.. names.Select(JsonEnum.Parse<ShellComponent>).Where(c => c is not null).Select(c => c!.Value)];
        }
        return regions;
    }

    // MARK: Equality

    /// <summary>Same layout (same JSON).</summary>
    public bool ContentEquals(MosaicLayout? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        try
        {
            return JsonText(pretty: false) == other.JsonText(pretty: false);
        }
        catch (ArgumentException)
        {
            return false;  // NaN or infinity somewhere: never equal to a normalised layout
        }
    }

    // MARK: Presets

    /// <summary>The factory layout of <paramref name="preset"/> (a fresh copy).</summary>
    public static MosaicLayout Factory(MosaicPresetID preset) => preset switch
    {
        MosaicPresetID.ZenArc => ZenArcLayout(),
        MosaicPresetID.Chrome => ChromeLayout(),
        MosaicPresetID.Opera => OperaLayout(),
        _ => EdgeLayout(),
    };

    private static Dictionary<ShellRegion, List<ShellComponent>> R(params (ShellRegion, ShellComponent[])[] regions) =>
        regions.ToDictionary(r => r.Item1, r => r.Item2.ToList());

    /// <summary>
    /// Arc / Zen: no toolbar. The sidebar holds navigation, a compact address field, pinned
    /// favourites, the tab list and workspaces; the page floats in a rounded card on a soft gradient.
    /// </summary>
    private static MosaicLayout ZenArcLayout() => new()
    {
        Preset = MosaicPresetID.ZenArc, Name = "Zen / Arc",
        TabPlacement = TabPlacement.SidebarLeading, TabStyle = TabStyle.Opera, AddressBar = AddressBarStyle.Compact,
        Regions = R(
            (ShellRegion.SidebarTop, [ShellComponent.SidebarToggle, ShellComponent.FlexibleSpace, ShellComponent.Back, ShellComponent.Forward, ShellComponent.Reload]),
            (ShellRegion.SidebarMiddle, [ShellComponent.AddressField, ShellComponent.BookmarkStar]),
            (ShellRegion.SidebarBottom, [ShellComponent.Workspaces, ShellComponent.FlexibleSpace, ShellComponent.Blocker, ShellComponent.Downloads, ShellComponent.Menu, ShellComponent.NewTab])),
        BookmarksBar = false, Sidebar = SidebarBehavior.Collapsible, SidebarWidth = 248,
        Appearance = new MosaicAppearance
        {
            Density = ShellDensity.Regular, CornerRadius = 9, ContentInset = 8, ContentCornerRadius = 11,
            Accent = "#7C6CF2", ColorScheme = ColorSchemePreference.System, Material = ChromeMaterial.LiquidGlass,
            Background = new BackgroundSpec(BackgroundKind.Gradient, "#E9E4FB", new GradientSpec(GradientKind.Linear, 125,
                new GradientStop("#C9BFF7", 0), new GradientStop("#E7D6F5", 0.55), new GradientStop("#BFD3F5", 1))),
            ToolbarSurface = false, Glow = false,
        },
    };

    /// <summary>
    /// Chrome: tabs in the title-bar row with the new-tab button after them; the omnibox row;
    /// bookmarks bar; the page flush with the frame.
    /// </summary>
    private static MosaicLayout ChromeLayout() => new()
    {
        Preset = MosaicPresetID.Chrome, Name = "Chrome",
        TabPlacement = TabPlacement.Top, TabStyle = TabStyle.Chrome, AddressBar = AddressBarStyle.Omnibox,
        Regions = R(
            (ShellRegion.TitleBar, [ShellComponent.TabStrip, ShellComponent.NewTab, ShellComponent.FlexibleSpace]),
            (ShellRegion.Toolbar, [ShellComponent.Back, ShellComponent.Forward, ShellComponent.Reload, ShellComponent.Home,
                ShellComponent.AddressField, ShellComponent.BookmarkStar, ShellComponent.EngineSwitch, ShellComponent.Blocker,
                ShellComponent.Extensions, ShellComponent.Downloads, ShellComponent.Menu])),
        BookmarksBar = true, Sidebar = SidebarBehavior.Collapsible, SidebarWidth = 260,
        Appearance = new MosaicAppearance
        {
            Density = ShellDensity.Regular, CornerRadius = 8, ContentInset = 0, ContentCornerRadius = 0,
            Accent = "#1A73E8", ColorScheme = ColorSchemePreference.System, Material = ChromeMaterial.Solid,
            Background = new BackgroundSpec(BackgroundKind.System), ToolbarSurface = true, Glow = false,
        },
    };

    /// <summary>Opera: rounded tabs on top, an address row, a slim quick-access rail; a slightly inset page.</summary>
    private static MosaicLayout OperaLayout() => new()
    {
        Preset = MosaicPresetID.Opera, Name = "Opera",
        TabPlacement = TabPlacement.Top, TabStyle = TabStyle.Opera, AddressBar = AddressBarStyle.Omnibox,
        Regions = R(
            (ShellRegion.TitleBar, [ShellComponent.TabStrip, ShellComponent.NewTab, ShellComponent.FlexibleSpace]),
            (ShellRegion.Toolbar, [ShellComponent.Back, ShellComponent.Forward, ShellComponent.Reload, ShellComponent.AddressField,
                ShellComponent.BookmarkStar, ShellComponent.EngineSwitch, ShellComponent.Blocker, ShellComponent.Extensions,
                ShellComponent.Downloads, ShellComponent.Menu]),
            (ShellRegion.Rail, [ShellComponent.Home, ShellComponent.BookmarksPage, ShellComponent.HistoryPage, ShellComponent.ReadingListPage,
                ShellComponent.DownloadsPage, ShellComponent.FlexibleSpace, ShellComponent.Settings])),
        BookmarksBar = false, Sidebar = SidebarBehavior.Collapsible, SidebarWidth = 260,
        Appearance = new MosaicAppearance
        {
            Density = ShellDensity.Regular, CornerRadius = 8, ContentInset = 6, ContentCornerRadius = 8,
            Accent = "#FF1B2D", ColorScheme = ColorSchemePreference.System, Material = ChromeMaterial.LiquidGlass,
            Background = new BackgroundSpec(BackgroundKind.System), ToolbarSurface = false, Glow = false,
        },
    };

    /// <summary>
    /// Microsoft Edge (Windows only): workspaces and the tab actions button before rounded tabs in
    /// the title bar; back/forward/reload/home and a rounded address bar with the star inside;
    /// Edge's sidebar of quick pages down the right; the page in a lightly inset rounded card on
    /// Mica. The tab actions button (or Settings) switches to vertical tabs.
    /// </summary>
    private static MosaicLayout EdgeLayout() => new()
    {
        Preset = MosaicPresetID.Edge, Name = "Microsoft Edge",
        TabPlacement = TabPlacement.Top, TabStyle = TabStyle.Opera, AddressBar = AddressBarStyle.Omnibox,
        Regions = R(
            (ShellRegion.TitleBar, [ShellComponent.Workspaces, ShellComponent.VerticalTabs, ShellComponent.TabStrip, ShellComponent.NewTab, ShellComponent.FlexibleSpace]),
            (ShellRegion.Toolbar, [ShellComponent.Back, ShellComponent.Forward, ShellComponent.Reload, ShellComponent.Home,
                ShellComponent.AddressField, ShellComponent.BookmarkStar, ShellComponent.EngineSwitch, ShellComponent.Blocker,
                ShellComponent.Extensions, ShellComponent.Downloads, ShellComponent.Menu]),
            (ShellRegion.TrailingRail, [ShellComponent.SearchEngine, ShellComponent.BookmarksPage, ShellComponent.HistoryPage,
                ShellComponent.ReadingListPage, ShellComponent.DownloadsPage, ShellComponent.FlexibleSpace, ShellComponent.Settings])),
        BookmarksBar = false, Sidebar = SidebarBehavior.Collapsible, SidebarWidth = 264,
        Appearance = new MosaicAppearance
        {
            Density = ShellDensity.Regular, CornerRadius = 8, ContentInset = 4, ContentCornerRadius = 8,
            Accent = HexColor.System, ColorScheme = ColorSchemePreference.System, Material = ChromeMaterial.LiquidGlass,
            Background = new BackgroundSpec(BackgroundKind.System), ToolbarSurface = false, Glow = false,
        },
    };

    /// <summary>
    /// macOS's Safari preset, kept only to read Safari layouts from macOS with the right
    /// defaults before <see cref="FromSafari"/> turns them into Edge. Never shown on Windows.
    /// </summary>
    internal static MosaicLayout SafariLayout() => new()
    {
        Preset = MosaicPresetID.Chrome, Name = "Safari",
        TabPlacement = TabPlacement.Top, TabStyle = TabStyle.Safari, AddressBar = AddressBarStyle.Unified,
        Regions = R(
            (ShellRegion.TitleBar, [ShellComponent.SidebarToggle, ShellComponent.Back, ShellComponent.Forward, ShellComponent.FlexibleSpace,
                ShellComponent.AddressField, ShellComponent.FlexibleSpace, ShellComponent.Blocker, ShellComponent.Downloads,
                ShellComponent.NewTab, ShellComponent.Menu]),
            (ShellRegion.TabBar, [ShellComponent.TabStrip]),
            (ShellRegion.SidebarTop, [ShellComponent.FlexibleSpace, ShellComponent.Home]),
            (ShellRegion.SidebarBottom, [ShellComponent.Workspaces, ShellComponent.FlexibleSpace, ShellComponent.Settings])),
        BookmarksBar = false, Sidebar = SidebarBehavior.Collapsible, SidebarWidth = 240,
        Appearance = new MosaicAppearance
        {
            Density = ShellDensity.Regular, CornerRadius = 8, ContentInset = 0, ContentCornerRadius = 0,
            Accent = HexColor.System, ColorScheme = ColorSchemePreference.System, Material = ChromeMaterial.LiquidGlass,
            Background = new BackgroundSpec(BackgroundKind.System), ToolbarSurface = false, Glow = false,
        },
    };

    /// <summary>
    /// The Windows stand-in for a Safari layout from macOS: the Edge preset (Windows's own
    /// browser, as Safari is the Mac's), vertical if the Safari layout had vertical tabs, keeping
    /// the person's look: density, corner radius, accent, light/dark, material, backgrounds,
    /// glow, and whether the bookmarks bar shows.
    /// </summary>
    public static MosaicLayout FromSafari(MosaicLayout safari)
    {
        var edge = EdgeLayout();
        if (safari.TabPlacement.IsVertical()) edge = edge.WithTabPlacement(safari.TabPlacement);
        edge.BookmarksBar = safari.BookmarksBar;
        var a = safari.Appearance;
        var e = edge.Appearance;
        e.Density = a.Density;
        e.CornerRadius = a.CornerRadius;
        e.Accent = a.Accent;
        e.ColorScheme = a.ColorScheme;
        e.Material = a.Material;
        e.Background = a.Background.Clone();
        e.NewTabBackground = a.NewTabBackground.Clone();
        e.WorkspaceBackgrounds = a.WorkspaceBackgrounds.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        e.Glow = a.Glow;
        return edge;
    }

    /// <summary>Opera GX styling applied to any layout: dark, neon accent, a dark gradient and a glow.</summary>
    public MosaicLayout WithGXStyling()
    {
        var layout = Clone();
        layout.Appearance.ColorScheme = ColorSchemePreference.Dark;
        layout.Appearance.Accent = "#FA1E4E";
        layout.Appearance.Glow = true;
        layout.Appearance.Background = new BackgroundSpec(BackgroundKind.Gradient, "#121019", new GradientSpec(GradientKind.Linear, 160,
            new GradientStop("#1B1024", 0), new GradientStop("#0F0E15", 0.6), new GradientStop("#24101A", 1)));
        return layout;
    }

    /// <summary>GX styling off: the preset's own scheme, accent, glow and background.</summary>
    public MosaicLayout WithoutGXStyling()
    {
        var layout = Clone();
        var defaults = Factory(Preset).Appearance;
        layout.Appearance.ColorScheme = defaults.ColorScheme;
        layout.Appearance.Accent = defaults.Accent;
        layout.Appearance.Glow = defaults.Glow;
        layout.Appearance.Background = defaults.Background;
        return layout;
    }
}
