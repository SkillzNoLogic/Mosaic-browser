import AppKit
import MosaicCore

/// Automation commands and `dump` fields for the canvas (see `Automation`). Every command
/// goes through the same `CanvasController` methods as the UI. Owner: **canvas**.
///
///     canvasMode presets|layout|gradient|configurations   open Settings › Mosaics in that mode
///     canvasMove <from>><region>[:<index>]                 from = component or region:index;
///                                                          index = drop position (before that item)
///     canvasDrag <from>><region>[:<index>]                 the same through the drag-and-drop path
///     canvasAdd <component>[><region>[:<index>]]           from the palette (no region: suggested row)
///     canvasRemove <component | region:index>
///     canvasSelect <component | region:index | none>
///     canvasKey left|right|up|down|alt-left|alt-right|alt-up|alt-down|return|escape|delete|undo|redo
///     canvasUndo / canvasRedo / canvasReset
///     canvasTabs top|sidebarLeading|sidebarTrailing
///     gradientTarget window|newTab|workspace
///     gradientApply <preset name>                          use a Gradient Studio preset on the target
///     gradientSet <key>=<value>                            kind=mesh|linear|radial|angular, angle, sharpness,
///                                                          noise, opacity, raw=on|off, centerX, centerY,
///                                                          addSource=x,y,#hex, stops=#hex@0,#hex@1
///     gradientSelect <n>                                   select colour n (0-based) in the open studio
///     gradientFile <path.json>                             a GradientSpec as JSON
///     newTabBackground plain|window|custom
///     configSave <name> / configApply <name|id> / configUpdate <name|id> / configDelete <name|id>
///     configRename <old>=<new>
///     configExport <name|id|current> <out path>
///     configImport <path>
///     configRestoreAll
@MainActor
enum CanvasAutomation {
    /// Returns true when `action` was a canvas command.
    static func handle(_ action: String, _ command: [String: Any], model: BrowserModel) -> Bool {
        let value = command["value"] as? String ?? ""
        let canvas = model.canvas
        switch action {
        case "canvasMode":
            if let mode = CanvasController.Mode(rawValue: value) {
                canvas.mode = mode
                model.showSettings(.mosaics)
            }
        case "canvasMove", "canvasDrag":
            guard let (from, region, index) = parseMove(value, canvas: canvas) else {
                canvas.say("Automation: can’t read “\(value)”.", error: true)
                return true
            }
            let target = index ?? canvas.layout.components(in: region).count
            if action == "canvasDrag", let component = canvas.component(at: from) {
                canvas.dragging = .placed(from, component)
                let verdict = canvas.verdict(for: .placed(from, component), in: region)
                if let message = verdict.message {
                    canvas.say(message, error: true)
                    canvas.dragging = nil
                } else {
                    canvas.drop(.placed(from, component), in: region, before: target)
                }
            } else {
                canvas.move(from, to: region, before: target)
            }
        case "canvasAdd":
            let parts = value.split(separator: ">", maxSplits: 1).map(String.init)
            guard let component = ShellComponent(rawValue: parts.first ?? "") else { return true }
            if parts.count > 1, let (region, index) = parseRegion(parts[1]) {
                canvas.add(component, to: region, before: index)
            } else {
                canvas.add(component)
            }
        case "canvasRemove":
            if let slot = parseSlot(value, canvas: canvas) { canvas.remove(slot) }
        case "canvasSelect":
            canvas.selection = value == "none" ? nil : parseSlot(value, canvas: canvas)
        case "canvasKey":
            key(value, canvas: canvas)
        case "canvasUndo": canvas.undo()
        case "canvasRedo": canvas.redo()
        case "canvasReset": canvas.resetToPreset()
        case "canvasTabs":
            if let placement = TabPlacement(rawValue: value) { canvas.setTabPlacement(placement) }
        case "gradientTarget":
            if let target = CanvasController.GradientTarget(rawValue: value) { canvas.gradientTarget = target }
        case "gradientApply":
            if let preset = GradientPresets.named(value) {
                var next = preset
                let current = canvas.background(for: canvas.gradientTarget).gradient
                next.noise = current.noise
                next.opacity = current.opacity
                next.raw = current.raw
                canvas.useGradient(next, for: canvas.gradientTarget)
            } else {
                canvas.say("Automation: no gradient preset “\(value)”.", error: true)
            }
        case "gradientSet":
            setGradient(value, canvas: canvas)
        case "gradientSelect":
            canvas.studioSelectionRequest = Int(value)
        case "gradientFile":
            if let data = try? Data(contentsOf: URL(fileURLWithPath: value)),
               let spec = try? JSONDecoder().decode(GradientSpec.self, from: data) {
                canvas.useGradient(spec, for: canvas.gradientTarget)
            } else {
                canvas.say("Automation: couldn’t read a gradient from \(value).", error: true)
            }
        case "newTabBackground":
            if let mode = NewTabBackground.Mode(rawValue: value) { canvas.set(\.appearance.newTabBackground.mode, mode) }
        case "configSave":
            canvas.saveCurrent(as: value)
        case "configApply":
            if let id = canvas.library.find(value)?.id { canvas.applyConfiguration(id) }
        case "configUpdate":
            if let id = canvas.library.find(value)?.id { canvas.updateConfiguration(id) }
        case "configDelete":
            if let id = canvas.library.find(value)?.id { canvas.deleteConfiguration(id) }
        case "configRename":
            let parts = value.split(separator: "=", maxSplits: 1).map(String.init)
            if parts.count == 2, let id = canvas.library.find(parts[0])?.id { canvas.renameConfiguration(id, to: parts[1]) }
        case "configExport":
            guard let out = command["out"] as? String else { return true }
            let id = value == "current" ? nil : canvas.library.find(value)?.id
            do { try canvas.export(id, to: URL(fileURLWithPath: out)) } catch {
                canvas.say("Couldn’t export: \(error.localizedDescription)", error: true)
            }
        case "configImport":
            canvas.importFile(URL(fileURLWithPath: value))
        case "configRestoreAll":
            canvas.restoreAllPresetDefaults()
        default:
            return false
        }
        return true
    }

    // MARK: Parsing

    /// "toolbar" or "toolbar:3".
    private static func parseRegion(_ text: String) -> (ShellRegion, Int?)? {
        let parts = text.split(separator: ":").map(String.init)
        guard let region = ShellRegion(rawValue: parts.first ?? "") else { return nil }
        return (region, parts.count > 1 ? Int(parts[1]) : nil)
    }

    /// "region:index", or a component's name (its first occurrence).
    private static func parseSlot(_ text: String, canvas: CanvasController) -> CanvasSlot? {
        if let (region, index) = parseRegion(text), let index { return CanvasSlot(region, index) }
        return ShellComponent(rawValue: text).flatMap(canvas.locate)
    }

    private static func parseMove(_ text: String, canvas: CanvasController) -> (CanvasSlot, ShellRegion, Int?)? {
        let parts = text.split(separator: ">", maxSplits: 1).map(String.init)
        guard parts.count == 2, let from = parseSlot(parts[0], canvas: canvas),
              let (region, index) = parseRegion(parts[1]) else { return nil }
        return (from, region, index)
    }

    /// The same calls the canvas's key handler makes.
    private static func key(_ name: String, canvas: CanvasController) {
        switch name {
        case "left": canvas.grab != nil ? canvas.moveSelected(.left) : canvas.moveSelection(.left)
        case "right": canvas.grab != nil ? canvas.moveSelected(.right) : canvas.moveSelection(.right)
        case "up": canvas.grab != nil ? canvas.moveSelected(.up) : canvas.moveSelection(.up)
        case "down": canvas.grab != nil ? canvas.moveSelected(.down) : canvas.moveSelection(.down)
        case "alt-left": canvas.moveSelected(.left)
        case "alt-right": canvas.moveSelected(.right)
        case "alt-up": canvas.moveSelected(.up)
        case "alt-down": canvas.moveSelected(.down)
        case "return", "space": canvas.toggleGrab()
        case "escape": canvas.endGrab(commit: false)
        case "delete": canvas.removeSelected()
        case "undo": canvas.undo()
        case "redo": canvas.redo()
        default: break
        }
    }

    private static func setGradient(_ assignment: String, canvas: CanvasController) {
        guard let eq = assignment.firstIndex(of: "=") else { return }
        let key = String(assignment[..<eq]), value = String(assignment[assignment.index(after: eq)...])
        let target = canvas.gradientTarget
        var g = canvas.background(for: target).gradient
        let number = Double(value)
        switch key {
        case "kind": if let kind = GradientSpec.Kind(rawValue: value) { g = g.converted(to: kind) }
        case "angle": g.angle = number ?? g.angle
        case "sharpness": g.sharpness = number ?? g.sharpness
        case "noise": g.noise = number ?? g.noise
        case "opacity": g.opacity = number ?? g.opacity
        case "centerX": g.centerX = number ?? g.centerX
        case "centerY": g.centerY = number ?? g.centerY
        case "raw": g.raw = value == "on" || value == "true"
        case "addSource":
            let parts = value.split(separator: ",").map(String.init)
            if parts.count == 3, let x = Double(parts[0]), let y = Double(parts[1]) {
                if g.sources.isEmpty { g.sources = g.meshSources }
                g.sources.append(GradientSource(x: x, y: y, color: parts[2]))
            }
        case "stops":
            g.stops = value.split(separator: ",").compactMap { item in
                let p = item.split(separator: "@").map(String.init)
                guard p.count == 2, let location = Double(p[1]) else { return nil }
                return GradientStop(color: p[0], location: location)
            }
        default: return
        }
        canvas.useGradient(g, for: target)
    }

    // MARK: Dump

    static func dumpFields(model: BrowserModel) -> [String: Any] {
        let canvas = model.canvas
        let layout = canvas.layout
        var fields: [String: Any] = [
            "mode": canvas.mode.rawValue,
            "canUndo": canvas.canUndo,
            "canRedo": canvas.canRedo,
            "undoDepth": canvas.undoDepth,
            "grabbing": canvas.grab?.component.rawValue ?? "",
            "message": canvas.message?.text ?? "",
            "messageIsError": canvas.message?.isError ?? false,
            "palette": LayoutCanvasRules.palette(layout).map(\.rawValue),
            "hasSettingsAccess": LayoutCanvasRules.hasSettingsAccess(layout),
            "gradientTarget": canvas.gradientTarget.rawValue,
            "usesGradient": Dictionary(uniqueKeysWithValues: CanvasController.GradientTarget.allCases.map {
                ($0.rawValue, canvas.usesGradient($0))
            }),
            "workspaceID": canvas.workspaceID,
            "meshImagesRendered": MeshImageCache.renderCount,
            "configurations": canvas.library.configurations.map {
                ["id": $0.id, "name": $0.name, "preset": $0.layout.preset.rawValue] as [String: Any]
            },
            "activeConfiguration": canvas.activeConfiguration?.name ?? "",
        ]
        if let slot = canvas.selection {
            fields["selection"] = ["region": slot.region.rawValue, "index": slot.index,
                                   "component": canvas.component(at: slot)?.rawValue ?? ""] as [String: Any]
        }
        if let outcome = canvas.lastImport {
            fields["lastImport"] = ["file": outcome.file, "name": outcome.name ?? "", "messages": outcome.messages,
                                    "error": outcome.error ?? ""] as [String: Any]
        }
        return fields
    }
}
