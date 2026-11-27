import Foundation

// Gradient Studio model: the free-form ("mesh") gradient ported from Eminence's
// `TintGradient`, its renderer, presets, conversions between gradient kinds, the bounds
// every imported or edited gradient is held to, and the New Tab page's background.
//
// A mesh gradient is a set of colour sources. Each pulls nearby pixels towards its colour
// (inverse-distance weighting), blended in OKLab so mixes stay clean. It is rendered on the
// CPU into a small image that the UI scales up smoothly: a gradient has no fine detail to
// lose, and it stays fast enough to redraw while a source is dragged.
//
// Owner: **canvas**.

/// One colour source of a mesh gradient: where it sits (0…1 of the window), its colour and
/// how far it reaches (1 is normal; larger sources push their colour further).
public struct GradientSource: Codable, Hashable, Identifiable, Sendable {
    public var id: String
    public var x: Double
    public var y: Double
    public var color: String
    public var spread: Double

    public init(id: String = UUID().uuidString, x: Double, y: Double, color: String, spread: Double = 1) {
        self.id = id
        self.x = x
        self.y = y
        self.color = color
        self.spread = spread
    }

    private enum CodingKeys: String, CodingKey { case id, x, y, color, hex, spread }

    /// Forgiving: missing values get defaults; Eminence's `hex` key is accepted for `color`.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = (try? c.decodeIfPresent(String.self, forKey: .id)) ?? UUID().uuidString
        x = (try? c.decodeIfPresent(Double.self, forKey: .x)) ?? 0.5
        y = (try? c.decodeIfPresent(Double.self, forKey: .y)) ?? 0.5
        color = (try? c.decodeIfPresent(String.self, forKey: .color))
            ?? (try? c.decodeIfPresent(String.self, forKey: .hex)) ?? "#808080"
        spread = (try? c.decodeIfPresent(Double.self, forKey: .spread)) ?? 1
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encode(x, forKey: .x)
        try c.encode(y, forKey: .y)
        try c.encode(color, forKey: .color)
        try c.encode(spread, forKey: .spread)
    }

    public var colorValue: ColorValue { ColorValue(hex: color) ?? .fallback }
}

/// The New Tab page's background.
public struct NewTabBackground: Codable, Hashable, Sendable {
    public enum Mode: String, Codable, CaseIterable, Sendable {
        /// The page colour with a whisper of the accent (the default).
        case plain
        /// The same background as the window.
        case window
        /// Its own background (`custom`).
        case custom

        public var title: String {
            switch self {
            case .plain: "Plain"
            case .window: "Same as Window"
            case .custom: "Own Background"
            }
        }
    }

    public var mode: Mode
    public var custom: BackgroundSpec

    public init(mode: Mode = .plain, custom: BackgroundSpec = BackgroundSpec(kind: .gradient, gradient: GradientPresets.all[0].gradient)) {
        self.mode = mode
        self.custom = custom
    }

    public init(from decoder: Decoder) throws {
        let d = NewTabBackground()
        let c = try decoder.container(keyedBy: CodingKeys.self)
        mode = (try? c.decodeIfPresent(Mode.self, forKey: .mode)) ?? d.mode
        custom = (try? c.decodeIfPresent(BackgroundSpec.self, forKey: .custom)) ?? d.custom
    }
}

// MARK: - Bounds

extension GradientSpec {
    public static let maxStops = 16
    public static let maxSources = 32
    public static let sharpnessRange = 1.0...6.0
    public static let spreadRange = 0.3...3.0
    public static let opacityRange = 0.1...1.0

    /// Every number in range, invalid colours dropped, lists capped. Values that are already
    /// valid are left exactly as they are (presets stay unchanged).
    public func clamped() -> GradientSpec {
        var g = self
        func clamp(_ v: Double, _ range: ClosedRange<Double>, _ fallback: Double) -> Double {
            v.isFinite ? min(max(v, range.lowerBound), range.upperBound) : fallback
        }
        g.angle = angle.isFinite ? angle : 135
        g.stops = Array(stops.filter { HexColor.components($0.color) != nil }.prefix(Self.maxStops))
            .map { GradientStop(color: $0.color, location: clamp($0.location, 0...1, 0)) }
        var ids: Set<String> = []
        g.sources = Array(sources.filter { HexColor.components($0.color) != nil }.prefix(Self.maxSources)).map { s in
            var s = s
            s.x = clamp(s.x, 0...1, 0.5)
            s.y = clamp(s.y, 0...1, 0.5)
            s.spread = clamp(s.spread, Self.spreadRange, 1)
            if s.id.isEmpty || ids.contains(s.id) { s.id = UUID().uuidString }
            ids.insert(s.id)
            return s
        }
        g.sharpness = clamp(sharpness, Self.sharpnessRange, 2.2)
        g.centerX = clamp(centerX, 0...1, 0.5)
        g.centerY = clamp(centerY, 0...1, 0.5)
        g.noise = clamp(noise, 0...1, 0)
        g.opacity = clamp(opacity, Self.opacityRange, 1)
        return g
    }

    /// The colours that make up the gradient (for luminance and thumbnails).
    public var colors: [String] {
        kind == .mesh ? meshSources.map(\.color) : normalizedStops.map(\.color)
    }

    /// The mesh sources with valid colours, or two neutral ones when there are none.
    public var meshSources: [GradientSource] {
        let valid = sources.filter { ColorValue(hex: $0.color) != nil }
        return valid.isEmpty
            ? [GradientSource(id: "a", x: 0, y: 0, color: "#808080"), GradientSource(id: "b", x: 1, y: 1, color: "#B0B0B0")]
            : valid
    }
}

extension BackgroundSpec {
    public func clamped() -> BackgroundSpec {
        var b = self
        if HexColor.components(b.color) == nil { b.color = "#F2F2F7" }
        b.gradient = gradient.clamped()
        return b
    }
}

extension MosaicAppearance {
    /// Gradient Studio bounds applied by `MosaicLayout.normalized()`.
    public func clampedBackgrounds() -> MosaicAppearance {
        var a = self
        a.background = background.clamped()
        a.newTabBackground.custom = newTabBackground.custom.clamped()
        // A handful of workspaces at most; keys must look like IDs.
        a.workspaceBackgrounds = Dictionary(uniqueKeysWithValues: workspaceBackgrounds
            .filter { !$0.key.isEmpty && $0.key.count <= 64 }
            .sorted { $0.key < $1.key }
            .prefix(64)
            .map { ($0.key, $0.value.clamped()) })
        return a
    }

    /// The window background for a workspace (its own, or the shared one).
    public func background(forWorkspace id: String?) -> BackgroundSpec {
        id.flatMap { workspaceBackgrounds[$0] } ?? background
    }
}

// MARK: - Mesh rendering

extension GradientSpec {
    /// The mesh colour at a point (x, y in 0…1); `aspect` is width ÷ height, so reach is round.
    public func meshColor(atX x: Double, y: Double, aspect: Double) -> ColorValue {
        let sources = meshSources
        var sum = (0.0, 0.0, 0.0), total = 0.0
        let power = min(max(sharpness.isFinite ? sharpness : 2.2, 0.5), 8)
        for source in sources {
            let d = hypot((x - source.x) * aspect, y - source.y) / max(source.spread, 0.05)
            let weight = 1 / pow(d + 0.0005, power)
            let lab = source.colorValue.oklab
            sum = (sum.0 + weight * lab.l, sum.1 + weight * lab.a, sum.2 + weight * lab.b)
            total += weight
        }
        return ColorValue(oklabL: sum.0 / total, a: sum.1 / total, b: sum.2 / total)
    }

    /// The mesh as opaque RGBA pixels, `width` across with the given aspect (width ÷ height).
    public func renderMesh(width requestedWidth: Int, aspect requestedAspect: Double) -> (pixels: [UInt8], width: Int, height: Int) {
        let width = min(max(requestedWidth, 1), 1024)
        let aspect = requestedAspect.isFinite ? min(max(requestedAspect, 0.1), 10) : 1.6
        let height = min(max(1, Int((Double(width) / aspect).rounded())), 1024)
        let sources = meshSources
        let labs = sources.map(\.colorValue.oklab)
        let points = sources.map { ($0.x, $0.y, 1 / max($0.spread, 0.05)) }
        let power = min(max(sharpness.isFinite ? sharpness : 2.2, 0.5), 8)
        var pixels = [UInt8](repeating: 255, count: width * height * 4)
        for py in 0..<height {
            let y = (Double(py) + 0.5) / Double(height)
            for px in 0..<width {
                let x = (Double(px) + 0.5) / Double(width)
                var l = 0.0, a = 0.0, b = 0.0, total = 0.0
                for i in points.indices {
                    let d = hypot((x - points[i].0) * aspect, y - points[i].1) * points[i].2
                    let weight = 1 / pow(d + 0.0005, power)
                    l += weight * labs[i].l
                    a += weight * labs[i].a
                    b += weight * labs[i].b
                    total += weight
                }
                let rgb = ColorValue(oklabL: l / total, a: a / total, b: b / total)
                let o = (py * width + px) * 4
                pixels[o] = UInt8((rgb.r * 255).rounded())
                pixels[o + 1] = UInt8((rgb.g * 255).rounded())
                pixels[o + 2] = UInt8((rgb.b * 255).rounded())
            }
        }
        return (pixels, width, height)
    }

    /// The colour at `location` (0…1) along the stops, as SwiftUI interpolates them (sRGB).
    public func stopColor(at location: Double) -> ColorValue {
        let stops = normalizedStops
        let t = min(max(location.isFinite ? location : 0, 0), 1)
        guard let first = stops.first, let last = stops.last else { return .fallback }
        if t <= first.location { return ColorValue(hex: first.color) ?? .fallback }
        if t >= last.location { return ColorValue(hex: last.color) ?? .fallback }
        for (a, b) in zip(stops, stops.dropFirst()) where t >= a.location && t <= b.location {
            let ca = ColorValue(hex: a.color) ?? .fallback, cb = ColorValue(hex: b.color) ?? .fallback
            let span = b.location - a.location
            let f = span > 0 ? (t - a.location) / span : 0
            return ColorValue(r: ca.r + (cb.r - ca.r) * f, g: ca.g + (cb.g - ca.g) * f, b: ca.b + (cb.b - ca.b) * f)
        }
        return ColorValue(hex: last.color) ?? .fallback
    }

    /// The overall colour, averaged in OKLab.
    public var averageColor: ColorValue {
        let values = colors.compactMap(ColorValue.init(hex:))
        guard !values.isEmpty else { return .fallback }
        var sum = (0.0, 0.0, 0.0)
        for v in values {
            let lab = v.oklab
            sum = (sum.0 + lab.l, sum.1 + lab.a, sum.2 + lab.b)
        }
        let n = Double(values.count)
        return ColorValue(oklabL: sum.0 / n, a: sum.1 / n, b: sum.2 / n)
    }

    // MARK: Changing kind

    /// The same colours as another kind, so switching kinds in the studio keeps the look:
    /// stops become sources laid along the angle, sources become stops by where they fall
    /// along it.
    public func converted(to newKind: Kind) -> GradientSpec {
        var g = self
        guard newKind != kind else { return g }
        g.kind = newKind
        let radians = angle * .pi / 180
        let (dx, dy) = (cos(radians), sin(radians))
        if newKind == .mesh, kind != .mesh {
            g.sources = normalizedStops.enumerated().map { index, stop in
                let offset = stop.location - 0.5
                return GradientSource(id: "s\(index)-\(UUID().uuidString.prefix(8))",
                                      x: min(max(0.5 + dx * offset, 0), 1), y: min(max(0.5 + dy * offset, 0), 1),
                                      color: stop.color)
            }
        } else if kind == .mesh, newKind != .mesh {
            let projected = meshSources.map { source -> (Double, String) in
                ((source.x - 0.5) * dx + (source.y - 0.5) * dy, source.color)
            }.sorted { $0.0 < $1.0 }
            let lo = projected.first?.0 ?? 0, hi = projected.last?.0 ?? 1
            let span = hi - lo
            g.stops = projected.enumerated().map { index, item in
                let location = span > 0.0001 ? (item.0 - lo) / span
                    : Double(index) / Double(max(projected.count - 1, 1))
                return GradientStop(color: item.1, location: location)
            }
        }
        return g
    }
}

// MARK: - Presets

/// Ready-made gradients for the studio's preset strip: Eminence's free-form designs (the
/// sources already placed) and the shell's soft linear backgrounds.
public enum GradientPresets {
    private static func mesh(_ name: String, _ points: [(Double, Double, String)], sharpness: Double = 2.2) -> (name: String, gradient: GradientSpec) {
        let sources = points.enumerated().map { index, p in
            GradientSource(id: "\(name.lowercased().replacingOccurrences(of: " ", with: "-"))-\(index)", x: p.0, y: p.1, color: p.2)
        }
        return (name, GradientSpec(kind: .mesh, angle: 135, stops: [], sources: sources, sharpness: sharpness))
    }

    private static func linear(_ name: String, _ angle: Double, _ stops: [(String, Double)]) -> (name: String, gradient: GradientSpec) {
        (name, GradientSpec(kind: .linear, angle: angle, stops: stops.map { GradientStop(color: $0.0, location: $0.1) }))
    }

    public static let mesh: [(name: String, gradient: GradientSpec)] = [
        mesh("Sunset", [(0.05, 0.1, "#FF7E5F"), (0.95, 0.15, "#FEB47B"), (0.5, 0.95, "#6A3093")]),
        mesh("Aurora", [(0.1, 0.9, "#00C9A7"), (0.9, 0.1, "#845EC2"), (0.35, 0.3, "#2C73D2"), (0.75, 0.75, "#0081CF")]),
        mesh("Ocean", [(0.5, 0.0, "#2E3192"), (0.5, 1.0, "#1BFFFF")], sharpness: 1.6),
        mesh("Peach", [(0.0, 0.0, "#FFD3A5"), (1.0, 1.0, "#FD6585")], sharpness: 1.6),
        mesh("Four Corners", [(0, 0, "#FF6B6B"), (1, 0, "#FFD93D"), (0, 1, "#4D96FF"), (1, 1, "#6BCB77")]),
        mesh("Radial Glow", [(0.5, 0.5, "#F2994A"), (0, 0, "#1A1A2E"), (1, 0, "#1A1A2E"), (0, 1, "#1A1A2E"), (1, 1, "#1A1A2E")], sharpness: 2.6),
        mesh("Diagonal", [(0.0, 0.0, "#667EEA"), (1.0, 1.0, "#764BA2")], sharpness: 1.6),
        mesh("Duotone", [(0.0, 0.5, "#FC466B"), (1.0, 0.5, "#3F5EFB")], sharpness: 1.8),
        mesh("Midnight", [(0.1, 0.1, "#0F2027"), (0.6, 0.5, "#203A43"), (0.95, 0.95, "#2C5364")]),
        mesh("Pastel", [(0.15, 0.2, "#FBC2EB"), (0.85, 0.15, "#A6C1EE"), (0.5, 0.55, "#FDFCFB"), (0.2, 0.9, "#C2E9FB"), (0.85, 0.85, "#FEE140")]),
        mesh("Mesh", [(0.15, 0.2, "#7F7FD5"), (0.8, 0.1, "#86A8E7"), (0.45, 0.55, "#91EAE4"), (0.1, 0.85, "#F8B195"), (0.9, 0.8, "#C06C84")], sharpness: 2.8),
        mesh("Forest", [(0.0, 0.0, "#134E5E"), (1.0, 0.3, "#71B280"), (0.3, 1.0, "#0B3D2E")]),
    ]

    public static let linear: [(name: String, gradient: GradientSpec)] = [
        linear("Lavender", 125, [("#C9BFF7", 0), ("#E7D6F5", 0.55), ("#BFD3F5", 1)]),
        linear("Dusk", 135, [("#FFB88C", 0), ("#FF8FA3", 0.5), ("#C79BFF", 1)]),
        linear("Sky", 140, [("#A1C4FD", 0), ("#C2E9FB", 1)]),
        linear("Mint", 120, [("#B8F1D3", 0), ("#D9F6C4", 1)]),
        linear("Night", 160, [("#1B1024", 0), ("#0F0E15", 0.6), ("#24101A", 1)]),
        linear("Graphite", 90, [("#3A3D44", 0), ("#24262B", 1)]),
    ]

    public static var all: [(name: String, gradient: GradientSpec)] { mesh + linear }

    /// A preset by name, ignoring case and spaces.
    public static func named(_ name: String) -> GradientSpec? {
        let key = name.lowercased().replacingOccurrences(of: " ", with: "")
        return all.first { $0.name.lowercased().replacingOccurrences(of: " ", with: "") == key }?.gradient
    }
}
