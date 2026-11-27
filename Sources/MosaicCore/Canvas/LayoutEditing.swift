import Foundation

// The layout canvas's rules: which drops are allowed (and why not), the palette of
// components not placed, the edits the editor makes (drag, palette, keyboard), the guard
// that keeps every layout usable, and the undo history. UI-independent and unit-tested.
//
// Owner: **canvas**. Built only on `MosaicLayout`'s public API (shell).

/// A component at a position in the layout.
public struct CanvasSlot: Hashable, Codable, Sendable {
    public var region: ShellRegion
    public var index: Int

    public init(_ region: ShellRegion, _ index: Int) {
        self.region = region
        self.index = index
    }
}

/// Whether an edit may happen, with the reason shown to the user when not.
public enum CanvasVerdict: Equatable, Sendable {
    case allowed
    case rejected(String)

    public var isAllowed: Bool { self == .allowed }

    public var message: String? {
        if case .rejected(let message) = self { return message }
        return nil
    }
}

/// The result of an edit: the new (usable) layout, or why nothing changed.
public enum CanvasEdit: Equatable, Sendable {
    case changed(MosaicLayout, selection: CanvasSlot?)
    case rejected(String)

    public var layout: MosaicLayout? {
        if case .changed(let layout, _) = self { return layout }
        return nil
    }

    public var message: String? {
        if case .rejected(let message) = self { return message }
        return nil
    }
}

public enum LayoutCanvasRules {
    /// Regions in the order the keyboard steps through them (top to bottom, then the side
    /// columns).
    public static let regionOrder: [ShellRegion] = [
        .titleBar, .toolbar, .tabBar, .rail, .sidebarTop, .sidebarMiddle, .sidebarBottom,
    ]

    /// Regions that are on screen whenever the window is (a horizontal layout's sidebar is
    /// hidden until its button opens it).
    public static func alwaysVisibleRegions(_ layout: MosaicLayout) -> Set<ShellRegion> {
        var regions: Set<ShellRegion> = [.titleBar, .toolbar, .tabBar, .rail]
        if layout.tabPlacement.isVertical, layout.sidebar != .autoHide {
            regions.formUnion([.sidebarTop, .sidebarMiddle, .sidebarBottom])
        }
        return regions
    }

    /// Settings is reachable from the window: the Settings button or the browser menu
    /// (which contains Settings…) sits in a region that is always visible. (⌘, and the app
    /// menu work regardless; this keeps a pointer path too.)
    public static func hasSettingsAccess(_ layout: MosaicLayout) -> Bool {
        let visible = alwaysVisibleRegions(layout)
        return [ShellComponent.settings, .menu].contains { component in
            visible.contains { layout.components(in: $0).contains(component) }
        }
    }

    /// The layout made safe to show: `normalized()` (address field, tab strip, bounds), and a
    /// browser menu added when no way to Settings is left.
    public static func usable(_ layout: MosaicLayout) -> MosaicLayout {
        var result = layout.normalized()
        // A horizontal layout's sidebar opens with its button: the button can't live inside it.
        if !result.tabPlacement.isVertical, result.region(of: .sidebarToggle)?.isSidebar == true {
            let target: ShellRegion = result.components(in: .toolbar).isEmpty ? .titleBar : .toolbar
            result.insert(.sidebarToggle, in: target, at: 0)
            result = result.normalized()
        }
        if !hasSettingsAccess(result) {
            let visible = alwaysVisibleRegions(result)
            let candidates: [ShellRegion] = [.toolbar, .titleBar, .sidebarBottom, .sidebarTop, .tabBar, .rail]
            let target = candidates.first { visible.contains($0) && !result.components(in: $0).isEmpty }
                ?? (result.tabPlacement.isVertical ? .sidebarBottom : .toolbar)
            result.insert(.menu, in: target)
            result = result.normalized()
        }
        return result
    }

    /// Components that are not in the layout, which the palette offers. Flexible space is
    /// always offered (it may appear more than once); the tab strip only with tabs on top.
    public static func palette(_ layout: MosaicLayout) -> [ShellComponent] {
        var list: [ShellComponent] = [.flexibleSpace]
        for component in ShellComponent.allCases where component != .flexibleSpace && !layout.contains(component) {
            if component == .tabStrip && layout.tabPlacement.isVertical { continue }
            list.append(component)
        }
        return list
    }

    /// Whether `component` is drawn where it is (the tab strip is not with vertical tabs).
    public static func isDrawn(_ component: ShellComponent, in layout: MosaicLayout) -> Bool {
        !(component == .tabStrip && layout.tabPlacement.isVertical)
    }

    // MARK: Verdicts

    /// May `component` be placed in `region` (from elsewhere or from the palette)?
    public static func verdict(placing component: ShellComponent, in region: ShellRegion,
                               layout: MosaicLayout, from source: CanvasSlot? = nil) -> CanvasVerdict {
        if !region.accepts(component) {
            if region == .rail {
                return .rejected("The quick-access rail is too narrow for the \(component.title.lowercased()).")
            }
            if component == .tabStrip {
                return .rejected("The tab strip needs a horizontal row. For tabs in a sidebar, set Tabs to Left or Right Sidebar.")
            }
            return .rejected("\(region.title) can’t hold the \(component.title.lowercased()).")
        }
        if component == .addressField, region.isSidebar, !layout.tabPlacement.isVertical {
            return .rejected("With tabs on top the sidebar starts hidden, so the address field stays in a row that is always visible.")
        }
        if component == .sidebarToggle, region.isSidebar, !layout.tabPlacement.isVertical {
            return .rejected("With tabs on top the sidebar starts hidden, so its button must stay outside it.")
        }
        if component == .tabStrip, region.isSidebar || region == .rail {
            return .rejected("The tab strip needs a horizontal row.")
        }
        // Moving the last way to Settings somewhere hidden.
        if component == .settings || component == .menu {
            var trial = layout
            if let source {
                _ = trial.remove(at: source.index, from: source.region)
            } else if component != .flexibleSpace {
                trial.remove(component)
            }
            trial.regions[region, default: []].append(component)
            if hasSettingsAccess(layout), !hasSettingsAccess(trial) {
                return .rejected("Settings must stay reachable: keep the Settings button or the menu in a row that is always visible.")
            }
        }
        return .allowed
    }

    /// May the component at `slot` be taken out of the layout (back to the palette)?
    public static func verdict(removing slot: CanvasSlot, layout: MosaicLayout) -> CanvasVerdict {
        let list = layout.components(in: slot.region)
        guard list.indices.contains(slot.index) else { return .rejected("Nothing is there.") }
        let component = list[slot.index]
        switch component {
        case .addressField:
            return .rejected("The address field is always part of the window. Drag it to another row instead.")
        case .tabStrip where layout.tabPlacement == .top:
            return .rejected("With tabs on top the tab strip is required. Set Tabs to a sidebar first, or move it to another row.")
        case .sidebarToggle where layout.tabPlacement.isVertical && layout.sidebar == .collapsible:
            return .rejected("A collapsible sidebar needs its button. Choose Always Shown or Auto-Hide first.")
        default:
            break
        }
        var trial = layout
        _ = trial.remove(at: slot.index, from: slot.region)
        if hasSettingsAccess(layout), !hasSettingsAccess(trial) {
            return .rejected("Settings must stay reachable: keep the Settings button or the menu in the window.")
        }
        return .allowed
    }

    // MARK: Edits

    /// Moves the component at `source` so it lands before the item now at `index` of
    /// `region` (an index into the destination as it is drawn, before the move; `count`
    /// appends). This is what a drop indicator shows.
    public static func move(_ source: CanvasSlot, to region: ShellRegion, before index: Int,
                            in layout: MosaicLayout) -> CanvasEdit {
        let list = layout.components(in: source.region)
        guard list.indices.contains(source.index) else { return .rejected("Nothing is there.") }
        let component = list[source.index]
        let verdict = verdict(placing: component, in: region, layout: layout, from: source)
        if let message = verdict.message { return .rejected(message) }
        var target = min(max(index, 0), layout.components(in: region).count)
        if region == source.region {
            if target == source.index || target == source.index + 1 { return .changed(layout, selection: source) }
            if source.index < target { target -= 1 }
        }
        var edited = layout
        guard edited.move(from: source.region, at: source.index, to: region, at: target) else {
            return .rejected("\(region.title) can’t hold the \(component.title.lowercased()).")
        }
        return finish(edited, original: layout, component: component, preferred: CanvasSlot(region, target))
    }

    /// Adds `component` from the palette before `index` of `region` (nil appends).
    public static func add(_ component: ShellComponent, to region: ShellRegion, before index: Int? = nil,
                           in layout: MosaicLayout) -> CanvasEdit {
        if component != .flexibleSpace, let existing = layout.region(of: component),
           let at = layout.components(in: existing).firstIndex(of: component) {
            return move(CanvasSlot(existing, at), to: region, before: index ?? layout.components(in: region).count, in: layout)
        }
        let verdict = verdict(placing: component, in: region, layout: layout)
        if let message = verdict.message { return .rejected(message) }
        let target = min(max(index ?? layout.components(in: region).count, 0), layout.components(in: region).count)
        var edited = layout
        var list = edited.components(in: region)
        list.insert(component, at: target)
        edited.regions[region] = list
        return finish(edited, original: layout, component: component, preferred: CanvasSlot(region, target))
    }

    /// Takes the component at `slot` out of the layout.
    public static func remove(_ slot: CanvasSlot, in layout: MosaicLayout) -> CanvasEdit {
        let verdict = verdict(removing: slot, layout: layout)
        if let message = verdict.message { return .rejected(message) }
        var edited = layout
        _ = edited.remove(at: slot.index, from: slot.region)
        let result = usable(edited)
        let remaining = result.components(in: slot.region)
        let selection = remaining.isEmpty ? nil : CanvasSlot(slot.region, min(slot.index, remaining.count - 1))
        return .changed(result, selection: selection)
    }

    /// Keyboard: moves the component at `slot` one place left (-1) or right (+1) in its row.
    public static func shift(_ slot: CanvasSlot, by offset: Int, in layout: MosaicLayout) -> CanvasEdit {
        let list = layout.components(in: slot.region)
        guard list.indices.contains(slot.index) else { return .rejected("Nothing is selected.") }
        let target = slot.index + offset
        guard list.indices.contains(target) else {
            return .rejected(offset < 0 ? "Already first in \(slot.region.title)." : "Already last in \(slot.region.title).")
        }
        var edited = layout
        _ = edited.move(from: slot.region, at: slot.index, to: slot.region, at: target)
        return finish(edited, original: layout, component: list[slot.index], preferred: CanvasSlot(slot.region, target))
    }

    /// Keyboard: moves the component at `slot` to the next region up (-1) or down (+1) in
    /// `regionOrder` that accepts it, at the end (or the start, going up).
    public static func moveToAdjacentRegion(_ slot: CanvasSlot, direction: Int, in layout: MosaicLayout) -> CanvasEdit {
        let list = layout.components(in: slot.region)
        guard list.indices.contains(slot.index), let start = regionOrder.firstIndex(of: slot.region) else {
            return .rejected("Nothing is selected.")
        }
        let component = list[slot.index]
        var position = start + direction
        var lastRejection: String?
        while regionOrder.indices.contains(position) {
            let region = regionOrder[position]
            let verdict = verdict(placing: component, in: region, layout: layout, from: slot)
            if verdict.isAllowed {
                let index = direction < 0 ? layout.components(in: region).count : 0
                return move(slot, to: region, before: index, in: layout)
            }
            lastRejection = verdict.message
            position += direction
        }
        return .rejected(lastRejection ?? (direction < 0 ? "No row above takes the \(component.title.lowercased())."
                                                          : "No row below takes the \(component.title.lowercased())."))
    }

    /// Where the palette puts a component when it is added without a position (double-click,
    /// menu): the toolbar, else the title bar, else the sidebar's bottom row…
    public static func suggestedRegion(for component: ShellComponent, in layout: MosaicLayout) -> ShellRegion? {
        let order: [ShellRegion] = layout.tabPlacement.isVertical
            ? [.sidebarBottom, .sidebarTop, .toolbar, .titleBar, .tabBar, .rail]
            : [.toolbar, .titleBar, .tabBar, .rail, .sidebarBottom]
        let nonEmpty = order.filter { !layout.components(in: $0).isEmpty }
        return (nonEmpty + order).first { verdict(placing: component, in: $0, layout: layout).isAllowed }
    }

    /// The finished edit through `usable`, and where the moved component ended up.
    private static func finish(_ edited: MosaicLayout, original: MosaicLayout, component: ShellComponent,
                               preferred: CanvasSlot) -> CanvasEdit {
        let result = usable(edited)
        if result.components(in: preferred.region).indices.contains(preferred.index),
           result.components(in: preferred.region)[preferred.index] == component {
            return .changed(result, selection: preferred)
        }
        // normalized() merged gaps or repaired something: find the component again.
        if let region = result.region(of: component), let index = result.components(in: region).firstIndex(of: component) {
            return .changed(result, selection: component == .flexibleSpace ? nil : CanvasSlot(region, index))
        }
        return .changed(result, selection: nil)
    }
}

// MARK: - Undo

/// Undo and redo for values (the canvas keeps one per preset). `record` the value before
/// each change; `undo`/`redo` take the current value and return the one to show.
public struct EditHistory<Value: Equatable>: Equatable {
    public private(set) var undoStack: [Value] = []
    public private(set) var redoStack: [Value] = []
    public let limit: Int

    public init(limit: Int = 100) { self.limit = max(1, limit) }

    public var canUndo: Bool { !undoStack.isEmpty }
    public var canRedo: Bool { !redoStack.isEmpty }

    /// Call before changing `previous` into something else. Clears the redo stack.
    public mutating func record(_ previous: Value) {
        if undoStack.last == previous { return }
        undoStack.append(previous)
        if undoStack.count > limit { undoStack.removeFirst(undoStack.count - limit) }
        redoStack.removeAll()
    }

    public mutating func undo(current: Value) -> Value? {
        guard let value = undoStack.popLast() else { return nil }
        redoStack.append(current)
        return value
    }

    public mutating func redo(current: Value) -> Value? {
        guard let value = redoStack.popLast() else { return nil }
        undoStack.append(current)
        return value
    }

    public mutating func clear() {
        undoStack.removeAll()
        redoStack.removeAll()
    }
}
