import Foundation

// The "Mosaic": a Codable description of the whole window's chrome. The shell renders a
// `MosaicLayout`; presets are just starting values of it; the layout editor (canvas) edits
// it by moving `ShellComponent`s between `ShellRegion`s with the mutation API below.
//
// Owner: **shell**. Everything here is engine- and UI-independent and round-trips through
// JSON. Decoding is forgiving: unknown components, regions and enum values written by a
// newer version are dropped or replaced by the preset's value instead of failing, so an
// imported or older file always produces a usable layout.

// MARK: - Presets

/// The four built-in starting points. Each is fully editable; the user's edits are kept per
/// preset, and "Restore Defaults" returns a preset to `MosaicLayout.preset(_:)`.
public enum MosaicPresetID: String, Codable, CaseIterable, Identifiable, Sendable {
    case zenArc, chrome, opera, safari

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .zenArc: "Zen / Arc"
        case .chrome: "Chrome"
        case .opera: "Opera"
        case .safari: "Safari"
        }
    }

    /// One line for the preset card.
    public var summary: String {
        switch self {
        case .zenArc: "Vertical tabs in a sidebar, pinned favourites and workspaces; the page floats in a rounded card."
        case .chrome: "Tabs across the top, a full-width address bar with the usual buttons, and a bookmarks bar."
        case .opera: "Rounded tabs, a slim quick-access rail on the left and Speed Dial. Optional GX styling."
        case .safari: "One unified toolbar with a centred address field, a compact tab bar and a hidden sidebar."
        }
    }
}

// MARK: - Components and regions

/// Every element the shell can place. The raw values are stable identifiers (they are what
/// the JSON stores); add new cases at will, never rename existing ones.
public enum ShellComponent: String, Codable, CaseIterable, Identifiable, Sendable {
    case back, forward, reload, home
    case addressField
    /// The horizontal tab strip. With vertical tabs the sidebar shows the tabs instead and
    /// this component is not drawn.
    case tabStrip
    case sidebarToggle, newTab
    /// Mount point owned by the downloads area.
    case downloads
    /// The content blocker's shield.
    case blocker
    case extensions, settings
    /// Bookmark star. Directly after `addressField` in the same region it sits inside the field.
    case bookmarkStar
    /// Switches the selected tab between Mozilla and Chromium.
    case engineSwitch
    case searchEngine
    case workspaces
    /// The browser menu (everything in one place, like Chrome's ⋮).
    case menu
    /// Expanding gap; the only component that may appear more than once in a region.
    case flexibleSpace
    // Mosaic's own pages (useful in Opera's rail).
    case bookmarksPage, historyPage, readingListPage, downloadsPage

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .back: "Back"
        case .forward: "Forward"
        case .reload: "Reload"
        case .home: "Home"
        case .addressField: "Address Field"
        case .tabStrip: "Tab Strip"
        case .sidebarToggle: "Sidebar"
        case .newTab: "New Tab"
        case .downloads: "Downloads"
        case .blocker: "Content Blocker"
        case .extensions: "Extensions"
        case .settings: "Settings"
        case .bookmarkStar: "Bookmark"
        case .engineSwitch: "Engine"
        case .searchEngine: "Search Engine"
        case .workspaces: "Workspaces"
        case .menu: "Menu"
        case .flexibleSpace: "Flexible Space"
        case .bookmarksPage: "Bookmarks"
        case .historyPage: "History"
        case .readingListPage: "Reading List"
        case .downloadsPage: "Downloads Page"
        }
    }

    /// SF Symbol used by the layout editor's palette (and by most toolbar buttons).
    public var symbol: String {
        switch self {
        case .back: "chevron.left"
        case .forward: "chevron.right"
        case .reload: "arrow.clockwise"
        case .home: "house"
        case .addressField: "magnifyingglass"
        case .tabStrip: "rectangle.topthird.inset.filled"
        case .sidebarToggle: "sidebar.left"
        case .newTab: "plus"
        case .downloads: "arrow.down.circle"
        case .blocker: "shield.lefthalf.filled"
        case .extensions: "puzzlepiece.extension"
        case .settings: "gearshape"
        case .bookmarkStar: "star"
        case .engineSwitch: "circle.lefthalf.filled"
        case .searchEngine: "text.magnifyingglass"
        case .workspaces: "square.stack"
        case .menu: "ellipsis"
        case .flexibleSpace: "arrow.left.and.right"
        case .bookmarksPage: "book"
        case .historyPage: "clock"
        case .readingListPage: "eyeglasses"
        case .downloadsPage: "arrow.down.doc"
        }
    }

    /// Components that take up the remaining width of a row.
    public var isFlexible: Bool { self == .addressField || self == .tabStrip || self == .flexibleSpace }

    /// Components that need a wide horizontal row (not the narrow rail).
    public var needsWideRow: Bool { self == .addressField || self == .tabStrip }
}

/// The places components can go. Each region is an ordered list drawn as a row (the rail
/// as a column). Regions that end up empty are not drawn at all.
public enum ShellRegion: String, Codable, CaseIterable, Identifiable, CodingKeyRepresentable, Sendable {
    /// The top row of the window, beside the traffic lights (Chrome/Opera tabs, Safari's unified bar).
    case titleBar
    /// The navigation row under the title bar (Chrome's omnibox row).
    case toolbar
    /// A row of its own for the tab strip under the toolbar (Safari's separate tab bar).
    case tabBar
    /// Sidebar rows: top (beside the traffic lights), middle (Arc's address field), bottom.
    case sidebarTop, sidebarMiddle, sidebarBottom
    /// Opera's narrow quick-access column at the window's leading edge.
    case rail

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .titleBar: "Title Bar"
        case .toolbar: "Toolbar"
        case .tabBar: "Tab Bar"
        case .sidebarTop: "Sidebar Top"
        case .sidebarMiddle: "Sidebar Middle"
        case .sidebarBottom: "Sidebar Bottom"
        case .rail: "Quick-Access Rail"
        }
    }

    public var isSidebar: Bool { self == .sidebarTop || self == .sidebarMiddle || self == .sidebarBottom }

    /// Whether `component` may be placed here.
    public func accepts(_ component: ShellComponent) -> Bool {
        switch self {
        case .rail: return !component.needsWideRow
        case .sidebarTop, .sidebarMiddle, .sidebarBottom: return component != .tabStrip
        case .titleBar, .toolbar, .tabBar: return true
        }
    }
}

// MARK: - Layout options

public enum TabPlacement: String, Codable, CaseIterable, Sendable {
    /// Horizontal tabs, drawn where the `tabStrip` component is.
    case top
    /// Vertical tabs in a sidebar on the left or right.
    case sidebarLeading, sidebarTrailing

    public var isVertical: Bool { self != .top }

    public var title: String {
        switch self {
        case .top: "Top"
        case .sidebarLeading: "Left Sidebar"
        case .sidebarTrailing: "Right Sidebar"
        }
    }
}

/// The look of horizontal tabs.
public enum TabStyle: String, Codable, CaseIterable, Sendable {
    /// Shaped tabs that merge into the toolbar below.
    case chrome
    /// Separated rounded tabs.
    case opera
    /// Equal-width segments filling the bar.
    case safari

    public var title: String {
        switch self {
        case .chrome: "Shaped"
        case .opera: "Rounded"
        case .safari: "Segmented"
        }
    }
}

public enum AddressBarStyle: String, Codable, CaseIterable, Sendable {
    /// A wide field filling its row.
    case omnibox
    /// A small field sized for a sidebar.
    case compact
    /// A centred field with reload inside it (Safari).
    case unified

    public var title: String {
        switch self {
        case .omnibox: "Wide"
        case .compact: "Compact"
        case .unified: "Centred"
        }
    }
}

public enum SidebarBehavior: String, Codable, CaseIterable, Sendable {
    /// Always visible.
    case shown
    /// Visible, and can be hidden with the sidebar button or ⌘⇧L.
    case collapsible
    /// Hidden until the pointer reaches the window's edge.
    case autoHide

    public var title: String {
        switch self {
        case .shown: "Always Shown"
        case .collapsible: "Collapsible"
        case .autoHide: "Auto-Hide"
        }
    }
}

public enum ShellDensity: String, Codable, CaseIterable, Sendable {
    case compact, regular, comfortable

    public var title: String { rawValue.capitalized }
}

public enum ColorSchemePreference: String, Codable, CaseIterable, Sendable {
    case system, light, dark

    public var title: String { rawValue.capitalized }
}

public enum ChromeMaterial: String, Codable, CaseIterable, Sendable {
    /// Liquid Glass on toolbars and controls (macOS 26 and later; solid elsewhere).
    case liquidGlass
    /// Opaque surfaces.
    case solid
}

// MARK: - Colours and backgrounds

/// Colours are stored as `#RRGGBB` / `#RRGGBBAA` strings, or `"system"` for the system accent.
public enum HexColor {
    public static let system = "system"

    /// Red, green, blue and alpha in 0…1, or nil if `hex` is not a colour.
    public static func components(_ hex: String) -> (red: Double, green: Double, blue: Double, alpha: Double)? {
        var text = hex.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("#") { text.removeFirst() }
        guard text.count == 6 || text.count == 8, let value = UInt64(text, radix: 16) else { return nil }
        let hasAlpha = text.count == 8
        let r = Double((value >> (hasAlpha ? 24 : 16)) & 0xFF) / 255
        let g = Double((value >> (hasAlpha ? 16 : 8)) & 0xFF) / 255
        let b = Double((value >> (hasAlpha ? 8 : 0)) & 0xFF) / 255
        let a = hasAlpha ? Double(value & 0xFF) / 255 : 1
        return (r, g, b, a)
    }

    public static func isValid(_ hex: String) -> Bool { hex == system || components(hex) != nil }

    /// `#RRGGBB` (or `#RRGGBBAA` when not opaque) for components in 0…1.
    public static func string(red: Double, green: Double, blue: Double, alpha: Double = 1) -> String {
        func byte(_ v: Double) -> Int { Int((min(max(v, 0), 1) * 255).rounded()) }
        let base = String(format: "#%02X%02X%02X", byte(red), byte(green), byte(blue))
        return alpha >= 0.999 ? base : base + String(format: "%02X", byte(alpha))
    }
}

public struct GradientStop: Codable, Hashable, Sendable {
    public var color: String
    /// 0…1 along the gradient.
    public var location: Double

    public init(color: String, location: Double) {
        self.color = color
        self.location = location
    }
}

/// A minimal gradient description; the Gradient Studio extends it (add fields with defaults
/// so older files still decode).
public struct GradientSpec: Codable, Hashable, Sendable {
    public enum Kind: String, Codable, CaseIterable, Sendable { case linear, radial, angular }

    public var kind: Kind
    /// Degrees; 0 runs left to right, 90 top to bottom.
    public var angle: Double
    public var stops: [GradientStop]

    public init(kind: Kind = .linear, angle: Double = 135, stops: [GradientStop]) {
        self.kind = kind
        self.angle = angle
        self.stops = stops
    }

    /// Stops sorted by location, clamped to 0…1, invalid colours removed; at least two.
    public var normalizedStops: [GradientStop] {
        var result = stops.filter { HexColor.components($0.color) != nil }
            .map { GradientStop(color: $0.color, location: min(max($0.location, 0), 1)) }
            .sorted { $0.location < $1.location }
        if result.isEmpty { result = [GradientStop(color: "#808080", location: 0)] }
        if result.count == 1 { result.append(GradientStop(color: result[0].color, location: 1)) }
        return result
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        kind = (try? c.decodeIfPresent(Kind.self, forKey: .kind)) ?? .linear
        angle = (try? c.decodeIfPresent(Double.self, forKey: .angle)) ?? 135
        stops = (try? c.decodeIfPresent([GradientStop].self, forKey: .stops)) ?? []
    }
}

public struct BackgroundSpec: Codable, Hashable, Sendable {
    public enum Kind: String, Codable, CaseIterable, Sendable {
        /// The system window colour (follows light/dark).
        case system
        case solid
        case gradient
    }

    public var kind: Kind
    public var color: String
    public var gradient: GradientSpec

    public init(kind: Kind = .system, color: String = "#F2F2F7", gradient: GradientSpec = .init(stops: [])) {
        self.kind = kind
        self.color = color
        self.gradient = gradient
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        kind = (try? c.decodeIfPresent(Kind.self, forKey: .kind)) ?? .system
        color = (try? c.decodeIfPresent(String.self, forKey: .color)) ?? "#F2F2F7"
        gradient = (try? c.decodeIfPresent(GradientSpec.self, forKey: .gradient)) ?? GradientSpec(stops: [])
    }
}

public struct MosaicAppearance: Codable, Hashable, Sendable {
    public var density: ShellDensity
    /// Corner radius of controls (address field, tabs, buttons), in points.
    public var cornerRadius: Double
    /// Gap between the page and the window chrome (Arc's floating card); 0 = flush.
    public var contentInset: Double
    /// Corner radius of the page card.
    public var contentCornerRadius: Double
    public var accent: String
    public var colorScheme: ColorSchemePreference
    public var material: ChromeMaterial
    public var background: BackgroundSpec
    /// The toolbar row has its own lighter surface that the selected tab merges into (Chrome).
    public var toolbarSurface: Bool
    /// A soft accent-coloured glow around the page (Opera GX).
    public var glow: Bool

    public init(
        density: ShellDensity = .regular, cornerRadius: Double = 8, contentInset: Double = 0,
        contentCornerRadius: Double = 0, accent: String = HexColor.system,
        colorScheme: ColorSchemePreference = .system, material: ChromeMaterial = .liquidGlass,
        background: BackgroundSpec = .init(), toolbarSurface: Bool = false, glow: Bool = false
    ) {
        self.density = density
        self.cornerRadius = cornerRadius
        self.contentInset = contentInset
        self.contentCornerRadius = contentCornerRadius
        self.accent = accent
        self.colorScheme = colorScheme
        self.material = material
        self.background = background
        self.toolbarSurface = toolbarSurface
        self.glow = glow
    }

    public init(from decoder: Decoder) throws {
        let d = MosaicAppearance()
        let c = try decoder.container(keyedBy: CodingKeys.self)
        func value<T: Decodable>(_ key: CodingKeys, _ fallback: T) -> T {
            (try? c.decodeIfPresent(T.self, forKey: key)) ?? fallback
        }
        density = value(.density, d.density)
        cornerRadius = value(.cornerRadius, d.cornerRadius)
        contentInset = value(.contentInset, d.contentInset)
        contentCornerRadius = value(.contentCornerRadius, d.contentCornerRadius)
        accent = value(.accent, d.accent)
        colorScheme = value(.colorScheme, d.colorScheme)
        material = value(.material, d.material)
        background = value(.background, d.background)
        toolbarSurface = value(.toolbarSurface, d.toolbarSurface)
        glow = value(.glow, d.glow)
    }
}

// MARK: - The layout

public struct MosaicLayout: Codable, Hashable, Sendable {
    public static let currentSchemaVersion = 1

    public var schemaVersion: Int
    /// The preset this layout started from (drives a few preset-specific touches).
    public var preset: MosaicPresetID
    public var name: String
    public var tabPlacement: TabPlacement
    public var tabStyle: TabStyle
    public var addressBar: AddressBarStyle
    /// Components per region, in order. Regions missing from the dictionary are empty.
    public var regions: [ShellRegion: [ShellComponent]]
    public var bookmarksBar: Bool
    public var sidebar: SidebarBehavior
    public var sidebarWidth: Double
    public var appearance: MosaicAppearance

    public init(
        preset: MosaicPresetID, name: String, tabPlacement: TabPlacement, tabStyle: TabStyle,
        addressBar: AddressBarStyle, regions: [ShellRegion: [ShellComponent]], bookmarksBar: Bool,
        sidebar: SidebarBehavior, sidebarWidth: Double = 240, appearance: MosaicAppearance
    ) {
        schemaVersion = Self.currentSchemaVersion
        self.preset = preset
        self.name = name
        self.tabPlacement = tabPlacement
        self.tabStyle = tabStyle
        self.addressBar = addressBar
        self.regions = regions
        self.bookmarksBar = bookmarksBar
        self.sidebar = sidebar
        self.sidebarWidth = sidebarWidth
        self.appearance = appearance
    }

    // MARK: Reading

    public func components(in region: ShellRegion) -> [ShellComponent] { regions[region] ?? [] }

    public func contains(_ component: ShellComponent) -> Bool {
        regions.values.contains { $0.contains(component) }
    }

    /// The region holding `component` (the first one, for flexible space).
    public func region(of component: ShellComponent) -> ShellRegion? {
        ShellRegion.allCases.first { components(in: $0).contains(component) }
    }

    /// Whether the window has a sidebar at all (vertical tabs, or a sidebar button to open one).
    public var hasSidebar: Bool { tabPlacement.isVertical || contains(.sidebarToggle) }

    // MARK: Mutation (used by the settings page and the layout editor)

    /// Inserts `component` into `region` at `index` (clamped). If it is already elsewhere it
    /// is moved, since every component except flexible space appears once. Returns false
    /// (and changes nothing) when the region does not accept it.
    @discardableResult
    public mutating func insert(_ component: ShellComponent, in region: ShellRegion, at index: Int? = nil) -> Bool {
        guard region.accepts(component) else { return false }
        var target = index ?? components(in: region).count
        if component != .flexibleSpace, let from = self.region(of: component),
           let fromIndex = components(in: from).firstIndex(of: component) {
            regions[from]?.remove(at: fromIndex)
            if from == region, fromIndex < target { target -= 1 }
        }
        var list = components(in: region)
        list.insert(component, at: min(max(target, 0), list.count))
        regions[region] = list
        return true
    }

    /// Removes the component at `index` of `region`; returns it, or nil if out of range.
    @discardableResult
    public mutating func remove(at index: Int, from region: ShellRegion) -> ShellComponent? {
        guard var list = regions[region], list.indices.contains(index) else { return nil }
        let removed = list.remove(at: index)
        regions[region] = list
        return removed
    }

    /// Removes every occurrence of `component`.
    public mutating func remove(_ component: ShellComponent) {
        for region in ShellRegion.allCases { regions[region]?.removeAll { $0 == component } }
    }

    /// Moves the component at `index` of `from` to `toIndex` of `to` (index in the list as it
    /// is after removal). Returns false and changes nothing if either side is invalid.
    @discardableResult
    public mutating func move(from: ShellRegion, at index: Int, to: ShellRegion, at toIndex: Int) -> Bool {
        guard components(in: from).indices.contains(index) else { return false }
        let component = components(in: from)[index]
        guard to.accepts(component) else { return false }
        regions[from]?.remove(at: index)
        var list = components(in: to)
        list.insert(component, at: min(max(toIndex, 0), list.count))
        regions[to] = list
        return true
    }

    /// Changes where the tabs are, moving navigation so it stays reachable: going to the top,
    /// what the sidebar's top and middle rows held moves into the toolbar (a horizontal
    /// layout's sidebar is hidden by default); going to a sidebar, the toolbar's navigation
    /// and address field move into the sidebar when the layout has no toolbar of its own.
    public func withTabPlacement(_ placement: TabPlacement) -> MosaicLayout {
        var layout = self
        guard placement != tabPlacement else { return self }
        let wasVertical = tabPlacement.isVertical
        layout.tabPlacement = placement
        if wasVertical && !placement.isVertical {
            let moving = (components(in: .sidebarTop) + components(in: .sidebarMiddle))
                .filter { $0 != .flexibleSpace && $0 != .sidebarToggle }
            for component in moving { layout.insert(component, in: .toolbar) }
            if !layout.contains(.tabStrip) { layout.insert(.tabStrip, in: .titleBar, at: 0) }
            if layout.components(in: .titleBar) == [.tabStrip] { layout.insert(.newTab, in: .titleBar) }
        } else if !wasVertical && placement.isVertical {
            let toolbar = components(in: .toolbar)
            if layout.components(in: .sidebarTop).isEmpty && layout.components(in: .sidebarMiddle).isEmpty {
                let navigation: [ShellComponent] = [.back, .forward, .reload]
                layout.regions[.sidebarTop] = [.sidebarToggle, .flexibleSpace] + toolbar.filter(navigation.contains)
                if layout.region(of: .addressField) == .toolbar {
                    layout.insert(.addressField, in: .sidebarMiddle)
                    if layout.region(of: .bookmarkStar) == .toolbar { layout.insert(.bookmarkStar, in: .sidebarMiddle) }
                }
                layout.regions[.toolbar]?.removeAll(where: navigation.contains)
                // Whatever is left in the toolbar goes to the sidebar's bottom row.
                let rest = layout.components(in: .toolbar).filter { $0 != .flexibleSpace }
                if layout.components(in: .sidebarBottom).isEmpty, !rest.isEmpty {
                    layout.regions[.toolbar] = []
                    layout.regions[.sidebarBottom] = [.workspaces, .flexibleSpace]
                        + rest.filter { $0 != .workspaces && $0 != .newTab } + [.newTab]
                }
            }
            // The tab strip's title-bar row is not needed any more.
            if layout.components(in: .titleBar).allSatisfy({ [.tabStrip, .newTab, .flexibleSpace].contains($0) }) {
                if layout.components(in: .titleBar).contains(.newTab), layout.region(of: .newTab) == .titleBar {
                    layout.insert(.newTab, in: .sidebarBottom)
                }
                layout.regions[.titleBar] = []
            }
        }
        return layout.normalized()
    }

    /// Repairs anything that would leave the window unusable or is out of range: components
    /// in regions that cannot hold them, duplicates, a missing address field or tab strip,
    /// invalid colours and numbers. Always applied before a layout is shown or saved.
    public func normalized() -> MosaicLayout {
        var layout = self
        layout.schemaVersion = Self.currentSchemaVersion
        var seen: Set<ShellComponent> = []
        var cleaned: [ShellRegion: [ShellComponent]] = [:]
        for region in ShellRegion.allCases {
            var list: [ShellComponent] = []
            for component in components(in: region) where region.accepts(component) {
                if component != .flexibleSpace {
                    guard !seen.contains(component) else { continue }
                    seen.insert(component)
                }
                // Two gaps in a row do nothing a single one doesn't.
                if component == .flexibleSpace, list.last == .flexibleSpace { continue }
                list.append(component)
            }
            if !list.isEmpty { cleaned[region] = list }
        }
        layout.regions = cleaned

        // The address field and (for horizontal tabs) the tab strip must exist somewhere.
        if !layout.contains(.addressField) {
            layout.insert(.addressField, in: layout.tabPlacement.isVertical ? .sidebarMiddle : .toolbar)
        }
        if layout.tabPlacement == .top, layout.region(of: .addressField)?.isSidebar == true {
            // A horizontal layout's sidebar is hidden by default; the field must stay visible.
            layout.insert(.addressField, in: .toolbar)
        }
        if layout.tabPlacement == .top {
            let strip = layout.region(of: .tabStrip)
            if strip == nil || strip?.isSidebar == true || strip == .rail {
                layout.insert(.tabStrip, in: .titleBar, at: 0)
            }
        }
        // A sidebar that is hidden by default needs a way to open it.
        if layout.tabPlacement.isVertical, layout.sidebar == .collapsible, !layout.contains(.sidebarToggle) {
            layout.insert(.sidebarToggle, in: .sidebarTop, at: 0)
        }

        layout.sidebarWidth = min(max(layout.sidebarWidth.isFinite ? layout.sidebarWidth : 240, 180), 420)
        var a = layout.appearance
        func clamp(_ v: Double, _ lo: Double, _ hi: Double, _ fallback: Double) -> Double {
            v.isFinite ? min(max(v, lo), hi) : fallback
        }
        a.cornerRadius = clamp(a.cornerRadius, 0, 20, 8)
        a.contentInset = clamp(a.contentInset, 0, 24, 0)
        a.contentCornerRadius = clamp(a.contentCornerRadius, 0, 28, 0)
        if !HexColor.isValid(a.accent) { a.accent = HexColor.system }
        if HexColor.components(a.background.color) == nil { a.background.color = "#F2F2F7" }
        a.background.gradient.angle = a.background.gradient.angle.isFinite ? a.background.gradient.angle : 135
        layout.appearance = a
        return layout
    }

    // MARK: Coding

    private enum CodingKeys: String, CodingKey {
        case schemaVersion, preset, name, tabPlacement, tabStyle, addressBar, regions
        case bookmarksBar, sidebar, sidebarWidth, appearance
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let preset = (try? c.decodeIfPresent(MosaicPresetID.self, forKey: .preset)) ?? .chrome
        let d = MosaicLayout.preset(preset)
        func value<T: Decodable>(_ key: CodingKeys, _ fallback: T) -> T {
            (try? c.decodeIfPresent(T.self, forKey: key)) ?? fallback
        }
        schemaVersion = value(.schemaVersion, Self.currentSchemaVersion)
        self.preset = preset
        name = value(.name, d.name)
        tabPlacement = value(.tabPlacement, d.tabPlacement)
        tabStyle = value(.tabStyle, d.tabStyle)
        addressBar = value(.addressBar, d.addressBar)
        bookmarksBar = value(.bookmarksBar, d.bookmarksBar)
        sidebar = value(.sidebar, d.sidebar)
        sidebarWidth = value(.sidebarWidth, d.sidebarWidth)
        appearance = value(.appearance, d.appearance)
        // Regions are read as plain strings so that unknown names are skipped, not fatal.
        if let raw = try? c.decodeIfPresent([String: [String]].self, forKey: .regions) {
            var regions: [ShellRegion: [ShellComponent]] = [:]
            for (key, names) in raw {
                guard let region = ShellRegion(rawValue: key) else { continue }
                regions[region] = names.compactMap(ShellComponent.init(rawValue:))
            }
            self.regions = regions
        } else {
            regions = d.regions
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(schemaVersion, forKey: .schemaVersion)
        try c.encode(preset, forKey: .preset)
        try c.encode(name, forKey: .name)
        try c.encode(tabPlacement, forKey: .tabPlacement)
        try c.encode(tabStyle, forKey: .tabStyle)
        try c.encode(addressBar, forKey: .addressBar)
        // Plain string keys, in a fixed order, so files diff cleanly.
        var raw: [String: [String]] = [:]
        for (region, list) in regions where !list.isEmpty { raw[region.rawValue] = list.map(\.rawValue) }
        try c.encode(raw, forKey: .regions)
        try c.encode(bookmarksBar, forKey: .bookmarksBar)
        try c.encode(sidebar, forKey: .sidebar)
        try c.encode(sidebarWidth, forKey: .sidebarWidth)
        try c.encode(appearance, forKey: .appearance)
    }

    public func jsonData(pretty: Bool = true) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = pretty ? [.prettyPrinted, .sortedKeys] : [.sortedKeys]
        return try encoder.encode(self)
    }

    public static func from(json data: Data) throws -> MosaicLayout {
        try JSONDecoder().decode(MosaicLayout.self, from: data).normalized()
    }
}

// MARK: - Preset definitions

extension MosaicLayout {
    /// The factory layout of `preset`.
    public static func preset(_ preset: MosaicPresetID) -> MosaicLayout {
        switch preset {
        case .zenArc: zenArc
        case .chrome: chrome
        case .opera: opera
        case .safari: safari
        }
    }

    /// Arc / Zen: no toolbar at all. The sidebar holds navigation, a compact address field,
    /// pinned favourites, the tab list and workspaces; the page floats in a rounded card on
    /// a soft accent gradient.
    static let zenArc = MosaicLayout(
        preset: .zenArc, name: "Zen / Arc",
        tabPlacement: .sidebarLeading, tabStyle: .opera, addressBar: .compact,
        regions: [
            .sidebarTop: [.sidebarToggle, .flexibleSpace, .back, .forward, .reload],
            .sidebarMiddle: [.addressField, .bookmarkStar],
            .sidebarBottom: [.workspaces, .flexibleSpace, .blocker, .downloads, .menu, .newTab],
        ],
        bookmarksBar: false, sidebar: .collapsible, sidebarWidth: 248,
        appearance: MosaicAppearance(
            density: .regular, cornerRadius: 9, contentInset: 8, contentCornerRadius: 11,
            accent: "#7C6CF2", colorScheme: .system, material: .liquidGlass,
            background: BackgroundSpec(kind: .gradient, color: "#E9E4FB", gradient: GradientSpec(
                kind: .linear, angle: 125,
                stops: [GradientStop(color: "#C9BFF7", location: 0), GradientStop(color: "#E7D6F5", location: 0.55),
                        GradientStop(color: "#BFD3F5", location: 1)])),
            toolbarSurface: false, glow: false))

    /// Chrome: tabs in the title-bar row with the new-tab button right after them; the
    /// omnibox row with back/forward/reload/home, bookmark star in the field, extensions,
    /// downloads and the ⋮ menu at the end; bookmarks bar; the page flush with the frame.
    static let chrome = MosaicLayout(
        preset: .chrome, name: "Chrome",
        tabPlacement: .top, tabStyle: .chrome, addressBar: .omnibox,
        regions: [
            .titleBar: [.tabStrip, .newTab, .flexibleSpace],
            .toolbar: [.back, .forward, .reload, .home, .addressField, .bookmarkStar,
                       .engineSwitch, .blocker, .extensions, .downloads, .menu],
        ],
        bookmarksBar: true, sidebar: .collapsible, sidebarWidth: 260,
        appearance: MosaicAppearance(
            density: .regular, cornerRadius: 8, contentInset: 0, contentCornerRadius: 0,
            accent: "#1A73E8", colorScheme: .system, material: .solid,
            background: BackgroundSpec(kind: .system), toolbarSurface: true, glow: false))

    /// Opera: refined rounded tabs on top, an address row, and a slim rail of quick-access
    /// icons down the left; the page sits in a slightly inset card.
    static let opera = MosaicLayout(
        preset: .opera, name: "Opera",
        tabPlacement: .top, tabStyle: .opera, addressBar: .omnibox,
        regions: [
            .titleBar: [.tabStrip, .newTab, .flexibleSpace],
            .toolbar: [.back, .forward, .reload, .addressField, .bookmarkStar,
                       .engineSwitch, .blocker, .extensions, .downloads, .menu],
            .rail: [.home, .bookmarksPage, .historyPage, .readingListPage, .downloadsPage,
                    .flexibleSpace, .settings],
        ],
        bookmarksBar: false, sidebar: .collapsible, sidebarWidth: 260,
        appearance: MosaicAppearance(
            density: .regular, cornerRadius: 8, contentInset: 6, contentCornerRadius: 8,
            accent: "#FF1B2D", colorScheme: .system, material: .liquidGlass,
            background: BackgroundSpec(kind: .system), toolbarSurface: false, glow: false))

    /// Opera GX styling applied to any layout: dark, neon accent, a dark gradient and a glow
    /// around the page.
    public func withGXStyling() -> MosaicLayout {
        var layout = self
        layout.appearance.colorScheme = .dark
        layout.appearance.accent = "#FA1E4E"
        layout.appearance.glow = true
        layout.appearance.background = BackgroundSpec(kind: .gradient, color: "#121019", gradient: GradientSpec(
            kind: .linear, angle: 160,
            stops: [GradientStop(color: "#1B1024", location: 0), GradientStop(color: "#0F0E15", location: 0.6),
                    GradientStop(color: "#24101A", location: 1)]))
        return layout
    }

    /// True when the layout carries GX styling (as applied by `withGXStyling`).
    public var hasGXStyling: Bool { appearance.glow && appearance.colorScheme == .dark }

    /// Safari: one unified row with the sidebar button, back/forward, a centred address
    /// field (reload inside it) and the trailing buttons; the tabs in a compact bar of
    /// their own below; a sidebar that is hidden until asked for.
    static let safari = MosaicLayout(
        preset: .safari, name: "Safari",
        tabPlacement: .top, tabStyle: .safari, addressBar: .unified,
        regions: [
            .titleBar: [.sidebarToggle, .back, .forward, .flexibleSpace, .addressField, .flexibleSpace,
                        .blocker, .downloads, .newTab, .menu],
            .tabBar: [.tabStrip],
            .sidebarTop: [.flexibleSpace, .home],
            .sidebarBottom: [.workspaces, .flexibleSpace, .settings],
        ],
        bookmarksBar: false, sidebar: .collapsible, sidebarWidth: 240,
        appearance: MosaicAppearance(
            density: .regular, cornerRadius: 8, contentInset: 0, contentCornerRadius: 0,
            accent: HexColor.system, colorScheme: .system, material: .liquidGlass,
            background: BackgroundSpec(kind: .system), toolbarSurface: false, glow: false))
}
