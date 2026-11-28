using System.Globalization;
using System.Text.Json.Nodes;

// INTERIM COPY of the shell's layout model (macOS: Sources/MosaicCore/Shell/MosaicLayout.swift),
// so the canvas can be built and tested before the shell area's Mosaic.Core.Shell model lands
// in win-next. It is a line-for-line port with the same JSON (keys, raw values, forgiving
// decoding), verified against files written by the macOS app (tests/…/Canvas/Fixtures).
//
// When the shell's model lands: delete this folder, change `using Mosaic.Core.Canvas.Interim;`
// to `using Mosaic.Core.Shell;` in the canvas files, and map any renamed members in
// Canvas/ShellModelShim.cs. The canvas only uses the members listed there.
//
// Owner: canvas (temporarily; the model itself belongs to the shell area).

namespace Mosaic.Core.Canvas.Interim;

/// <summary>JSON raw values of the model's enums: the case name with a lower-case first letter (Swift's raw values).</summary>
public static class RawValues
{
    private static class Table<T> where T : struct, Enum
    {
        public static readonly (T Value, string Raw)[] Entries =
            [.. Enum.GetValues<T>().Select(v => (v, char.ToLowerInvariant(v.ToString()[0]) + v.ToString()[1..]))];
    }

    public static string Raw<T>(this T value) where T : struct, Enum
    {
        foreach (var (v, raw) in Table<T>.Entries)
        {
            if (EqualityComparer<T>.Default.Equals(v, value)) return raw;
        }
        return value.ToString();
    }

    /// <summary>The case for a raw value (case-sensitive, as in Swift), or null.</summary>
    public static T? Parse<T>(string? raw) where T : struct, Enum
    {
        if (raw is null) return null;
        foreach (var (v, r) in Table<T>.Entries)
        {
            if (r == raw) return v;
        }
        return null;
    }

    public static IReadOnlyList<T> All<T>() where T : struct, Enum => [.. Table<T>.Entries.Select(e => e.Value)];
}

/// <summary>The built-in starting points (macOS has these four; the shell adds Edge on Windows).</summary>
public enum MosaicPresetID { ZenArc, Chrome, Opera, Safari }

/// <summary>Every element the shell can place; order = Swift's <c>allCases</c> (the palette's order).</summary>
public enum ShellComponent
{
    Back, Forward, Reload, Home, AddressField, TabStrip, SidebarToggle, NewTab, Downloads, Blocker,
    Extensions, Settings, BookmarkStar, EngineSwitch, SearchEngine, Workspaces, Menu, FlexibleSpace,
    BookmarksPage, HistoryPage, ReadingListPage, DownloadsPage,
}

/// <summary>The places components can go; order = Swift's <c>allCases</c>.</summary>
public enum ShellRegion { TitleBar, Toolbar, TabBar, SidebarTop, SidebarMiddle, SidebarBottom, Rail }

public enum TabPlacement { Top, SidebarLeading, SidebarTrailing }

public enum TabStyle { Chrome, Opera, Safari }

public enum AddressBarStyle { Omnibox, Compact, Unified }

public enum SidebarBehavior { Shown, Collapsible, AutoHide }

public enum ShellDensity { Compact, Regular, Comfortable }

public enum ColorSchemePreference { System, Light, Dark }

public enum ChromeMaterial { LiquidGlass, Solid }

public static class ShellModelText
{
    public static string Title(this MosaicPresetID preset) => preset switch
    {
        MosaicPresetID.ZenArc => "Zen / Arc",
        MosaicPresetID.Chrome => "Chrome",
        MosaicPresetID.Opera => "Opera",
        _ => "Safari",
    };

    public static string Title(this ShellComponent component) => component switch
    {
        ShellComponent.Back => "Back",
        ShellComponent.Forward => "Forward",
        ShellComponent.Reload => "Reload",
        ShellComponent.Home => "Home",
        ShellComponent.AddressField => "Address Field",
        ShellComponent.TabStrip => "Tab Strip",
        ShellComponent.SidebarToggle => "Sidebar",
        ShellComponent.NewTab => "New Tab",
        ShellComponent.Downloads => "Downloads",
        ShellComponent.Blocker => "Content Blocker",
        ShellComponent.Extensions => "Extensions",
        ShellComponent.Settings => "Settings",
        ShellComponent.BookmarkStar => "Bookmark",
        ShellComponent.EngineSwitch => "Engine",
        ShellComponent.SearchEngine => "Search Engine",
        ShellComponent.Workspaces => "Workspaces",
        ShellComponent.Menu => "Menu",
        ShellComponent.FlexibleSpace => "Flexible Space",
        ShellComponent.BookmarksPage => "Bookmarks",
        ShellComponent.HistoryPage => "History",
        ShellComponent.ReadingListPage => "Reading List",
        _ => "Downloads Page",
    };

    /// <summary>The SF Symbol name macOS uses; <c>Icons.Make</c> on Windows takes the same names.</summary>
    public static string Symbol(this ShellComponent component) => component switch
    {
        ShellComponent.Back => "chevron.left",
        ShellComponent.Forward => "chevron.right",
        ShellComponent.Reload => "arrow.clockwise",
        ShellComponent.Home => "house",
        ShellComponent.AddressField => "magnifyingglass",
        ShellComponent.TabStrip => "rectangle.topthird.inset.filled",
        ShellComponent.SidebarToggle => "sidebar.left",
        ShellComponent.NewTab => "plus",
        ShellComponent.Downloads => "arrow.down.circle",
        ShellComponent.Blocker => "shield.lefthalf.filled",
        ShellComponent.Extensions => "puzzlepiece.extension",
        ShellComponent.Settings => "gearshape",
        ShellComponent.BookmarkStar => "star",
        ShellComponent.EngineSwitch => "circle.lefthalf.filled",
        ShellComponent.SearchEngine => "text.magnifyingglass",
        ShellComponent.Workspaces => "square.stack",
        ShellComponent.Menu => "ellipsis",
        ShellComponent.FlexibleSpace => "arrow.left.and.right",
        ShellComponent.BookmarksPage => "book",
        ShellComponent.HistoryPage => "clock",
        ShellComponent.ReadingListPage => "eyeglasses",
        _ => "arrow.down.doc",
    };

    public static bool IsFlexible(this ShellComponent c) =>
        c is ShellComponent.AddressField or ShellComponent.TabStrip or ShellComponent.FlexibleSpace;

    public static bool NeedsWideRow(this ShellComponent c) => c is ShellComponent.AddressField or ShellComponent.TabStrip;

    public static string Title(this ShellRegion region) => region switch
    {
        ShellRegion.TitleBar => "Title Bar",
        ShellRegion.Toolbar => "Toolbar",
        ShellRegion.TabBar => "Tab Bar",
        ShellRegion.SidebarTop => "Sidebar Top",
        ShellRegion.SidebarMiddle => "Sidebar Middle",
        ShellRegion.SidebarBottom => "Sidebar Bottom",
        _ => "Quick-Access Rail",
    };

    public static bool IsSidebar(this ShellRegion r) =>
        r is ShellRegion.SidebarTop or ShellRegion.SidebarMiddle or ShellRegion.SidebarBottom;

    public static bool Accepts(this ShellRegion region, ShellComponent component) => region switch
    {
        ShellRegion.Rail => !component.NeedsWideRow(),
        ShellRegion.SidebarTop or ShellRegion.SidebarMiddle or ShellRegion.SidebarBottom => component != ShellComponent.TabStrip,
        _ => true,
    };

    public static bool IsVertical(this TabPlacement p) => p != TabPlacement.Top;

    public static string Title(this TabPlacement p) => p switch
    {
        TabPlacement.Top => "Top",
        TabPlacement.SidebarLeading => "Left Sidebar",
        _ => "Right Sidebar",
    };

    public static string Title(this TabStyle s) => s switch { TabStyle.Chrome => "Shaped", TabStyle.Opera => "Rounded", _ => "Segmented" };

    public static string Title(this AddressBarStyle s) => s switch { AddressBarStyle.Omnibox => "Wide", AddressBarStyle.Compact => "Compact", _ => "Centred" };

    public static string Title(this SidebarBehavior s) => s switch
    {
        SidebarBehavior.Shown => "Always Shown",
        SidebarBehavior.Collapsible => "Collapsible",
        _ => "Auto-Hide",
    };
}

/// <summary>Colours as <c>#RRGGBB</c> / <c>#RRGGBBAA</c>, or "system".</summary>
public static class HexColor
{
    public const string System = "system";

    public static (double Red, double Green, double Blue, double Alpha)? Components(string? hex)
    {
        if (hex is null) return null;
        var text = hex.Trim(' ', '\t');
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length is not (6 or 8) || !text.All(Uri.IsHexDigit)) return null;
        var value = ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var alpha = text.Length == 8;
        double Byte(int shift) => ((value >> shift) & 0xFF) / 255.0;
        return (Byte(alpha ? 24 : 16), Byte(alpha ? 16 : 8), Byte(alpha ? 8 : 0), alpha ? Byte(0) : 1);
    }

    public static bool IsValid(string? hex) => hex == System || Components(hex) is not null;

    public static string String(double red, double green, double blue, double alpha = 1)
    {
        static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
        var b = $"#{Byte(red):X2}{Byte(green):X2}{Byte(blue):X2}";
        return alpha >= 0.999 ? b : b + $"{Byte(alpha):X2}";
    }
}

public sealed record GradientStop(string Color, double Location);

/// <summary>A gradient description (shell type, extended by the Gradient Studio).</summary>
public sealed class GradientSpec : IEquatable<GradientSpec>
{
    public enum Kind { Linear, Radial, Angular, Mesh }

    public Kind GradientKind { get; set; } = Kind.Linear;
    /// <summary>Degrees; 0 runs left to right, 90 top to bottom.</summary>
    public double Angle { get; set; } = 135;
    public List<GradientStop> Stops { get; set; } = [];
    public List<GradientSource> Sources { get; set; } = [];
    public double Sharpness { get; set; } = 2.2;
    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.5;
    public double Noise { get; set; }
    public double Opacity { get; set; } = 1;
    public bool Raw { get; set; }

    public GradientSpec() { }

    public GradientSpec(Kind kind = Kind.Linear, double angle = 135, IEnumerable<GradientStop>? stops = null,
                        IEnumerable<GradientSource>? sources = null, double sharpness = 2.2, double centerX = 0.5,
                        double centerY = 0.5, double noise = 0, double opacity = 1, bool raw = false)
    {
        GradientKind = kind;
        Angle = angle;
        Stops = stops?.ToList() ?? [];
        Sources = sources?.ToList() ?? [];
        Sharpness = sharpness;
        CenterX = centerX;
        CenterY = centerY;
        Noise = noise;
        Opacity = opacity;
        Raw = raw;
    }

    public GradientSpec Clone() => new()
    {
        GradientKind = GradientKind, Angle = Angle, Stops = [.. Stops], Sources = [.. Sources], Sharpness = Sharpness,
        CenterX = CenterX, CenterY = CenterY, Noise = Noise, Opacity = Opacity, Raw = Raw,
    };

    /// <summary>Stops sorted by location, clamped to 0…1, invalid colours removed; at least two.</summary>
    public List<GradientStop> NormalizedStops
    {
        get
        {
            var result = Stops.Where(s => HexColor.Components(s.Color) is not null)
                .Select(s => new GradientStop(s.Color, Math.Min(Math.Max(s.Location, 0), 1)))
                .OrderBy(s => s.Location)  // stable, like Swift's sort for equal keys in practice
                .ToList();
            if (result.Count == 0) result.Add(new GradientStop("#808080", 0));
            if (result.Count == 1) result.Add(new GradientStop(result[0].Color, 1));
            return result;
        }
    }

    public bool Equals(GradientSpec? other) =>
        other is not null && GradientKind == other.GradientKind && Angle.Equals(other.Angle) && Stops.SequenceEqual(other.Stops)
        && Sources.SequenceEqual(other.Sources) && Sharpness.Equals(other.Sharpness) && CenterX.Equals(other.CenterX)
        && CenterY.Equals(other.CenterY) && Noise.Equals(other.Noise) && Opacity.Equals(other.Opacity) && Raw == other.Raw;

    public override bool Equals(object? obj) => Equals(obj as GradientSpec);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GradientKind);
        hash.Add(Angle);
        foreach (var s in Stops) hash.Add(s);
        foreach (var s in Sources) hash.Add(s);
        hash.Add(Sharpness);
        hash.Add(CenterX);
        hash.Add(CenterY);
        hash.Add(Noise);
        hash.Add(Opacity);
        hash.Add(Raw);
        return hash.ToHashCode();
    }

    public static bool operator ==(GradientSpec? a, GradientSpec? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(GradientSpec? a, GradientSpec? b) => !(a == b);
}

public sealed class BackgroundSpec : IEquatable<BackgroundSpec>
{
    public enum Kind { System, Solid, Gradient }

    public Kind BackgroundKind { get; set; } = Kind.System;
    public string Color { get; set; } = "#F2F2F7";
    public GradientSpec Gradient { get; set; } = new();

    public BackgroundSpec() { }

    public BackgroundSpec(Kind kind = Kind.System, string color = "#F2F2F7", GradientSpec? gradient = null)
    {
        BackgroundKind = kind;
        Color = color;
        Gradient = gradient ?? new GradientSpec();
    }

    public BackgroundSpec Clone() => new(BackgroundKind, Color, Gradient.Clone());

    public bool Equals(BackgroundSpec? o) => o is not null && BackgroundKind == o.BackgroundKind && Color == o.Color && Gradient == o.Gradient;
    public override bool Equals(object? obj) => Equals(obj as BackgroundSpec);
    public override int GetHashCode() => HashCode.Combine(BackgroundKind, Color, Gradient);
    public static bool operator ==(BackgroundSpec? a, BackgroundSpec? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(BackgroundSpec? a, BackgroundSpec? b) => !(a == b);
}

public sealed class MosaicAppearance : IEquatable<MosaicAppearance>
{
    public ShellDensity Density { get; set; } = ShellDensity.Regular;
    public double CornerRadius { get; set; } = 8;
    public double ContentInset { get; set; }
    public double ContentCornerRadius { get; set; }
    public string Accent { get; set; } = HexColor.System;
    public ColorSchemePreference ColorScheme { get; set; } = ColorSchemePreference.System;
    public ChromeMaterial Material { get; set; } = ChromeMaterial.LiquidGlass;
    public BackgroundSpec Background { get; set; } = new();
    public bool ToolbarSurface { get; set; }
    public bool Glow { get; set; }
    public NewTabBackground NewTabBackground { get; set; } = new();
    public Dictionary<string, BackgroundSpec> WorkspaceBackgrounds { get; set; } = [];

    public MosaicAppearance Clone() => new()
    {
        Density = Density, CornerRadius = CornerRadius, ContentInset = ContentInset, ContentCornerRadius = ContentCornerRadius,
        Accent = Accent, ColorScheme = ColorScheme, Material = Material, Background = Background.Clone(),
        ToolbarSurface = ToolbarSurface, Glow = Glow, NewTabBackground = NewTabBackground.Clone(),
        WorkspaceBackgrounds = WorkspaceBackgrounds.ToDictionary(p => p.Key, p => p.Value.Clone()),
    };

    public bool Equals(MosaicAppearance? o) =>
        o is not null && Density == o.Density && CornerRadius.Equals(o.CornerRadius) && ContentInset.Equals(o.ContentInset)
        && ContentCornerRadius.Equals(o.ContentCornerRadius) && Accent == o.Accent && ColorScheme == o.ColorScheme
        && Material == o.Material && Background == o.Background && ToolbarSurface == o.ToolbarSurface && Glow == o.Glow
        && NewTabBackground == o.NewTabBackground && WorkspaceBackgrounds.Count == o.WorkspaceBackgrounds.Count
        && WorkspaceBackgrounds.All(p => o.WorkspaceBackgrounds.TryGetValue(p.Key, out var v) && v == p.Value);

    public override bool Equals(object? obj) => Equals(obj as MosaicAppearance);
    public override int GetHashCode() => HashCode.Combine(Density, CornerRadius, Accent, Background, NewTabBackground, WorkspaceBackgrounds.Count);
    public static bool operator ==(MosaicAppearance? a, MosaicAppearance? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(MosaicAppearance? a, MosaicAppearance? b) => !(a == b);
}

/// <summary>
/// The whole window's chrome. A mutable class with value equality (Swift's struct): mutate a
/// <see cref="Clone"/>, never a layout someone else holds.
/// </summary>
public sealed class MosaicLayout : IEquatable<MosaicLayout>
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public MosaicPresetID Preset { get; set; }
    public string Name { get; set; } = "";
    public TabPlacement TabPlacement { get; set; }
    public TabStyle TabStyle { get; set; }
    public AddressBarStyle AddressBar { get; set; }
    /// <summary>Components per region, in order. Missing regions are empty.</summary>
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

    public bool Contains(ShellComponent component) => Regions.Values.Any(l => l.Contains(component));

    public ShellRegion? RegionOf(ShellComponent component)
    {
        foreach (var region in Enum.GetValues<ShellRegion>())
        {
            if (Components(region).Contains(component)) return region;
        }
        return null;
    }

    public bool HasSidebar => TabPlacement.IsVertical() || Contains(ShellComponent.SidebarToggle);

    // MARK: Mutation

    /// <summary>Inserts (or moves, all but flexible space appear once) <paramref name="component"/>.</summary>
    public bool Insert(ShellComponent component, ShellRegion region, int? index = null)
    {
        if (!region.Accepts(component)) return false;
        var target = index ?? Components(region).Count;
        if (component != ShellComponent.FlexibleSpace && RegionOf(component) is { } from)
        {
            var fromIndex = Regions[from].IndexOf(component);
            Regions[from].RemoveAt(fromIndex);
            if (from == region && fromIndex < target) target -= 1;
        }
        var list = Components(region).ToList();
        list.Insert(Math.Min(Math.Max(target, 0), list.Count), component);
        Regions[region] = list;
        return true;
    }

    public ShellComponent? RemoveAt(int index, ShellRegion region)
    {
        if (!Regions.TryGetValue(region, out var list) || index < 0 || index >= list.Count) return null;
        var removed = list[index];
        list.RemoveAt(index);
        return removed;
    }

    public void Remove(ShellComponent component)
    {
        foreach (var list in Regions.Values) list.RemoveAll(c => c == component);
    }

    public bool Move(ShellRegion from, int index, ShellRegion to, int toIndex)
    {
        var source = Components(from);
        if (index < 0 || index >= source.Count) return false;
        var component = source[index];
        if (!to.Accepts(component)) return false;
        Regions[from].RemoveAt(index);
        var list = Components(to).ToList();
        list.Insert(Math.Min(Math.Max(toIndex, 0), list.Count), component);
        Regions[to] = list;
        return true;
    }

    /// <summary>Where the tabs are, moving navigation so it stays reachable (see the Swift original).</summary>
    public MosaicLayout WithTabPlacement(TabPlacement placement)
    {
        if (placement == TabPlacement) return Clone();
        var layout = Clone();
        var wasVertical = TabPlacement.IsVertical();
        layout.TabPlacement = placement;
        if (wasVertical && !placement.IsVertical())
        {
            var moving = Components(ShellRegion.SidebarTop).Concat(Components(ShellRegion.SidebarMiddle))
                .Where(c => c != ShellComponent.FlexibleSpace && c != ShellComponent.SidebarToggle).ToList();
            foreach (var component in moving) layout.Insert(component, ShellRegion.Toolbar);
            if (!layout.Contains(ShellComponent.TabStrip)) layout.Insert(ShellComponent.TabStrip, ShellRegion.TitleBar, 0);
            if (layout.Components(ShellRegion.TitleBar).SequenceEqual([ShellComponent.TabStrip]))
                layout.Insert(ShellComponent.NewTab, ShellRegion.TitleBar);
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
                        layout.Insert(ShellComponent.BookmarkStar, ShellRegion.SidebarMiddle);
                }
                if (layout.Regions.TryGetValue(ShellRegion.Toolbar, out var tb)) tb.RemoveAll(c => navigation.Contains(c));
                var rest = layout.Components(ShellRegion.Toolbar).Where(c => c != ShellComponent.FlexibleSpace).ToList();
                if (layout.Components(ShellRegion.SidebarBottom).Count == 0 && rest.Count > 0)
                {
                    layout.Regions[ShellRegion.Toolbar] = [];
                    layout.Regions[ShellRegion.SidebarBottom] =
                    [
                        ShellComponent.Workspaces, ShellComponent.FlexibleSpace,
                        .. rest.Where(c => c != ShellComponent.Workspaces && c != ShellComponent.NewTab), ShellComponent.NewTab,
                    ];
                }
            }
            ShellComponent[] stripOnly = [ShellComponent.TabStrip, ShellComponent.NewTab, ShellComponent.FlexibleSpace];
            if (layout.Components(ShellRegion.TitleBar).All(stripOnly.Contains))
            {
                if (layout.Components(ShellRegion.TitleBar).Contains(ShellComponent.NewTab)
                    && layout.RegionOf(ShellComponent.NewTab) == ShellRegion.TitleBar)
                    layout.Insert(ShellComponent.NewTab, ShellRegion.SidebarBottom);
                layout.Regions[ShellRegion.TitleBar] = [];
            }
        }
        return layout.Normalized();
    }

    /// <summary>Repairs anything that would leave the window unusable or is out of range.</summary>
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
                if (component == ShellComponent.FlexibleSpace && list.Count > 0 && list[^1] == ShellComponent.FlexibleSpace) continue;
                list.Add(component);
            }
            if (list.Count > 0) cleaned[region] = list;
        }
        layout.Regions = cleaned;

        if (!layout.Contains(ShellComponent.AddressField))
            layout.Insert(ShellComponent.AddressField, layout.TabPlacement.IsVertical() ? ShellRegion.SidebarMiddle : ShellRegion.Toolbar);
        if (layout.TabPlacement == TabPlacement.Top && layout.RegionOf(ShellComponent.AddressField)?.IsSidebar() == true)
            layout.Insert(ShellComponent.AddressField, ShellRegion.Toolbar);
        if (layout.TabPlacement == TabPlacement.Top)
        {
            var strip = layout.RegionOf(ShellComponent.TabStrip);
            if (strip is null || strip.Value.IsSidebar() || strip == ShellRegion.Rail)
                layout.Insert(ShellComponent.TabStrip, ShellRegion.TitleBar, 0);
        }
        if (layout.TabPlacement.IsVertical() && layout.Sidebar == SidebarBehavior.Collapsible && !layout.Contains(ShellComponent.SidebarToggle))
            layout.Insert(ShellComponent.SidebarToggle, ShellRegion.SidebarTop, 0);

        static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Min(Math.Max(v, lo), hi) : fallback;
        layout.SidebarWidth = Math.Min(Math.Max(double.IsFinite(layout.SidebarWidth) ? layout.SidebarWidth : 240, 180), 420);
        var a = layout.Appearance;
        a.CornerRadius = Clamp(a.CornerRadius, 0, 20, 8);
        a.ContentInset = Clamp(a.ContentInset, 0, 24, 0);
        a.ContentCornerRadius = Clamp(a.ContentCornerRadius, 0, 28, 0);
        if (!HexColor.IsValid(a.Accent)) a.Accent = HexColor.System;
        if (HexColor.Components(a.Background.Color) is null) a.Background.Color = "#F2F2F7";
        a.Background.Gradient.Angle = double.IsFinite(a.Background.Gradient.Angle) ? a.Background.Gradient.Angle : 135;
        layout.Appearance = a.ClampedBackgrounds();  // canvas: Gradient Studio bounds
        return layout;
    }

    // MARK: Equality

    public bool Equals(MosaicLayout? o)
    {
        if (o is null) return false;
        if (SchemaVersion != o.SchemaVersion || Preset != o.Preset || Name != o.Name || TabPlacement != o.TabPlacement
            || TabStyle != o.TabStyle || AddressBar != o.AddressBar || BookmarksBar != o.BookmarksBar || Sidebar != o.Sidebar
            || !SidebarWidth.Equals(o.SidebarWidth) || Appearance != o.Appearance || Regions.Count != o.Regions.Count)
            return false;
        foreach (var (region, list) in Regions)
        {
            if (!o.Regions.TryGetValue(region, out var other) || !list.SequenceEqual(other)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as MosaicLayout);
    public override int GetHashCode() => HashCode.Combine(Preset, Name, TabPlacement, Regions.Count, Appearance);
    public static bool operator ==(MosaicLayout? a, MosaicLayout? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(MosaicLayout? a, MosaicLayout? b) => !(a == b);

    // MARK: JSON

    public JsonObject ToJson() => LayoutJson.Encode(this);

    public string JsonText(bool pretty = true) => LayoutJson.Write(ToJson(), pretty);

    /// <summary>Decodes (forgivingly) and normalises, like <c>MosaicLayout.from(json:)</c>.</summary>
    public static MosaicLayout FromJson(string json) => LayoutJson.DecodeLayout(JsonNode.Parse(json)).Normalized();

    // MARK: Presets

    public static MosaicLayout PresetLayout(MosaicPresetID preset) => preset switch
    {
        MosaicPresetID.ZenArc => ZenArc(),
        MosaicPresetID.Chrome => Chrome(),
        MosaicPresetID.Opera => Opera(),
        _ => Safari(),
    };

    private static Dictionary<ShellRegion, List<ShellComponent>> R(params (ShellRegion, ShellComponent[])[] items) =>
        items.ToDictionary(i => i.Item1, i => i.Item2.ToList());

    private static MosaicLayout ZenArc() => new()
    {
        Preset = MosaicPresetID.ZenArc, Name = "Zen / Arc", TabPlacement = TabPlacement.SidebarLeading, TabStyle = TabStyle.Opera,
        AddressBar = AddressBarStyle.Compact,
        Regions = R(
            (ShellRegion.SidebarTop, [ShellComponent.SidebarToggle, ShellComponent.FlexibleSpace, ShellComponent.Back, ShellComponent.Forward, ShellComponent.Reload]),
            (ShellRegion.SidebarMiddle, [ShellComponent.AddressField, ShellComponent.BookmarkStar]),
            (ShellRegion.SidebarBottom, [ShellComponent.Workspaces, ShellComponent.FlexibleSpace, ShellComponent.Blocker, ShellComponent.Downloads, ShellComponent.Menu, ShellComponent.NewTab])),
        BookmarksBar = false, Sidebar = SidebarBehavior.Collapsible, SidebarWidth = 248,
        Appearance = new MosaicAppearance
        {
            Density = ShellDensity.Regular, CornerRadius = 9, ContentInset = 8, ContentCornerRadius = 11, Accent = "#7C6CF2",
            ColorScheme = ColorSchemePreference.System, Material = ChromeMaterial.LiquidGlass,
            Background = new BackgroundSpec(BackgroundSpec.Kind.Gradient, "#E9E4FB", new GradientSpec(GradientSpec.Kind.Linear, 125,
                [new("#C9BFF7", 0), new("#E7D6F5", 0.55), new("#BFD3F5", 1)])),
        },
    };

    private static MosaicLayout Chrome() => new()
    {
        Preset = MosaicPresetID.Chrome, Name = "Chrome", TabPlacement = TabPlacement.Top, TabStyle = TabStyle.Chrome,
        AddressBar = AddressBarStyle.Omnibox,
        Regions = R(
            (ShellRegion.TitleBar, [ShellComponent.TabStrip, ShellComponent.NewTab, ShellComponent.FlexibleSpace]),
            (ShellRegion.Toolbar, [ShellComponent.Back, ShellComponent.Forward, ShellComponent.Reload, ShellComponent.Home, ShellComponent.AddressField,
                ShellComponent.BookmarkStar, ShellComponent.EngineSwitch, ShellComponent.Blocker, ShellComponent.Extensions, ShellComponent.Downloads, ShellComponent.Menu])),
        BookmarksBar = true, Sidebar = SidebarBehavior.Collapsible, SidebarWidth = 260,
        Appearance = new MosaicAppearance
        {
            Accent = "#1A73E8", Material = ChromeMaterial.Solid, Background = new BackgroundSpec(BackgroundSpec.Kind.System), ToolbarSurface = true,
        },
    };

    private static MosaicLayout Opera() => new()
    {
        Preset = MosaicPresetID.Opera, Name = "Opera", TabPlacement = TabPlacement.Top, TabStyle = TabStyle.Opera,
        AddressBar = AddressBarStyle.Omnibox,
        Regions = R(
            (ShellRegion.TitleBar, [ShellComponent.TabStrip, ShellComponent.NewTab, ShellComponent.FlexibleSpace]),
            (ShellRegion.Toolbar, [ShellComponent.Back, ShellComponent.Forward, ShellComponent.Reload, ShellComponent.AddressField, ShellComponent.BookmarkStar,
                ShellComponent.EngineSwitch, ShellComponent.Blocker, ShellComponent.Extensions, ShellComponent.Downloads, ShellComponent.Menu]),
            (ShellRegion.Rail, [ShellComponent.Home, ShellComponent.BookmarksPage, ShellComponent.HistoryPage, ShellComponent.ReadingListPage,
                ShellComponent.DownloadsPage, ShellComponent.FlexibleSpace, ShellComponent.Settings])),
        BookmarksBar = false, Sidebar = SidebarBehavior.Collapsible, SidebarWidth = 260,
        Appearance = new MosaicAppearance
        {
            ContentInset = 6, ContentCornerRadius = 8, Accent = "#FF1B2D", Material = ChromeMaterial.LiquidGlass,
            Background = new BackgroundSpec(BackgroundSpec.Kind.System),
        },
    };

    private static MosaicLayout Safari() => new()
    {
        Preset = MosaicPresetID.Safari, Name = "Safari", TabPlacement = TabPlacement.Top, TabStyle = TabStyle.Safari,
        AddressBar = AddressBarStyle.Unified,
        Regions = R(
            (ShellRegion.TitleBar, [ShellComponent.SidebarToggle, ShellComponent.Back, ShellComponent.Forward, ShellComponent.FlexibleSpace, ShellComponent.AddressField,
                ShellComponent.FlexibleSpace, ShellComponent.Blocker, ShellComponent.Downloads, ShellComponent.NewTab, ShellComponent.Menu]),
            (ShellRegion.TabBar, [ShellComponent.TabStrip]),
            (ShellRegion.SidebarTop, [ShellComponent.FlexibleSpace, ShellComponent.Home]),
            (ShellRegion.SidebarBottom, [ShellComponent.Workspaces, ShellComponent.FlexibleSpace, ShellComponent.Settings])),
        BookmarksBar = false, Sidebar = SidebarBehavior.Collapsible, SidebarWidth = 240,
        Appearance = new MosaicAppearance { Background = new BackgroundSpec(BackgroundSpec.Kind.System) },
    };

    /// <summary>Opera GX styling applied to any layout.</summary>
    public MosaicLayout WithGXStyling()
    {
        var layout = Clone();
        layout.Appearance.ColorScheme = ColorSchemePreference.Dark;
        layout.Appearance.Accent = "#FA1E4E";
        layout.Appearance.Glow = true;
        layout.Appearance.Background = new BackgroundSpec(BackgroundSpec.Kind.Gradient, "#121019", new GradientSpec(GradientSpec.Kind.Linear, 160,
            [new("#1B1024", 0), new("#0F0E15", 0.6), new("#24101A", 1)]));
        return layout;
    }

    public bool HasGXStyling => Appearance.Glow && Appearance.ColorScheme == ColorSchemePreference.Dark;
}
