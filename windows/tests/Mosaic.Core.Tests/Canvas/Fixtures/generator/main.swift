import Foundation
import MosaicCore

// Writes fixtures produced by the macOS MosaicCore, for the Windows port's parity tests.
let out = URL(fileURLWithPath: CommandLine.arguments[1])
try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
let fixed = Date(timeIntervalSince1970: 1_790_000_000)  // 2026-09-21T…Z

func write(_ name: String, _ data: Data) throws { try data.write(to: out.appendingPathComponent(name)) }
func json(_ object: Any) throws -> Data {
    try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
}
func layoutObject(_ layout: MosaicLayout) -> Any {
    try! JSONSerialization.jsonObject(with: layout.jsonData(pretty: false))
}

// 1. Exports of every preset, and a customised one.
for preset in MosaicPresetID.allCases {
    try write("macos-export-\(preset.rawValue).json", LayoutTransfer.export(name: preset.title, layout: .preset(preset), now: fixed))
}
var custom = MosaicLayout.preset(.opera).withGXStyling()
var mesh = GradientPresets.named("Aurora")!
mesh.noise = 0.35
mesh.opacity = 0.8
mesh.raw = true
mesh.sharpness = 3.1
custom.appearance.background = BackgroundSpec(kind: .gradient, color: "#121019", gradient: mesh)
custom.appearance.newTabBackground = NewTabBackground(mode: .custom, custom: BackgroundSpec(kind: .gradient, gradient: GradientSpec(
    kind: .radial, angle: 30, stops: [GradientStop(color: "#112233", location: 0), GradientStop(color: "#AABBCC", location: 1)],
    centerX: 0.3, centerY: 0.7)))
custom.appearance.workspaceBackgrounds = [
    "6F1C2B0E-8B7A-4C61-9A43-1D2E3F405162": BackgroundSpec(kind: .gradient, gradient: GradientPresets.named("Ocean")!),
    "A0B1C2D3-0000-4000-8000-000000000001": BackgroundSpec(kind: .solid, color: "#336699"),
]
custom.regions[.toolbar]?.insert(.flexibleSpace, at: 3)
custom = LayoutCanvasRules.add(.settings, to: .toolbar, in: custom).layout!
try write("macos-export-custom.json", LayoutTransfer.export(name: "GX Aurora", layout: custom, now: fixed))
try write("macos-bare-zenArc.json", MosaicLayout.preset(.zenArc).jsonData())

// 2. A configuration library.
let library = ConfigurationLibrary(configurations: [
    LayoutConfiguration(id: "11111111-2222-4333-8444-555555555555", name: "Work", created: fixed, modified: fixed.addingTimeInterval(3600),
                        layout: { var l = LayoutCanvasRules.usable(.preset(.chrome)); l.name = "Work"; return l }()),
    LayoutConfiguration(id: "66666666-7777-4888-9999-AAAAAAAAAAAA", name: "GX Aurora", created: fixed, modified: fixed,
                        layout: { var l = LayoutCanvasRules.usable(custom); l.name = "GX Aurora"; return l }()),
    LayoutConfiguration(id: "BBBBBBBB-CCCC-4DDD-8EEE-FFFFFFFFFFFF", name: "Calm", created: fixed, modified: fixed,
                        layout: { var l = LayoutCanvasRules.usable(.preset(.zenArc)); l.name = "Calm"; return l }()),
], activeID: "66666666-7777-4888-9999-AAAAAAAAAAAA")
let libURL = out.appendingPathComponent("macos-configurations.json")
try library.write(to: libURL)

// 3. Expectations: colours, gradients, rules.
var colors: [[String: Any]] = []
for hex in ["#FF7E5F", "#2C73D2", "#000000", "#FFFFFF", "#808080", "#13A35B", "#FDFCFB", "#845EC2"] {
    let c = ColorValue(hex: hex)!
    var entry: [String: Any] = ["hex": hex]
    for e in ColorEncoding.allCases where e != .hex {
        entry[e.rawValue] = e.values(of: c)
        entry[e.rawValue + "-string"] = e.string(for: c)
    }
    let ok = c.oklab
    entry["oklab"] = [ok.l, ok.a, ok.b]
    colors.append(entry)
}

var gradients: [[String: Any]] = []
for preset in GradientPresets.all {
    let g = preset.gradient
    var entry: [String: Any] = ["name": preset.name, "average": g.averageColor.hex]
    if g.kind == .mesh {
        let image = g.renderMesh(width: 24, aspect: 1.6)
        entry["meshWidth"] = image.width
        entry["meshHeight"] = image.height
        entry["meshPixels"] = image.pixels.enumerated().filter { $0.offset % 4 != 3 }.map { Int($0.element) }
        entry["meshAt"] = [[0.1, 0.2], [0.5, 0.5], [0.9, 0.7]].map { g.meshColor(atX: $0[0], y: $0[1], aspect: 1.6).hex }
        entry["asLinear"] = try JSONSerialization.jsonObject(with: JSONEncoder().encode(g.converted(to: .linear).stops))
    } else {
        entry["stopsAt"] = [0, 0.25, 0.5, 0.8, 1].map { g.stopColor(at: $0).hex }
        let m = g.converted(to: .mesh)
        entry["asMesh"] = m.sources.map { ["x": $0.x, "y": $0.y, "color": $0.color] as [String: Any] }
    }
    gradients.append(entry)
}

func describe(_ edit: CanvasEdit) -> [String: Any] {
    switch edit {
    case .changed(let layout, let selection):
        let full = layoutObject(layout) as! [String: Any]
        var d: [String: Any] = ["regions": full["regions"]!]
        if let selection { d["selection"] = ["region": selection.region.rawValue, "index": selection.index] }
        return d
    case .rejected(let message):
        return ["rejected": message]
    }
}

var rules: [[String: Any]] = []
var startLayouts: [(String, MosaicLayout)] = MosaicPresetID.allCases.map { ($0.rawValue, MosaicLayout.preset($0)) }
var chromeVertical = MosaicLayout.preset(.chrome).withTabPlacement(.sidebarTrailing)
startLayouts.append(("chrome-trailing", chromeVertical))
var zenTop = MosaicLayout.preset(.zenArc).withTabPlacement(.top)
startLayouts.append(("zenArc-top", zenTop))
var zenAuto = MosaicLayout.preset(.zenArc)
zenAuto.sidebar = .autoHide
startLayouts.append(("zenArc-autoHide", zenAuto))
for (name, layout) in startLayouts {
    var entry: [String: Any] = ["name": name, "layout": layoutObject(layout)]
    entry["palette"] = LayoutCanvasRules.palette(layout).map(\.rawValue)
    entry["usable"] = layoutObject(LayoutCanvasRules.usable(layout))
    entry["hasSettingsAccess"] = LayoutCanvasRules.hasSettingsAccess(layout)
    var moves: [[String: Any]] = []
    var removes: [[String: Any]] = []
    var keys: [[String: Any]] = []
    for region in LayoutCanvasRules.regionOrder {
        for index in layout.components(in: region).indices {
            let slot = CanvasSlot(region, index)
            for target in LayoutCanvasRules.regionOrder {
                for before in Set([0, 1, layout.components(in: target).count]) {
                    var d = describe(LayoutCanvasRules.move(slot, to: target, before: before, in: layout))
                    d["from"] = ["region": region.rawValue, "index": index]
                    d["to"] = target.rawValue
                    d["before"] = before
                    moves.append(d)
                }
            }
            var r = describe(LayoutCanvasRules.remove(slot, in: layout))
            r["from"] = ["region": region.rawValue, "index": index]
            removes.append(r)
            for (kind, edit) in [("left", LayoutCanvasRules.shift(slot, by: -1, in: layout)),
                                 ("right", LayoutCanvasRules.shift(slot, by: 1, in: layout)),
                                 ("up", LayoutCanvasRules.moveToAdjacentRegion(slot, direction: -1, in: layout)),
                                 ("down", LayoutCanvasRules.moveToAdjacentRegion(slot, direction: 1, in: layout))] {
                var k = describe(edit)
                k["from"] = ["region": region.rawValue, "index": index]
                k["key"] = kind
                keys.append(k)
            }
        }
    }
    var adds: [[String: Any]] = []
    for component in ShellComponent.allCases {
        for target in LayoutCanvasRules.regionOrder {
            var d = describe(LayoutCanvasRules.add(component, to: target, before: nil, in: layout))
            d["component"] = component.rawValue
            d["to"] = target.rawValue
            adds.append(d)
        }
        entry["suggested-\(component.rawValue)"] = LayoutCanvasRules.suggestedRegion(for: component, in: layout)?.rawValue ?? NSNull()
    }
    var placements: [String: Any] = [:]
    for p in TabPlacement.allCases { placements[p.rawValue] = layoutObject(layout.withTabPlacement(p)) }
    entry["moves"] = moves
    entry["removes"] = removes
    entry["keys"] = keys
    entry["adds"] = adds
    entry["placements"] = placements
    rules.append(entry)
}

// Imports of messy files.
let messy: [String: String] = [
    "repairs": #"""
    {"format":"dev.mosaic.layout","version":99,"name":"Odd\u0007 one","layout":{
      "preset":"chrome","tabPlacement":"top","sidebarWidth":5000,
      "regions":{"titleBar":["tabStrip","teleporter","newTab"],"toolbar":["back","back","addressField",42],
                 "basement":["home"],"rail":["addressField"]},
      "appearance":{"cornerRadius":-4,"accent":"blue","background":{"kind":"gradient","gradient":{"kind":"mesh","sharpness":50,"sources":[{"x":9,"y":0,"color":"#FF0000"}]}}}}}
    """#,
    "weird": #"{"preset":7,"regions":"none","tabPlacement":["top"],"appearance":[]}"#,
    "edge": #"{"preset":"edge","regions":{"toolbar":["back","addressField","menu"]},"tabPlacement":"top"}"#,
    "foreign": #"{"format":"other.app","version":1,"layout":{"preset":"zenArc","tabPlacement":"top","sidebar":"autoHide","regions":{"sidebarTop":["sidebarToggle","addressField"],"toolbar":["back"]}}}"#,
    "notALayout": #"{"recipe":"soup"}"#,
    "notJSON": #"{"preset": "#,
    "array": "[1,2,3]",
]
var imports: [String: Any] = [:]
for (name, text) in messy {
    switch LayoutTransfer.importLayout(Data(text.utf8)) {
    case .success(let report):
        imports[name] = ["input": text, "name": report.name, "messages": report.messages, "layout": layoutObject(report.layout)]
    case .failure(let error):
        imports[name] = ["input": text, "error": "\(error)", "description": error.errorDescription ?? ""]
    }
}

try write("macos-expectations.json", JSONSerialization.data(withJSONObject: ["colors": colors, "gradients": gradients, "rules": rules, "imports": imports], options: [.sortedKeys]))
print("ok")
