// Regenerates the golden files: what the macOS app writes (Sources/MosaicCore), which the
// Windows port must match byte for byte. Run from the repository root:
//   cp windows/tests/Mosaic.Core.Tests/Shell/Golden/generate.swift /tmp/main.swift   # top-level code must be main.swift
//   swiftc -o /tmp/mosaic-golden Sources/MosaicCore/Shell/MosaicLayout.swift \
//     Sources/MosaicCore/Canvas/Backgrounds.swift Sources/MosaicCore/Canvas/ColorValue.swift \
//     /tmp/main.swift
//   /tmp/mosaic-golden windows/tests/Mosaic.Core.Tests/Shell/Golden
import Foundation

let out = CommandLine.arguments[1]
func write(_ name: String, _ data: Data) { try! data.write(to: URL(fileURLWithPath: out + "/" + name)) }
func text(_ name: String, _ string: String) { write(name, string.data(using: .utf8)!) }

for p in MosaicPresetID.allCases {
    write("preset-\(p.rawValue).json", try! MosaicLayout.preset(p).jsonData())
    write("preset-\(p.rawValue).compact.json", try! MosaicLayout.preset(p).jsonData(pretty: false))
}
write("opera-gx.json", try! MosaicLayout.preset(.opera).withGXStyling().jsonData())

// Must match ShellGoldenTests.CustomChrome().
var c = MosaicLayout.preset(.chrome).withTabPlacement(.sidebarTrailing)
c.appearance.accent = "#12AB34"
c.appearance.cornerRadius = 12.5
c.appearance.contentInset = 0.1
c.appearance.workspaceBackgrounds = ["B": BackgroundSpec(kind: .solid, color: "#101010"), "A": BackgroundSpec(), "a_": BackgroundSpec()]
c.name = "My \"Chrome\" / 1 é\u{1F600}\t\u{1b}"
write("chrome-custom.json", try! c.normalized().jsonData())

write("zen-top.json", try! MosaicLayout.preset(.zenArc).withTabPlacement(.top).jsonData())
write("zen-trailing.json", try! MosaicLayout.preset(.zenArc).withTabPlacement(.sidebarTrailing).jsonData())
write("chrome-vertical.json", try! MosaicLayout.preset(.chrome).withTabPlacement(.sidebarLeading).jsonData())
write("opera-vertical.json", try! MosaicLayout.preset(.opera).withTabPlacement(.sidebarLeading).jsonData())
write("opera-vertical-top.json", try! MosaicLayout.preset(.opera).withTabPlacement(.sidebarLeading).withTabPlacement(.top).jsonData())

// Forgiving decoding: <name>.input.json decoded with MosaicLayout.from(json:) gives <name>.json.
let inputs: [String: String] = [
    "messy": ##"{"preset":"opera","regions":{"toolbar":["back","bogus","addressField","back"],"nowhere":["menu"],"rail":["addressField","home"]},"appearance":{"cornerRadius":99,"accent":"blue","density":"huge","background":{"kind":"gradient","gradient":{"stops":[{"color":"#FF0000","location":2},{"color":"nope","location":0.5}],"sharpness":40,"sources":[{"x":7,"color":"#00FF00","id":"s"},{"hex":"#0000FF","id":"s"}]}}},"sidebarWidth":10}"##,
    "empty": "{}",
    "wrongtypes": ##"{"preset":"zenArc","name":5,"regions":{"toolbar":["back",3]},"bookmarksBar":"yes","sidebarWidth":"wide","appearance":{"glow":1,"workspaceBackgrounds":{"x":{"kind":"solid","color":"#123456"},"y":3}}}"##,
    "future": ##"{"preset":"chrome","schemaVersion":7,"tabPlacement":"floating","tabStyle":"edge","regions":{"titleBar":["tabStrip","newTab","copilot"],"toolbar":["addressField"],"trailingRail":["settings"]},"appearance":{"material":"metal","colorScheme":"dark"},"extra":{"a":1}}"##,
    "nullregions": ##"{"preset":"opera","regions":null,"appearance":null}"##,
]
for (name, json) in inputs {
    text("\(name).input.json", json)
    write("\(name).json", try! MosaicLayout.from(json: json.data(using: .utf8)!).jsonData())
}

// The store file (MosaicStore.save()).
struct SavedState: Codable { var active: MosaicPresetID; var layouts: [String: MosaicLayout] }
let s = SavedState(active: .opera, layouts: Dictionary(uniqueKeysWithValues: MosaicPresetID.allCases.map { ($0.rawValue, MosaicLayout.preset($0)) }))
let e = JSONEncoder(); e.outputFormatting = [.prettyPrinted, .sortedKeys]
write("mosaics.json", try! e.encode(s))
let safariActive = SavedState(active: .safari, layouts: ["safari": MosaicLayout.preset(.safari).withTabPlacement(.sidebarLeading)])
write("mosaics-safari.json", try! e.encode(safariActive))
