import AppKit
import MosaicCore

/// Automation commands and `dump` fields for the shell (see `Automation`). Owner: **shell**.
///
///     mosaic <zenArc|chrome|opera|safari>       apply a preset
///     previewMosaic <preset|off>                preview without applying
///     restoreMosaic <preset>                    restore a preset's defaults
///     mosaicSet <key>=<value>                   glass=on|off, scheme=system|light|dark,
///                                               density=compact|regular|comfortable,
///                                               tabs=top|sidebarLeading|sidebarTrailing,
///                                               tabStyle=chrome|opera|safari, address=omnibox|compact|unified,
///                                               sidebar=shown|collapsible|autoHide, bookmarksBar=on|off,
///                                               gx=on|off, inset=<pt>, radius=<pt>, accent=<#hex|system>
///     importLayout <path.json> / exportLayout <path.json>
///     settings [category] / closeSettings
///     sidebar show|hide|toggle
///     shellSet <key>=<value>                    homepage=<url>, startup=startPage|homepage,
///                                               newTab=startPage|homepage, homeButton=on|off,
///                                               reduceMotion=system|on|off, reduceTransparency=…,
///                                               textSize=standard|large|larger
///     home
@MainActor
enum ShellAutomation {
    /// Returns true when `action` was a shell command.
    static func handle(_ action: String, value: String, model: BrowserModel) -> Bool {
        switch action {
        case "mosaic":
            if let preset = MosaicPresetID(rawValue: value) { model.applyMosaic(preset) }
        case "previewMosaic":
            if let preset = MosaicPresetID(rawValue: value) { model.mosaics.preview(preset) } else { model.mosaics.endPreview() }
        case "restoreMosaic":
            if let preset = MosaicPresetID(rawValue: value) { model.mosaics.restoreDefaults(preset) }
        case "mosaicSet":
            setLayoutValue(value, model: model)
        case "importLayout":
            if let data = try? Data(contentsOf: URL(fileURLWithPath: value)),
               let layout = try? MosaicLayout.from(json: data) {
                model.mosaics.replaceActiveLayout(layout)
            }
        case "exportLayout":
            try? model.mosaics.layout.jsonData().write(to: URL(fileURLWithPath: value), options: .atomic)
        case "settings":
            model.showSettings(SettingsCategory(rawValue: value))
        case "closeSettings":
            model.dismissSettings()
        case "sidebar":
            let visible = model.isSidebarVisible
            if value == "toggle" || (value == "show" && !visible) || (value == "hide" && visible) { model.toggleSidebar() }
        case "shellSet":
            setShellValue(value, model: model)
        case "home":
            model.goHome()
        default:
            return false
        }
        return true
    }

    private static func split(_ value: String) -> (String, String)? {
        guard let index = value.firstIndex(of: "=") else { return nil }
        return (String(value[..<index]), String(value[value.index(after: index)...]))
    }

    private static func setLayoutValue(_ assignment: String, model: BrowserModel) {
        guard let (key, value) = split(assignment) else { return }
        let on = value == "on" || value == "true"
        let store = model.mosaics
        switch key {
        case "tabs":
            if let placement = TabPlacement(rawValue: value) {
                store.replaceActiveLayout(store.layout.withTabPlacement(placement))
            }
        case "gx":
            store.update { layout in
                if on {
                    layout = layout.withGXStyling()
                } else {
                    let defaults = MosaicLayout.preset(layout.preset).appearance
                    layout.appearance.colorScheme = defaults.colorScheme
                    layout.appearance.accent = defaults.accent
                    layout.appearance.glow = defaults.glow
                    layout.appearance.background = defaults.background
                }
            }
        default:
            store.update { layout in
                switch key {
                case "glass": layout.appearance.material = on ? .liquidGlass : .solid
                case "scheme": layout.appearance.colorScheme = ColorSchemePreference(rawValue: value) ?? layout.appearance.colorScheme
                case "density": layout.appearance.density = ShellDensity(rawValue: value) ?? layout.appearance.density
                case "tabStyle": layout.tabStyle = TabStyle(rawValue: value) ?? layout.tabStyle
                case "address": layout.addressBar = AddressBarStyle(rawValue: value) ?? layout.addressBar
                case "sidebar": layout.sidebar = SidebarBehavior(rawValue: value) ?? layout.sidebar
                case "bookmarksBar": layout.bookmarksBar = on
                case "inset": layout.appearance.contentInset = Double(value) ?? layout.appearance.contentInset
                case "radius": layout.appearance.cornerRadius = Double(value) ?? layout.appearance.cornerRadius
                case "accent": layout.appearance.accent = value
                default: break
                }
            }
        }
    }

    private static func setShellValue(_ assignment: String, model: BrowserModel) {
        guard let (key, value) = split(assignment) else { return }
        let settings = model.shellSettings
        let on = value == "on" || value == "true"
        switch key {
        case "homepage": settings.homepage = value
        case "startup": settings.startupPage = ShellSettings.StartupPage(rawValue: value) ?? settings.startupPage
        case "newTab": settings.newTabPage = ShellSettings.NewTabPage(rawValue: value) ?? settings.newTabPage
        case "homeButton": settings.showsHomeButton = on
        case "reduceMotion": settings.reduceMotion = ShellSettings.Override(rawValue: value) ?? settings.reduceMotion
        case "reduceTransparency":
            settings.reduceTransparency = ShellSettings.Override(rawValue: value) ?? settings.reduceTransparency
        case "textSize": settings.textSize = ShellSettings.TextSize(rawValue: value) ?? settings.textSize
        case "focusRings": settings.prominentFocusRings = on
        default: break
        }
    }

    private static func tree(_ view: NSView, depth: Int = 0) -> [String] {
        let r = view.convert(view.bounds, to: nil)
        var lines = [String(repeating: "  ", count: depth) + "\(type(of: view)) \(Int(r.minX)),\(Int(r.minY)) \(Int(r.width))x\(Int(r.height)) layer=\(view.layer.map { "\(Int($0.bounds.width))x\(Int($0.bounds.height))" } ?? "-")"]
        guard depth < 6 else { return lines }
        for sub in view.subviews { lines += tree(sub, depth: depth + 1) }
        return lines
    }

    /// Extra `dump` fields.
    static func dumpFields(model: BrowserModel) -> [String: Any] {
        var fields: [String: Any] = [
            "mosaic": model.mosaics.activePreset.rawValue,
            "previewMosaic": model.mosaics.previewedPreset?.rawValue ?? "",
            "settingsCategory": model.settingsCategory?.rawValue ?? "",
            "sidebarVisible": model.isSidebarVisible,
            "showsGeckoPage": model.showsGeckoPage,
            "windowOcclusionVisible": NSApp.windows.contains { $0.isVisible && $0.occlusionState.contains(.visible) },
        ]
        if let data = try? model.mosaics.effectiveLayout.jsonData(pretty: false),
           let object = try? JSONSerialization.jsonObject(with: data) {
            fields["layout"] = object
        }
        if let view = model.selectedTab?.chromiumView, let window = view.window {
            let inWindow = view.convert(view.bounds, to: nil)
            let container = view.superview.map { $0.convert($0.bounds, to: nil) } ?? .zero
            let content = view.subviews.first.map { $0.convert($0.bounds, to: nil) } ?? .zero
            func box(_ r: CGRect) -> [String: CGFloat] { ["x": r.minX, "y": r.minY, "width": r.width, "height": r.height] }
            fields["chromiumView"] = ["frameInWindow": box(inWindow), "containerInWindow": box(container),
                                      "browserContentInWindow": box(content),
                                      "screen": box(window.convertToScreen(inWindow)),
                                      "tree": tree(view)]
        }
        if let rect = ShellDiagnostics.contentRect {
            fields["contentRect"] = ["x": rect.minX, "y": rect.minY, "width": rect.width, "height": rect.height]
            // The same rectangle in Gecko's coordinates (top-left of the primary display), to
            // compare with `geckoEval rect`.
            if let primary = NSScreen.screens.first {
                fields["contentRectTopLeft"] = [
                    "x": Int(rect.minX.rounded()), "y": Int((primary.frame.maxY - rect.maxY).rounded()),
                    "width": Int(rect.width.rounded()), "height": Int(rect.height.rounded()),
                ]
            }
        }
        return fields
    }
}
