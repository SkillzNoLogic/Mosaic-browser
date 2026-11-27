// Renders Resources/AppIcon.icns: Mosaic's four-tile mark in the two engine colours.
//
//   swift Scripts/make-icon.swift && Scripts/build.sh
import AppKit

let root = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent().deletingLastPathComponent()
let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("Mosaic-\(UUID().uuidString).iconset")
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: iconset) }

let gecko = NSColor(srgbRed: 1.0, green: 0.45, blue: 0.10, alpha: 1)
let chromium = NSColor(srgbRed: 0.20, green: 0.52, blue: 0.96, alpha: 1)

func render(pixels: Int) -> Data {
    let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8, samplesPerPixel: 4,
        hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
    let size = CGFloat(pixels)

    // macOS icon grid: the artwork sits inside ~80% of the canvas.
    let plate = NSRect(x: size * 0.1, y: size * 0.1, width: size * 0.8, height: size * 0.8)
    let platePath = NSBezierPath(roundedRect: plate, xRadius: plate.width * 0.225, yRadius: plate.width * 0.225)
    NSGradient(colors: [NSColor(white: 0.17, alpha: 1), NSColor(white: 0.09, alpha: 1)])!
        .draw(in: platePath, angle: -90)

    let inset = plate.width * 0.2
    let area = plate.insetBy(dx: inset, dy: inset)
    let gap = area.width * 0.09
    let tile = (area.width - gap) / 2
    let radius = tile * 0.26
    let tiles: [(CGFloat, CGFloat, NSColor)] = [
        (0, 1, gecko), (1, 1, chromium.withAlphaComponent(0.55)),
        (0, 0, gecko.withAlphaComponent(0.55)), (1, 0, chromium),
    ]
    for (column, row, colour) in tiles {
        let rect = NSRect(
            x: area.minX + column * (tile + gap), y: area.minY + row * (tile + gap), width: tile, height: tile)
        colour.setFill()
        NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
    }
    NSGraphicsContext.restoreGraphicsState()
    return bitmap.representation(using: .png, properties: [:])!
}

for points in [16, 32, 128, 256, 512] {
    try render(pixels: points).write(to: iconset.appendingPathComponent("icon_\(points)x\(points).png"))
    try render(pixels: points * 2).write(to: iconset.appendingPathComponent("icon_\(points)x\(points)@2x.png"))
}

let output = root.appendingPathComponent("Resources/AppIcon.icns")
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", output.path]
try iconutil.run()
iconutil.waitUntilExit()
print(iconutil.terminationStatus == 0 ? "Wrote \(output.path)" : "iconutil failed")
