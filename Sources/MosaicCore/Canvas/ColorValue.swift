import Foundation

// Colours for the Gradient Studio's colour chart, ported from Eminence's `ColorChartPicker`:
// sRGB components with lossless conversions to and from every encoding the chart shows
// (HEX, RGB, HSB, HSL, CMYK, CIELAB, OKLCH) plus OKLab, which the free-form gradient blends
// in. Pure Foundation, so it is unit-tested and shared by the renderer and the UI.
//
// Owner: **canvas**.

/// A colour as sRGB components in 0…1.
public struct ColorValue: Equatable, Hashable, Sendable {
    public var r: Double, g: Double, b: Double

    public static let fallback = ColorValue(r: 0.35, g: 0.55, b: 0.95)

    public init(r: Double, g: Double, b: Double) {
        (self.r, self.g, self.b) = (Self.unit(r), Self.unit(g), Self.unit(b))
    }

    private static func unit(_ v: Double) -> Double { v.isFinite ? min(max(v, 0), 1) : 0 }

    // MARK: Hex

    /// Accepts #RGB, #RRGGBB and #RRGGBBAA (alpha ignored), with or without #.
    public init?(hex: String) {
        var text = hex.trimmingCharacters(in: .whitespaces).uppercased()
        if text.hasPrefix("#") { text.removeFirst() }
        if text.count == 3 { text = text.map { "\($0)\($0)" }.joined() }
        if text.count == 8 { text = String(text.prefix(6)) }
        guard text.count == 6, text.allSatisfy(\.isHexDigit), let value = UInt32(text, radix: 16) else { return nil }
        self.init(r: Double((value >> 16) & 0xFF) / 255, g: Double((value >> 8) & 0xFF) / 255, b: Double(value & 0xFF) / 255)
    }

    public var hex: String {
        String(format: "#%02X%02X%02X", Int((r * 255).rounded()), Int((g * 255).rounded()), Int((b * 255).rounded()))
    }

    /// Relative luminance (0 black … 1 white), for picking legible text.
    public var luminance: Double { 0.2126 * r + 0.7152 * g + 0.0722 * b }

    // MARK: HSB (HSV) and HSL; hue in degrees, the rest in percent

    public var hsb: (h: Double, s: Double, b: Double) {
        let maxV = max(r, g, b), minV = min(r, g, b), delta = maxV - minV
        return (hue(maxV: maxV, delta: delta), maxV == 0 ? 0 : delta / maxV * 100, maxV * 100)
    }

    public init(h: Double, s: Double, brightness: Double) {
        let (s, v) = (s / 100, brightness / 100)
        let c = v * s
        let (r, g, b) = Self.hueComponents(h, chroma: c)
        let m = v - c
        self.init(r: r + m, g: g + m, b: b + m)
    }

    public var hsl: (h: Double, s: Double, l: Double) {
        let maxV = max(r, g, b), minV = min(r, g, b), delta = maxV - minV
        let l = (maxV + minV) / 2
        let s = delta == 0 ? 0 : delta / (1 - abs(2 * l - 1))
        return (hue(maxV: maxV, delta: delta), s * 100, l * 100)
    }

    public init(h: Double, s: Double, lightness: Double) {
        let (s, l) = (s / 100, lightness / 100)
        let c = (1 - abs(2 * l - 1)) * s
        let (r, g, b) = Self.hueComponents(h, chroma: c)
        let m = l - c / 2
        self.init(r: r + m, g: g + m, b: b + m)
    }

    private func hue(maxV: Double, delta: Double) -> Double {
        guard delta > 0 else { return 0 }
        var h: Double
        if maxV == r { h = ((g - b) / delta).truncatingRemainder(dividingBy: 6) }
        else if maxV == g { h = (b - r) / delta + 2 }
        else { h = (r - g) / delta + 4 }
        h *= 60
        return h < 0 ? h + 360 : h
    }

    private static func hueComponents(_ h: Double, chroma c: Double) -> (Double, Double, Double) {
        let hp = (h.truncatingRemainder(dividingBy: 360) + 360).truncatingRemainder(dividingBy: 360) / 60
        let x = c * (1 - abs(hp.truncatingRemainder(dividingBy: 2) - 1))
        switch hp {
        case ..<1: return (c, x, 0)
        case ..<2: return (x, c, 0)
        case ..<3: return (0, c, x)
        case ..<4: return (0, x, c)
        case ..<5: return (x, 0, c)
        default: return (c, 0, x)
        }
    }

    // MARK: CMYK (naive, percent)

    public var cmyk: (c: Double, m: Double, y: Double, k: Double) {
        let k = 1 - max(r, g, b)
        guard k < 1 else { return (0, 0, 0, 100) }
        return ((1 - r - k) / (1 - k) * 100, (1 - g - k) / (1 - k) * 100, (1 - b - k) / (1 - k) * 100, k * 100)
    }

    public init(c: Double, m: Double, y: Double, k: Double) {
        let k = k / 100
        self.init(r: (1 - c / 100) * (1 - k), g: (1 - m / 100) * (1 - k), b: (1 - y / 100) * (1 - k))
    }

    // MARK: CIELAB (D65) and OKLCH, through linear sRGB

    private static func linear(_ v: Double) -> Double { v <= 0.04045 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4) }
    private static func gamma(_ v: Double) -> Double { v <= 0.0031308 ? v * 12.92 : 1.055 * pow(max(v, 0), 1 / 2.4) - 0.055 }

    private var linearRGB: (Double, Double, Double) { (Self.linear(r), Self.linear(g), Self.linear(b)) }

    private init(linearR: Double, g: Double, b: Double) {
        self.init(r: Self.gamma(linearR), g: Self.gamma(g), b: Self.gamma(b))
    }

    public var lab: (l: Double, a: Double, b: Double) {
        let (lr, lg, lb) = linearRGB
        let x = (0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb) / 0.95047
        let y = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb
        let z = (0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb) / 1.08883
        func f(_ t: Double) -> Double { t > 216.0 / 24389 ? cbrt(t) : (24389.0 / 27 * t + 16) / 116 }
        let (fx, fy, fz) = (f(x), f(y), f(z))
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz))
    }

    public init(l: Double, a: Double, labB: Double) {
        let fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - labB / 200
        func inv(_ t: Double) -> Double { t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) / (24389.0 / 27) }
        let (x, y, z) = (inv(fx) * 0.95047, inv(fy), inv(fz) * 1.08883)
        self.init(linearR: 3.2404542 * x - 1.5371385 * y - 0.4985314 * z,
                  g: -0.9692660 * x + 1.8760108 * y + 0.0415560 * z,
                  b: 0.0556434 * x - 0.2040259 * y + 1.0572252 * z)
    }

    /// OKLCH: lightness in percent, chroma (0…~0.4), hue in degrees.
    public var oklch: (l: Double, c: Double, h: Double) {
        let v = oklab
        var h = atan2(v.b, v.a) * 180 / .pi
        if h < 0 { h += 360 }
        return (v.l * 100, sqrt(v.a * v.a + v.b * v.b), h)
    }

    public init(oklchL: Double, c: Double, h: Double) {
        self.init(oklabL: oklchL / 100, a: c * cos(h * .pi / 180), b: c * sin(h * .pi / 180))
    }

    /// OKLab with L in 0…1 (the space the free-form gradient blends in, so mixes stay clean).
    public var oklab: (l: Double, a: Double, b: Double) {
        let (lr, lg, lb) = linearRGB
        let l = cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb)
        let m = cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb)
        let s = cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb)
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s)
    }

    public init(oklabL L: Double, a A: Double, b B: Double) {
        let l = pow(L + 0.3963377774 * A + 0.2158037573 * B, 3)
        let m = pow(L - 0.1055613458 * A - 0.0638541728 * B, 3)
        let s = pow(L - 0.0894841775 * A - 1.2914855480 * B, 3)
        self.init(linearR: 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                  g: -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                  b: -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s)
    }
}

/// The encodings the colour chart can show and edit.
public enum ColorEncoding: String, CaseIterable, Identifiable, Sendable {
    case hex = "HEX", rgb = "RGB", hsb = "HSB", hsl = "HSL", cmyk = "CMYK", lab = "LAB", oklch = "OKLCH"
    public var id: String { rawValue }

    /// Component names, value ranges and display precision.
    public var components: [(label: String, range: ClosedRange<Double>, digits: Int)] {
        switch self {
        case .hex: []
        case .rgb: [("R", 0...255, 0), ("G", 0...255, 0), ("B", 0...255, 0)]
        case .hsb: [("H", 0...360, 0), ("S", 0...100, 0), ("B", 0...100, 0)]
        case .hsl: [("H", 0...360, 0), ("S", 0...100, 0), ("L", 0...100, 0)]
        case .cmyk: [("C", 0...100, 0), ("M", 0...100, 0), ("Y", 0...100, 0), ("K", 0...100, 0)]
        case .lab: [("L", 0...100, 1), ("a", -128...127, 1), ("b", -128...127, 1)]
        case .oklch: [("L", 0...100, 1), ("C", 0...0.4, 3), ("H", 0...360, 1)]
        }
    }

    public func values(of color: ColorValue) -> [Double] {
        switch self {
        case .hex: []
        case .rgb: [color.r * 255, color.g * 255, color.b * 255]
        case .hsb: { let v = color.hsb; return [v.h, v.s, v.b] }()
        case .hsl: { let v = color.hsl; return [v.h, v.s, v.l] }()
        case .cmyk: { let v = color.cmyk; return [v.c, v.m, v.y, v.k] }()
        case .lab: { let v = color.lab; return [v.l, v.a, v.b] }()
        case .oklch: { let v = color.oklch; return [v.l, v.c, v.h] }()
        }
    }

    /// The colour for component values (missing values count as 0).
    public func color(from values: [Double]) -> ColorValue {
        let v = values + Array(repeating: 0, count: max(0, 4 - values.count))
        switch self {
        case .hex: return .fallback
        case .rgb: return ColorValue(r: v[0] / 255, g: v[1] / 255, b: v[2] / 255)
        case .hsb: return ColorValue(h: v[0], s: v[1], brightness: v[2])
        case .hsl: return ColorValue(h: v[0], s: v[1], lightness: v[2])
        case .cmyk: return ColorValue(c: v[0], m: v[1], y: v[2], k: v[3])
        case .lab: return ColorValue(l: v[0], a: v[1], labB: v[2])
        case .oklch: return ColorValue(oklchL: v[0], c: v[1], h: v[2])
        }
    }

    /// Typed text for each component, clamped to its range; unreadable entries keep `current`'s value.
    public func color(parsing fields: [String], current: ColorValue) -> ColorValue {
        let comps = components
        let existing = values(of: current)
        let numbers = comps.indices.map { i -> Double in
            guard i < fields.count else { return existing[i] }
            let text = fields[i].replacingOccurrences(of: ",", with: ".").filter { "0123456789.-".contains($0) }
            guard let number = Double(text), number.isFinite else { return existing[i] }
            return min(max(number, comps[i].range.lowerBound), comps[i].range.upperBound)
        }
        return color(from: numbers)
    }

    /// The colour written the way designers and CSS write it.
    public func string(for color: ColorValue) -> String {
        let v = values(of: color)
        func f(_ x: Double, _ d: Int) -> String { String(format: "%.\(d)f", x) }
        switch self {
        case .hex: return color.hex
        case .rgb: return "rgb(\(f(v[0], 0)), \(f(v[1], 0)), \(f(v[2], 0)))"
        case .hsb: return "hsb(\(f(v[0], 0))°, \(f(v[1], 0))%, \(f(v[2], 0))%)"
        case .hsl: return "hsl(\(f(v[0], 0)) \(f(v[1], 0))% \(f(v[2], 0))%)"
        case .cmyk: return "cmyk(\(f(v[0], 0))%, \(f(v[1], 0))%, \(f(v[2], 0))%, \(f(v[3], 0))%)"
        case .lab: return "lab(\(f(v[0], 1))% \(f(v[1], 1)) \(f(v[2], 1)))"
        case .oklch: return "oklch(\(f(v[0], 1))% \(f(v[1], 3)) \(f(v[2], 1)))"
        }
    }
}
