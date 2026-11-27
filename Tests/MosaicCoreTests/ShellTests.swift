import Foundation
import Testing
@testable import MosaicCore

@Suite("Mosaic layout model (shell)")
struct ShellTests {
    @Test("Every preset survives a JSON round trip unchanged")
    func presetsRoundTrip() throws {
        for id in MosaicPresetID.allCases {
            let layout = MosaicLayout.preset(id)
            let decoded = try MosaicLayout.from(json: layout.jsonData())
            #expect(decoded == layout.normalized(), "\(id)")
        }
    }

    @Test("Presets are already normalised (nothing to repair)")
    func presetsAreNormal() {
        for id in MosaicPresetID.allCases {
            let layout = MosaicLayout.preset(id)
            #expect(layout.normalized() == layout, "\(id)")
        }
    }

    @Test("Presets have the shape of the browsers they imitate")
    func presetShapes() {
        let zen = MosaicLayout.preset(.zenArc)
        #expect(zen.tabPlacement == .sidebarLeading)
        #expect(zen.components(in: .toolbar).isEmpty)
        #expect(zen.region(of: .addressField) == .sidebarMiddle)
        #expect(zen.appearance.contentInset > 0)

        let chrome = MosaicLayout.preset(.chrome)
        #expect(chrome.tabPlacement == .top)
        #expect(chrome.region(of: .tabStrip) == .titleBar)
        #expect(chrome.bookmarksBar)
        #expect(chrome.components(in: .toolbar).last == .menu)

        let opera = MosaicLayout.preset(.opera)
        #expect(!opera.components(in: .rail).isEmpty)

        let safari = MosaicLayout.preset(.safari)
        #expect(safari.region(of: .addressField) == .titleBar)
        #expect(safari.region(of: .tabStrip) == .tabBar)
        #expect(safari.addressBar == .unified)
    }

    @Test("Unknown components, regions and enum values are skipped, not fatal")
    func forgivingDecoding() throws {
        let json = """
        {"preset":"opera","tabPlacement":"diagonal","regions":{"toolbar":["back","teleport","addressField"],
         "ceiling":["reload"]},"appearance":{"density":"huge","accent":"#123456"}}
        """
        let layout = try MosaicLayout.from(json: Data(json.utf8))
        #expect(layout.preset == .opera)
        #expect(layout.tabPlacement == MosaicLayout.preset(.opera).tabPlacement)
        #expect(layout.components(in: .toolbar).prefix(2) == [.back, .addressField])
        #expect(!layout.contains(.reload))
        #expect(layout.appearance.density == .regular)
        #expect(layout.appearance.accent == "#123456")
        // The tab strip was missing; normalisation puts it back.
        #expect(layout.region(of: .tabStrip) == .titleBar)
    }

    @Test("An empty object decodes to a usable layout")
    func emptyObject() throws {
        let layout = try MosaicLayout.from(json: Data("{}".utf8))
        #expect(layout.contains(.addressField))
        #expect(layout.contains(.tabStrip))
    }

    @Test("Inserting a component that exists elsewhere moves it")
    func insertMoves() {
        var layout = MosaicLayout.preset(.chrome)
        let inserted = layout.insert(.home, in: .titleBar, at: 0)
        #expect(inserted)
        #expect(layout.components(in: .titleBar).first == .home)
        #expect(!layout.components(in: .toolbar).contains(.home))
    }

    @Test("Moving within a region lands at the requested index")
    func moveWithinRegion() {
        var layout = MosaicLayout.preset(.chrome)
        let before = layout.components(in: .toolbar)
        let inserted = layout.insert(.back, in: .toolbar, at: before.count)
        #expect(inserted)
        #expect(layout.components(in: .toolbar).last == .back)
        #expect(layout.components(in: .toolbar).count == before.count)
    }

    @Test("Regions refuse components they cannot hold")
    func regionRules() {
        var layout = MosaicLayout.preset(.opera)
        let intoRail = layout.insert(.addressField, in: .rail)
        let intoSidebar = layout.insert(.tabStrip, in: .sidebarTop)
        #expect(!intoRail && !intoSidebar)
        #expect(layout.region(of: .addressField) == .toolbar)
        let railCount = layout.components(in: .rail).count
        let index = layout.components(in: .toolbar).firstIndex(of: .addressField) ?? 0
        let moved = layout.move(from: .toolbar, at: index, to: .rail, at: 0)
        #expect(!moved)
        #expect(layout.components(in: .rail).count == railCount)
    }

    @Test("move(from:at:to:at:) rejects bad indexes")
    func moveBadIndex() {
        var layout = MosaicLayout.preset(.safari)
        let copy = layout
        let moved = layout.move(from: .toolbar, at: 5, to: .titleBar, at: 0)
        let removed = layout.remove(at: 99, from: .titleBar)
        #expect(!moved && removed == nil)
        #expect(layout == copy)
    }

    @Test("Normalisation removes duplicates and repeated gaps, clamps numbers")
    func normalisation() {
        var layout = MosaicLayout.preset(.chrome)
        layout.regions[.toolbar] = [.back, .back, .flexibleSpace, .flexibleSpace, .addressField]
        layout.regions[.titleBar] = [.tabStrip, .reload]
        layout.regions[.tabBar] = [.reload]
        layout.appearance.cornerRadius = 500
        layout.appearance.contentInset = .nan
        layout.appearance.accent = "blue"
        layout.sidebarWidth = 5
        let n = layout.normalized()
        #expect(n.components(in: .toolbar) == [.back, .flexibleSpace, .addressField])
        #expect(n.components(in: .tabBar).isEmpty)
        #expect(n.appearance.cornerRadius == 20)
        #expect(n.appearance.contentInset == 0)
        #expect(n.appearance.accent == HexColor.system)
        #expect(n.sidebarWidth == 180)
    }

    @Test("A collapsible vertical sidebar always keeps a sidebar button")
    func sidebarToggleKept() {
        var layout = MosaicLayout.preset(.zenArc)
        layout.remove(.sidebarToggle)
        #expect(layout.normalized().contains(.sidebarToggle))
    }

    @Test("Hex colours parse and format")
    func hexColours() throws {
        let c = try #require(HexColor.components("#FF8000"))
        #expect(c.red == 1 && abs(c.green - 128.0 / 255) < 0.001 && c.blue == 0 && c.alpha == 1)
        let a = try #require(HexColor.components("00000080"))
        #expect(abs(a.alpha - 128.0 / 255) < 0.001)
        #expect(HexColor.components("#12345") == nil)
        #expect(HexColor.components("#GGGGGG") == nil)
        #expect(HexColor.string(red: 1, green: 0.5, blue: 0) == "#FF8000")
        #expect(HexColor.string(red: 0, green: 0, blue: 0, alpha: 0.5) == "#00000080")
    }

    @Test("Gradient stops are sorted, clamped and never fewer than two")
    func gradientStops() {
        let spec = GradientSpec(stops: [GradientStop(color: "#FFFFFF", location: 2),
                                        GradientStop(color: "nope", location: 0.5),
                                        GradientStop(color: "#000000", location: -1)])
        let stops = spec.normalizedStops
        #expect(stops.map(\.color) == ["#000000", "#FFFFFF"])
        #expect(stops.map(\.location) == [0, 1])
        #expect(GradientSpec(stops: []).normalizedStops.count == 2)
    }

    @Test("GX styling is a reversible marker on top of any layout")
    func gxStyling() {
        let gx = MosaicLayout.preset(.opera).withGXStyling()
        #expect(gx.hasGXStyling)
        #expect(gx.appearance.background.kind == .gradient)
        #expect(!MosaicLayout.preset(.opera).hasGXStyling)
    }

    @Test("Moving Chrome's tabs to a sidebar keeps navigation and the address field reachable")
    func chromeToVertical() {
        let layout = MosaicLayout.preset(.chrome).withTabPlacement(.sidebarLeading)
        #expect(layout.tabPlacement == .sidebarLeading)
        #expect(layout.region(of: .addressField) == .sidebarMiddle)
        #expect(layout.components(in: .sidebarTop).contains(.back))
        #expect(layout.contains(.sidebarToggle))
        #expect(layout.components(in: .titleBar).isEmpty)
        #expect(layout.contains(.menu))
        #expect(layout == layout.normalized())
    }

    @Test("Moving Arc's tabs to the top brings navigation into the toolbar")
    func zenToTop() {
        let layout = MosaicLayout.preset(.zenArc).withTabPlacement(.top)
        #expect(layout.region(of: .tabStrip) == .titleBar)
        #expect(layout.region(of: .addressField) == .toolbar)
        #expect(layout.region(of: .back) == .toolbar)
        #expect(layout.components(in: .sidebarMiddle).isEmpty)
    }

    @Test("A horizontal layout never hides the address field in its sidebar")
    func addressFieldLeavesHiddenSidebar() {
        var layout = MosaicLayout.preset(.safari)
        layout.insert(.addressField, in: .sidebarMiddle)
        #expect(layout.normalized().region(of: .addressField) == .toolbar)
    }
}
