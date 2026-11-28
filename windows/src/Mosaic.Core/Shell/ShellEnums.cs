using System.Collections.Concurrent;

namespace Mosaic.Core.Shell;

// The vocabulary of a Mosaic layout. Port of Sources/MosaicCore/Shell/MosaicLayout.swift.
// Every enum's JSON value is its member name with a lower-case first letter (the Swift raw
// value); see JsonEnum. Add members at will, never rename existing ones.
// Owner: shell.

/// <summary>
/// The built-in starting points. Each is fully editable; the user's edits are kept per preset,
/// and "Restore Defaults" returns a preset to <see cref="MosaicLayout.Preset"/>.
/// </summary>
/// <remarks>
/// Windows has no Safari preset (macOS only): a layout from macOS that starts from Safari is
/// read as <see cref="Edge"/> (see <see cref="MosaicLayout.FromSafari"/>). Edge is Windows only;
/// macOS reads an Edge layout as Chrome.
/// </remarks>
public enum MosaicPresetID { ZenArc, Chrome, Opera, Edge }

/// <summary>Every element the shell can place (JSON: the Swift raw values).</summary>
public enum ShellComponent
{
    Back, Forward, Reload, Home,
    AddressField,
    /// <summary>The horizontal tab strip; with vertical tabs the sidebar shows the tabs instead.</summary>
    TabStrip,
    SidebarToggle, NewTab,
    /// <summary>Mount point owned by the downloads area.</summary>
    Downloads,
    /// <summary>The content blocker's shield.</summary>
    Blocker,
    Extensions, Settings,
    /// <summary>Bookmark star. Directly after <see cref="AddressField"/> in the same region it sits inside the field.</summary>
    BookmarkStar,
    /// <summary>Switches the selected tab between Mozilla and Chromium.</summary>
    EngineSwitch,
    SearchEngine,
    Workspaces,
    /// <summary>The browser menu (everything in one place, like Chrome's ⋮ or Edge's …).</summary>
    Menu,
    /// <summary>Expanding gap; the only component that may appear more than once in a region.</summary>
    FlexibleSpace,
    BookmarksPage, HistoryPage, ReadingListPage, DownloadsPage,
    /// <summary>
    /// Windows addition: Edge's tab actions button, which switches between horizontal and
    /// vertical tabs. macOS drops it when reading a layout.
    /// </summary>
    VerticalTabs,
}

/// <summary>The places components can go. Regions that end up empty are not drawn.</summary>
public enum ShellRegion
{
    /// <summary>The top row of the window, beside the caption buttons (Chrome/Edge/Opera tabs).</summary>
    TitleBar,
    /// <summary>The navigation row under the title bar (the omnibox row).</summary>
    Toolbar,
    /// <summary>A row of its own for the tab strip under the toolbar.</summary>
    TabBar,
    /// <summary>Sidebar rows: top, middle (Arc's address field), bottom.</summary>
    SidebarTop, SidebarMiddle, SidebarBottom,
    /// <summary>Opera's narrow quick-access column at the window's leading edge.</summary>
    Rail,
    /// <summary>
    /// Windows addition: a narrow column at the trailing edge (Edge's sidebar). macOS drops it
    /// when reading a layout.
    /// </summary>
    TrailingRail,
}

public enum TabPlacement { Top, SidebarLeading, SidebarTrailing }

/// <summary>The look of horizontal tabs.</summary>
public enum TabStyle
{
    /// <summary>Shaped tabs that merge into the toolbar below.</summary>
    Chrome,
    /// <summary>Separated rounded tabs (Opera, and Edge's Windows 11 tabs).</summary>
    Opera,
    /// <summary>Equal-width segments filling the bar.</summary>
    Safari,
}

public enum AddressBarStyle
{
    /// <summary>A wide field filling its row.</summary>
    Omnibox,
    /// <summary>A small field sized for a sidebar.</summary>
    Compact,
    /// <summary>A centred field with reload inside it.</summary>
    Unified,
}

public enum SidebarBehavior
{
    /// <summary>Always visible.</summary>
    Shown,
    /// <summary>Visible, and can be hidden with the sidebar button or Ctrl+Shift+L.</summary>
    Collapsible,
    /// <summary>Hidden until the pointer reaches the window's edge.</summary>
    AutoHide,
}

public enum ShellDensity { Compact, Regular, Comfortable }

public enum ColorSchemePreference { System, Light, Dark }

/// <summary>What the chrome is made of.</summary>
public enum ChromeMaterial
{
    /// <summary>
    /// Translucent chrome. JSON <c>liquidGlass</c> (the macOS name, kept so files move between
    /// platforms): Liquid Glass on macOS, Mica on the window and Acrylic on transient surfaces
    /// on Windows 11; solid where those are unavailable or transparency is turned off.
    /// </summary>
    LiquidGlass,
    /// <summary>Opaque surfaces.</summary>
    Solid,
}

public enum GradientKind { Linear, Radial, Angular, Mesh }

public enum BackgroundKind
{
    /// <summary>The system window colour (follows light/dark).</summary>
    System,
    Solid,
    Gradient,
}

public enum NewTabBackgroundMode
{
    /// <summary>The page colour with a whisper of the accent (the default).</summary>
    Plain,
    /// <summary>The same background as the window.</summary>
    Window,
    /// <summary>Its own background.</summary>
    Custom,
}

/// <summary>Maps the enums above to and from their JSON (Swift raw) values.</summary>
public static class JsonEnum
{
    private static readonly ConcurrentDictionary<Type, (Dictionary<string, object> Parse, Dictionary<object, string> Raw)> tables = new();

    private static (Dictionary<string, object> Parse, Dictionary<object, string> Raw) Table(Type type) =>
        tables.GetOrAdd(type, t =>
        {
            var parse = new Dictionary<string, object>(StringComparer.Ordinal);
            var raw = new Dictionary<object, string>();
            foreach (var value in Enum.GetValues(t))
            {
                var name = value.ToString()!;
                var json = char.ToLowerInvariant(name[0]) + name[1..];
                parse[json] = value;
                raw[value] = json;
            }
            return (parse, raw);
        });

    /// <summary>The JSON value of <paramref name="value"/> (e.g. <c>ZenArc</c> → <c>"zenArc"</c>).</summary>
    public static string Raw<T>(this T value) where T : struct, Enum => Table(typeof(T)).Raw[value];

    /// <summary>The member whose JSON value is exactly <paramref name="raw"/>, or null.</summary>
    public static T? Parse<T>(string? raw) where T : struct, Enum =>
        raw is not null && Table(typeof(T)).Parse.TryGetValue(raw, out var value) ? (T)value : null;
}

public static class ShellEnumInfo
{
    // MARK: Presets

    public static string Title(this MosaicPresetID preset) => preset switch
    {
        MosaicPresetID.ZenArc => "Zen / Arc",
        MosaicPresetID.Chrome => "Chrome",
        MosaicPresetID.Opera => "Opera",
        _ => "Microsoft Edge",
    };

    /// <summary>One line for the preset card.</summary>
    public static string Summary(this MosaicPresetID preset) => preset switch
    {
        MosaicPresetID.ZenArc => "Vertical tabs in a sidebar, pinned favourites and workspaces; the page floats in a rounded card.",
        MosaicPresetID.Chrome => "Tabs across the top, a full-width address bar with the usual buttons, and a bookmarks bar.",
        MosaicPresetID.Opera => "Rounded tabs, a slim quick-access rail on the left and Speed Dial. Optional GX styling.",
        _ => "Rounded tabs in the title bar or a vertical tab pane, a rounded address bar and Edge's sidebar on the right.",
    };

    // MARK: Components

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
        ShellComponent.DownloadsPage => "Downloads Page",
        ShellComponent.VerticalTabs => "Vertical Tabs",
        _ => component.ToString(),
    };

    /// <summary>Icon name (the SF Symbol names macOS uses; the app's <c>Icons</c> maps them to Fluent glyphs).</summary>
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
        ShellComponent.DownloadsPage => "arrow.down.doc",
        ShellComponent.VerticalTabs => "tabs.vertical",
        _ => "questionmark",
    };

    /// <summary>Components that take up the remaining width of a row.</summary>
    public static bool IsFlexible(this ShellComponent c) =>
        c is ShellComponent.AddressField or ShellComponent.TabStrip or ShellComponent.FlexibleSpace;

    /// <summary>Components that need a wide horizontal row (not a narrow rail).</summary>
    public static bool NeedsWideRow(this ShellComponent c) => c is ShellComponent.AddressField or ShellComponent.TabStrip;

    /// <summary>
    /// The id used in <c>FeatureMounts.Components</c> for this component: its JSON value
    /// ("downloads", "blocker", "extensions", …).
    /// </summary>
    public static string MountId(this ShellComponent c) => c.Raw();

    // MARK: Regions

    public static string Title(this ShellRegion region) => region switch
    {
        ShellRegion.TitleBar => "Title Bar",
        ShellRegion.Toolbar => "Toolbar",
        ShellRegion.TabBar => "Tab Bar",
        ShellRegion.SidebarTop => "Sidebar Top",
        ShellRegion.SidebarMiddle => "Sidebar Middle",
        ShellRegion.SidebarBottom => "Sidebar Bottom",
        ShellRegion.Rail => "Quick-Access Rail",
        _ => "Right Sidebar",
    };

    public static bool IsSidebar(this ShellRegion r) =>
        r is ShellRegion.SidebarTop or ShellRegion.SidebarMiddle or ShellRegion.SidebarBottom;

    public static bool IsRail(this ShellRegion r) => r is ShellRegion.Rail or ShellRegion.TrailingRail;

    /// <summary>Whether <paramref name="component"/> may be placed in <paramref name="region"/>.</summary>
    public static bool Accepts(this ShellRegion region, ShellComponent component) => region switch
    {
        ShellRegion.Rail or ShellRegion.TrailingRail => !component.NeedsWideRow(),
        ShellRegion.SidebarTop or ShellRegion.SidebarMiddle or ShellRegion.SidebarBottom => component != ShellComponent.TabStrip,
        _ => true,
    };

    // MARK: Options

    public static bool IsVertical(this TabPlacement p) => p != TabPlacement.Top;

    public static string Title(this TabPlacement p) => p switch
    {
        TabPlacement.Top => "Top",
        TabPlacement.SidebarLeading => "Left Sidebar",
        _ => "Right Sidebar",
    };

    public static string Title(this TabStyle s) => s switch
    {
        TabStyle.Chrome => "Shaped",
        TabStyle.Opera => "Rounded",
        _ => "Segmented",
    };

    public static string Title(this AddressBarStyle s) => s switch
    {
        AddressBarStyle.Omnibox => "Wide",
        AddressBarStyle.Compact => "Compact",
        _ => "Centred",
    };

    public static string Title(this SidebarBehavior s) => s switch
    {
        SidebarBehavior.Shown => "Always Shown",
        SidebarBehavior.Collapsible => "Collapsible",
        _ => "Auto-Hide",
    };

    public static string Title(this ShellDensity d) => d.ToString();

    public static string Title(this ColorSchemePreference c) => c.ToString();

    public static string Title(this NewTabBackgroundMode m) => m switch
    {
        NewTabBackgroundMode.Plain => "Plain",
        NewTabBackgroundMode.Window => "Same as Window",
        _ => "Own Background",
    };
}
