import Foundation
import Testing
@testable import MosaicCore

@Suite("Canvas: colours (Gradient Studio chart)")
struct CanvasColorTests {
    @Test("Every encoding round-trips a colour")
    func encodingsRoundTrip() {
        let samples = ["#FF7E5F", "#2C73D2", "#000000", "#FFFFFF", "#808080", "#13A35B", "#FDFCFB"]
        for hex in samples {
            let color = ColorValue(hex: hex)!
            for encoding in ColorEncoding.allCases where encoding != .hex {
                let back = encoding.color(from: encoding.values(of: color))
                #expect(back.hex == hex, "\(hex) via \(encoding)")
            }
        }
    }

    @Test("Hex parsing accepts short, long and alpha forms and rejects junk")
    func hexParsing() {
        #expect(ColorValue(hex: "#abc")?.hex == "#AABBCC")
        #expect(ColorValue(hex: "112233")?.hex == "#112233")
        #expect(ColorValue(hex: "#11223344")?.hex == "#112233")
        #expect(ColorValue(hex: "#12345G") == nil)
        #expect(ColorValue(hex: "") == nil)
        #expect(ColorValue(hex: "#-12345") == nil)
    }

    @Test("Typed component values are clamped; unreadable ones keep the current value")
    func parsingFields() {
        let current = ColorValue(hex: "#336699")!
        let red = ColorEncoding.rgb.color(parsing: ["300", "abc", "-5"], current: current)
        #expect(red.hex == "#FF6600")
        let hsl = ColorEncoding.hsl.color(parsing: ["120", "100", "50"], current: current)
        #expect(hsl.hex == "#00FF00")
    }

    @Test("CSS-style strings")
    func strings() {
        let c = ColorValue(hex: "#FF0000")!
        #expect(ColorEncoding.rgb.string(for: c) == "rgb(255, 0, 0)")
        #expect(ColorEncoding.hsl.string(for: c) == "hsl(0 100% 50%)")
        #expect(ColorEncoding.hex.string(for: c) == "#FF0000")
    }
}

@Suite("Canvas: gradients")
struct CanvasGradientTests {
    @Test("Old gradient JSON (before the studio) still decodes, with defaults for new fields")
    func backwardCompatible() throws {
        let json = ##"{"kind":"radial","angle":90,"stops":[{"color":"#112233","location":0},{"color":"#445566","location":1}]}"##
        let g = try JSONDecoder().decode(GradientSpec.self, from: Data(json.utf8))
        #expect(g.kind == .radial)
        #expect(g.angle == 90)
        #expect(g.stops.count == 2)
        #expect(g.sources.isEmpty)
        #expect(g.sharpness == 2.2)
        #expect(g.noise == 0)
        #expect(g.opacity == 1)
        #expect(!g.raw)
        #expect(g.centerX == 0.5 && g.centerY == 0.5)
    }

    @Test("Unknown kinds fall back; Eminence sources (with `hex`) decode")
    func forgiving() throws {
        let json = ##"{"kind":"conic","sources":[{"x":0.2,"y":0.3,"hex":"#FF0000"},{"x":"bad"}],"sharpness":3}"##
        let g = try JSONDecoder().decode(GradientSpec.self, from: Data(json.utf8))
        #expect(g.kind == .linear)
        #expect(g.sources.count == 2)
        #expect(g.sources[0].color == "#FF0000")
        #expect(g.sources[1].x == 0.5)
        #expect(g.sharpness == 3)
    }

    @Test("A layout written before the studio loads and keeps its background")
    func oldLayoutLoads() throws {
        let json = #"""
        {"preset":"chrome","name":"Old","tabPlacement":"top","regions":{"titleBar":["tabStrip","newTab"],"toolbar":["back","addressField","menu"]},
         "appearance":{"accent":"#FF0000","background":{"kind":"gradient","gradient":{"kind":"linear","angle":45,"stops":[{"color":"#000000","location":0},{"color":"#FFFFFF","location":1}]}}}}
        """#
        let layout = try MosaicLayout.from(json: Data(json.utf8))
        #expect(layout.appearance.background.kind == .gradient)
        #expect(layout.appearance.background.gradient.angle == 45)
        #expect(layout.appearance.newTabBackground.mode == .plain)
        #expect(layout.appearance.workspaceBackgrounds.isEmpty)
        #expect(layout.components(in: .toolbar) == [.back, .addressField, .menu])
    }

    @Test("A mesh background round-trips through a layout file")
    func meshRoundTrip() throws {
        var layout = MosaicLayout.preset(.zenArc)
        var g = GradientPresets.named("Aurora")!
        g.noise = 0.3
        g.opacity = 0.8
        g.raw = true
        layout.appearance.background = BackgroundSpec(kind: .gradient, gradient: g)
        layout.appearance.newTabBackground = NewTabBackground(mode: .custom, custom: BackgroundSpec(kind: .solid, color: "#123456"))
        layout.appearance.workspaceBackgrounds = ["W1": BackgroundSpec(kind: .gradient, gradient: GradientPresets.named("Ocean")!)]
        let decoded = try MosaicLayout.from(json: layout.jsonData())
        #expect(decoded == layout.normalized())
        #expect(decoded.appearance.background.gradient.sources.count == 4)
        #expect(decoded.appearance.background(forWorkspace: "W1").gradient.sources.count == 2)
        #expect(decoded.appearance.background(forWorkspace: "other") == decoded.appearance.background)
    }

    @Test("Bounds: numbers clamped, invalid colours dropped, lists capped")
    func clamping() {
        var g = GradientSpec(kind: .mesh, angle: .infinity, stops: (0..<40).map { GradientStop(color: "#000000", location: Double($0)) })
        g.sources = (0..<50).map { GradientSource(x: 3, y: -2, color: $0 == 0 ? "nope" : "#FFFFFF", spread: 99) }
        g.sharpness = 40
        g.noise = -1
        g.opacity = 0
        g.centerX = 7
        let c = g.clamped()
        #expect(c.angle == 135)
        #expect(c.stops.count == GradientSpec.maxStops)
        #expect(c.stops.allSatisfy { (0...1).contains($0.location) })
        #expect(c.sources.count == GradientSpec.maxSources)
        #expect(c.sources.allSatisfy { $0.color == "#FFFFFF" && $0.x == 1 && $0.y == 0 && $0.spread == 3 })
        #expect(Set(c.sources.map(\.id)).count == c.sources.count)
        #expect(c.sharpness == 6 && c.noise == 0 && c.opacity == 0.1 && c.centerX == 1)
    }

    @Test("Presets are valid and unchanged by clamping")
    func presetsValid() {
        for preset in GradientPresets.all {
            #expect(preset.gradient.clamped() == preset.gradient, "\(preset.name)")
        }
        #expect(GradientPresets.named("four corners") != nil)
        #expect(GradientPresets.mesh.count == 12)
    }

    @Test("Mesh renders the requested size and is its sources' colour at each source")
    func meshRender() {
        let g = GradientPresets.named("Four Corners")!
        let image = g.renderMesh(width: 64, aspect: 2)
        #expect(image.width == 64 && image.height == 32)
        #expect(image.pixels.count == 64 * 32 * 4)
        let topLeft = ColorValue(r: Double(image.pixels[0]) / 255, g: Double(image.pixels[1]) / 255, b: Double(image.pixels[2]) / 255)
        let expected = ColorValue(hex: "#FF6B6B")!
        #expect(abs(topLeft.r - expected.r) < 0.12 && abs(topLeft.g - expected.g) < 0.12)
        let at = g.meshColor(atX: 1, y: 1, aspect: 2)
        #expect(at.hex == "#6BCB77")
        // Degenerate input never crashes.
        let empty = GradientSpec(kind: .mesh, stops: []).renderMesh(width: 0, aspect: .nan)
        #expect(empty.width == 1 && empty.pixels.count == empty.width * empty.height * 4)
    }

    @Test("Switching kinds keeps the colours in order")
    func conversion() {
        let linear = GradientSpec(kind: .linear, angle: 0, stops: [
            GradientStop(color: "#FF0000", location: 0), GradientStop(color: "#00FF00", location: 0.5),
            GradientStop(color: "#0000FF", location: 1),
        ])
        let mesh = linear.converted(to: .mesh)
        #expect(mesh.kind == .mesh)
        #expect(mesh.sources.map(\.color) == ["#FF0000", "#00FF00", "#0000FF"])
        #expect(mesh.sources[0].x == 0 && mesh.sources[2].x == 1)
        let back = mesh.converted(to: .linear)
        #expect(back.stops.map(\.color) == ["#FF0000", "#00FF00", "#0000FF"])
        #expect(back.stops.first?.location == 0 && back.stops.last?.location == 1)
        #expect(linear.converted(to: .radial).stops == linear.stops)
    }

    @Test("Colour along the stops")
    func stopColor() {
        let g = GradientSpec(stops: [GradientStop(color: "#000000", location: 0), GradientStop(color: "#FFFFFF", location: 1)])
        #expect(g.stopColor(at: 0.5).hex == "#808080")
        #expect(g.stopColor(at: -3).hex == "#000000")
        #expect(g.stopColor(at: .nan).hex == "#000000")
    }
}

@Suite("Canvas: layout editing rules")
struct CanvasRulesTests {
    let chrome = MosaicLayout.preset(.chrome)

    @Test("Palette offers what isn't placed, and always flexible space")
    func palette() {
        let palette = LayoutCanvasRules.palette(chrome)
        #expect(palette.first == .flexibleSpace)
        #expect(!palette.contains(.back))
        #expect(palette.contains(.settings))
        #expect(!palette.contains(.tabStrip))
        let zen = LayoutCanvasRules.palette(.preset(.zenArc))
        #expect(!zen.contains(.tabStrip), "vertical tabs: the tab strip would not be drawn")
    }

    @Test("Reordering within a row uses drop-indicator positions")
    func reorder() {
        // toolbar: back, forward, reload, home, addressField, …
        let edit = LayoutCanvasRules.move(CanvasSlot(.toolbar, 0), to: .toolbar, before: 3, in: chrome)
        #expect(edit.layout?.components(in: .toolbar).prefix(4) == [.forward, .reload, .back, .home])
        if case .changed(_, let selection) = edit { #expect(selection == CanvasSlot(.toolbar, 2)) }
        // Dropping onto its own place changes nothing.
        #expect(LayoutCanvasRules.move(CanvasSlot(.toolbar, 1), to: .toolbar, before: 2, in: chrome).layout == chrome)
    }

    @Test("Moving between regions")
    func moveBetweenRegions() {
        let edit = LayoutCanvasRules.move(CanvasSlot(.toolbar, 3), to: .titleBar, before: 0, in: chrome)
        let layout = edit.layout!
        #expect(layout.components(in: .titleBar).first == .home)
        #expect(!layout.components(in: .toolbar).contains(.home))
    }

    @Test("Invalid drops are rejected with a reason")
    func rejections() {
        let field = chrome.components(in: .toolbar).firstIndex(of: .addressField)!
        #expect(LayoutCanvasRules.move(CanvasSlot(.toolbar, field), to: .rail, before: 0, in: chrome).message != nil)
        #expect(LayoutCanvasRules.move(CanvasSlot(.toolbar, field), to: .sidebarMiddle, before: 0, in: chrome).message != nil,
                "with tabs on top the sidebar is hidden")
        #expect(LayoutCanvasRules.move(CanvasSlot(.titleBar, 0), to: .sidebarTop, before: 0, in: chrome).message != nil,
                "tab strip into a sidebar")
        // A vertical layout may hold the address field in its sidebar.
        let zen = MosaicLayout.preset(.zenArc)
        #expect(LayoutCanvasRules.verdict(placing: .addressField, in: .sidebarTop, layout: zen).isAllowed)
    }

    @Test("The address field, required tab strip and last way to Settings can't be removed")
    func protectedRemovals() {
        let field = chrome.components(in: .toolbar).firstIndex(of: .addressField)!
        #expect(LayoutCanvasRules.remove(CanvasSlot(.toolbar, field), in: chrome).message != nil)
        #expect(LayoutCanvasRules.remove(CanvasSlot(.titleBar, 0), in: chrome).message != nil)
        let menu = chrome.components(in: .toolbar).firstIndex(of: .menu)!
        #expect(LayoutCanvasRules.remove(CanvasSlot(.toolbar, menu), in: chrome).message != nil)
        // With the Settings button added, the menu may go.
        let withSettings = LayoutCanvasRules.add(.settings, to: .toolbar, in: chrome).layout!
        let menuNow = withSettings.components(in: .toolbar).firstIndex(of: .menu)!
        let removed = LayoutCanvasRules.remove(CanvasSlot(.toolbar, menuNow), in: withSettings).layout!
        #expect(!removed.contains(.menu) && removed.contains(.settings))
        // Moving Settings into a horizontal layout's hidden sidebar is refused then.
        let settingsAt = removed.components(in: .toolbar).firstIndex(of: .settings)!
        #expect(LayoutCanvasRules.move(CanvasSlot(.toolbar, settingsAt), to: .sidebarBottom, before: 0, in: removed).message != nil)
    }

    @Test("usable() restores a way to Settings and the address field")
    func usableRepairs() {
        var broken = chrome
        broken.remove(.menu)
        broken.remove(.addressField)
        let fixed = LayoutCanvasRules.usable(broken)
        #expect(fixed.contains(.addressField))
        #expect(LayoutCanvasRules.hasSettingsAccess(fixed))
        for preset in MosaicPresetID.allCases {
            #expect(LayoutCanvasRules.usable(.preset(preset)) == .preset(preset), "\(preset) is usable as shipped")
        }
        // Tabs on top with the sidebar button left inside the (hidden) sidebar: it comes out.
        var hidden = MosaicLayout.preset(.zenArc)
        hidden.tabPlacement = .top
        let repaired = LayoutCanvasRules.usable(hidden)
        #expect(repaired.region(of: .sidebarToggle)?.isSidebar == false)
        #expect(LayoutCanvasRules.verdict(placing: .sidebarToggle, in: .sidebarTop, layout: chrome).message != nil)
    }

    @Test("Palette additions, including repeated flexible space")
    func paletteAdd() {
        let a = LayoutCanvasRules.add(.downloadsPage, to: .rail, in: chrome).layout!
        #expect(a.components(in: .rail) == [.downloadsPage])
        let b = LayoutCanvasRules.add(.flexibleSpace, to: .toolbar, before: 1, in: a).layout!
        #expect(b.components(in: .toolbar)[1] == .flexibleSpace)
        #expect(LayoutCanvasRules.add(.addressField, to: .rail, in: chrome).message != nil)
        #expect(LayoutCanvasRules.suggestedRegion(for: .settings, in: chrome) == .toolbar)
        #expect(LayoutCanvasRules.suggestedRegion(for: .downloadsPage, in: .preset(.zenArc)) == .sidebarBottom)
    }

    @Test("Keyboard moves: left/right within a row, up/down between rows")
    func keyboard() {
        let left = LayoutCanvasRules.shift(CanvasSlot(.toolbar, 1), by: -1, in: chrome)
        #expect(left.layout?.components(in: .toolbar).prefix(2) == [.forward, .back])
        #expect(LayoutCanvasRules.shift(CanvasSlot(.toolbar, 0), by: -1, in: chrome).message != nil)
        // Back (toolbar) up to the title bar's end.
        let up = LayoutCanvasRules.moveToAdjacentRegion(CanvasSlot(.toolbar, 0), direction: -1, in: chrome)
        #expect(up.layout?.components(in: .titleBar).last == .back)
        // The address field going down skips the rail (too narrow) and the hidden sidebar.
        let field = chrome.components(in: .toolbar).firstIndex(of: .addressField)!
        let down = LayoutCanvasRules.moveToAdjacentRegion(CanvasSlot(.toolbar, field), direction: 1, in: chrome)
        #expect(down.layout?.components(in: .tabBar) == [.addressField])
        let further = LayoutCanvasRules.moveToAdjacentRegion(CanvasSlot(.tabBar, 0), direction: 1, in: down.layout!)
        #expect(further.message != nil)
    }
}

@Suite("Canvas: undo history")
struct CanvasHistoryTests {
    @Test("Undo, redo, redo cleared by a new change, limit")
    func history() {
        var h = EditHistory<Int>(limit: 3)
        #expect(!h.canUndo)
        h.record(1)  // 1 → 2
        h.record(2)  // 2 → 3
        #expect(h.undo(current: 3) == 2)
        #expect(h.undo(current: 2) == 1)
        #expect(h.undo(current: 1) == nil)
        #expect(h.redo(current: 1) == 2)
        h.record(2)  // a new change clears redo
        #expect(!h.canRedo)
        for v in 10..<20 { h.record(v) }
        #expect(h.undoStack.count == 3)
        h.clear()
        #expect(!h.canUndo && !h.canRedo)
    }
}

@Suite("Canvas: configurations and import/export")
struct CanvasConfigurationTests {
    @Test("Save, unique names, rename, delete, active")
    func library() {
        var lib = ConfigurationLibrary()
        let a = lib.save(name: "  Work  ", layout: .preset(.chrome))!
        #expect(a.name == "Work" && a.layout.name == "Work")
        #expect(lib.activeID == a.id)
        let b = lib.save(name: "work", layout: .preset(.zenArc))!
        #expect(b.name == "work 2")
        let renamed = lib.rename(b.id, to: "Work")
        #expect(renamed)
        #expect(lib.configuration(b.id)?.name == "Work 2")
        let blank = lib.rename(b.id, to: "   ")
        #expect(!blank)
        #expect(lib.find("WORK")?.id == a.id)
        #expect(lib.activeID == b.id, "the last saved is active")
        lib.activeID = a.id
        let deletedB = lib.delete(b.id)
        #expect(deletedB)
        #expect(lib.activeID == a.id, "deleting another keeps the active one")
        let deletedA = lib.delete(a.id)
        #expect(deletedA)
        #expect(lib.activeID == nil)
        #expect(ConfigurationLibrary.cleanName(String(repeating: "x", count: 500))?.count == ConfigurationLibrary.maxNameLength)
    }

    @Test("The library survives a write/load round trip and a damaged entry")
    func persistence() throws {
        var lib = ConfigurationLibrary()
        lib.save(name: "One", layout: .preset(.opera))
        lib.save(name: "Two", layout: .preset(.safari))
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("canvas-\(UUID().uuidString)/configs.json")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        try lib.write(to: url)
        let loaded = ConfigurationLibrary.load(from: url)
        #expect(loaded.configurations.map(\.name) == ["One", "Two"])
        #expect(loaded.configurations.map(\.layout) == lib.configurations.map(\.layout))

        // Damage the first entry: the second survives.
        var json = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        var entries = json["configurations"] as! [[String: Any]]
        entries[0]["layout"] = "garbage"
        json["configurations"] = entries
        try JSONSerialization.data(withJSONObject: json).write(to: url)
        #expect(ConfigurationLibrary.load(from: url).configurations.map(\.name) == ["Two"])
        // Not JSON at all: empty library, no crash.
        try Data("{{{".utf8).write(to: url)
        #expect(ConfigurationLibrary.load(from: url).configurations.isEmpty)
    }

    @Test("Export then import gives the same layout, with no messages")
    func roundTrip() throws {
        var layout = MosaicLayout.preset(.opera).withGXStyling()
        layout.appearance.background.gradient = GradientPresets.named("Mesh")!
        let data = try LayoutTransfer.export(name: "GX Mesh", layout: layout)
        let report = try LayoutTransfer.importLayout(data).get()
        #expect(report.name == "GX Mesh")
        #expect(report.messages.isEmpty, "\(report.messages)")
        var expected = LayoutCanvasRules.usable(layout)
        expected.name = "GX Mesh"
        #expect(report.layout == expected)
    }

    @Test("A bare layout (the shell's exportLayout) imports")
    func bareLayout() throws {
        let data = try MosaicLayout.preset(.safari).jsonData()
        let report = try LayoutTransfer.importLayout(data).get()
        #expect(report.layout == .preset(.safari))
        #expect(report.messages.isEmpty)
    }

    @Test("Unknown components and regions are dropped and reported; values clamped")
    func repairs() throws {
        let json = #"""
        {"format":"dev.mosaic.layout","version":99,"name":"Odd\u0007 one","layout":{
          "preset":"chrome","tabPlacement":"top","sidebarWidth":5000,
          "regions":{"titleBar":["tabStrip","teleporter","newTab"],"toolbar":["back","back","addressField",42],
                     "basement":["home"],"rail":["addressField"]},
          "appearance":{"cornerRadius":-4,"accent":"blue","background":{"kind":"gradient","gradient":{"kind":"mesh","sharpness":50,"sources":[{"x":9,"y":0,"color":"#FF0000"}]}}}}}
        """#
        let report = try LayoutTransfer.importLayout(Data(json.utf8)).get()
        let all = report.messages.joined(separator: "\n")
        #expect(report.name == "Odd one")
        #expect(all.contains("teleporter"))
        #expect(all.contains("basement"))
        #expect(all.contains("newer Mosaic"))
        #expect(all.contains("sidebar width"))
        #expect(all.contains("corner radius"))
        #expect(all.contains("accent"))
        #expect(all.contains("duplicates"))
        #expect(all.contains("weren’t lists"))
        #expect(all.contains("background"))
        #expect(all.contains("Settings"), "no menu or settings in the file: the menu is added")
        let layout = report.layout
        #expect(layout.sidebarWidth == 420)
        #expect(layout.appearance.cornerRadius == 0)
        #expect(layout.appearance.background.gradient.sharpness == 6)
        #expect(layout.appearance.background.gradient.sources.first?.x == 1)
        #expect(layout.components(in: .toolbar).filter { $0 == .back }.count == 1)
        #expect(layout.components(in: .rail).isEmpty)
        #expect(LayoutCanvasRules.hasSettingsAccess(layout))
    }

    @Test("Malformed files are refused with a reason, never a crash")
    func malformed() {
        func error(_ text: String) -> LayoutTransfer.ImportError? {
            if case .failure(let e) = LayoutTransfer.importLayout(Data(text.utf8)) { return e }
            return nil
        }
        #expect(error("") == .empty)
        #expect(error("{\"preset\": ") == .notJSON)
        #expect(error("[1,2,3]") == .notALayout)
        #expect(error("\"hello\"") == .notALayout)
        #expect(error("{\"recipe\":\"soup\"}") == .notALayout)
        #expect(error("{\"layout\": 7}") == .notALayout)
        #expect(LayoutTransfer.importLayout(Data(repeating: 0x20, count: LayoutTransfer.maxFileSize + 1)) == .failure(.tooLarge(LayoutTransfer.maxFileSize + 1)))
        var bytes = [UInt8](repeating: 0, count: 4096)
        for i in bytes.indices { bytes[i] = UInt8((i * 7919) & 0xFF) }
        #expect(error(String(decoding: bytes, as: UTF8.self)) != nil)
        // Wrong types everywhere still give a usable layout.
        let weird = #"{"preset":7,"regions":"none","tabPlacement":["top"],"appearance":[]}"#
        if case .success(let report) = LayoutTransfer.importLayout(Data(weird.utf8)) {
            #expect(report.layout.contains(.addressField))
            #expect(LayoutCanvasRules.hasSettingsAccess(report.layout))
        } else {
            Issue.record("a readable object with a preset-like shape should import")
        }
    }
}
