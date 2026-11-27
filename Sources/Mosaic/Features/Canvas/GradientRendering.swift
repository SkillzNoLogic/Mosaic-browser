import AppKit
import MosaicCore
import SwiftUI

// Drawing Gradient Studio backgrounds: the free-form (mesh) gradient as a small cached image
// scaled up smoothly, film grain as one cached tile, and the full background (strength over
// the system window colour, light/dark adaptation unless Raw). Nothing here animates, so an
// idle window does no drawing work; a new image is computed only when the gradient or the
// window's shape changes. Owner: **canvas**.

/// Rendered mesh images, keyed by gradient, width and shape, so redrawing a window (or a
/// thumbnail) reuses the pixels instead of recomputing them.
@MainActor
enum MeshImageCache {
    private struct Key: Hashable {
        let gradient: GradientSpec
        let width: Int
        /// Aspect to 1/50: a window resize recomputes only when its shape really changes.
        let aspect: Int
    }

    private static var images: [Key: CGImage] = [:]
    /// For `dump`: how many images were computed (it must stay put while idle).
    private(set) static var renderCount = 0

    static func image(for gradient: GradientSpec, width: Int, aspect: Double) -> CGImage? {
        // Only what affects the pixels is part of the key.
        var shape = gradient
        shape.noise = 0
        shape.opacity = 1
        shape.raw = false
        shape.angle = 0
        shape.stops = []
        shape.centerX = 0.5
        shape.centerY = 0.5
        let key = Key(gradient: shape, width: width, aspect: Int(((aspect.isFinite ? aspect : 1.6) * 50).rounded()))
        if let image = images[key] { return image }
        let rendered = shape.renderMesh(width: width, aspect: Double(max(key.aspect, 1)) / 50)
        guard let provider = CGDataProvider(data: Data(rendered.pixels) as CFData),
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let image = CGImage(width: rendered.width, height: rendered.height, bitsPerComponent: 8, bitsPerPixel: 32,
                                  bytesPerRow: rendered.width * 4, space: space,
                                  bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                                  provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
        else { return nil }
        renderCount += 1
        if images.count > 48 { images.removeAll() }  // a drag makes many one-off frames
        images[key] = image
        return image
    }
}

/// Draws a mesh gradient filling its frame.
struct MeshGradientImage: View {
    let spec: GradientSpec
    /// Pixels across; scaled up with smooth interpolation (a gradient has no fine detail).
    var resolution = 160

    var body: some View {
        GeometryReader { geo in
            let aspect = geo.size.height > 0 ? geo.size.width / geo.size.height : 1.6
            if let image = MeshImageCache.image(for: spec, width: resolution, aspect: aspect) {
                Image(decorative: image, scale: 1)
                    .resizable()
                    .interpolation(.high)
                    .frame(width: geo.size.width, height: geo.size.height)
            }
        }
    }
}

/// Film grain: one 128-pixel noise tile made once, repeated over the area.
struct GrainOverlay: View {
    let amount: Double

    @MainActor private static let tile: CGImage? = {
        let side = 128
        var pixels = [UInt8](repeating: 0, count: side * side * 4)
        var seed: UInt64 = 0x9E37_79B9_7F4A_7C15
        for i in 0..<(side * side) {
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            let v = UInt8(truncatingIfNeeded: seed >> 56)
            pixels[i * 4] = v
            pixels[i * 4 + 1] = v
            pixels[i * 4 + 2] = v
            pixels[i * 4 + 3] = 255
        }
        guard let provider = CGDataProvider(data: Data(pixels) as CFData),
              let space = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
        return CGImage(width: side, height: side, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: side * 4,
                       space: space, bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }()

    var body: some View {
        if amount > 0.001, let tile = Self.tile {
            Image(decorative: tile, scale: 2)
                .resizable(resizingMode: .tile)
                .blendMode(.overlay)
                .opacity(min(amount, 1) * 0.55)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
    }
}

/// A gradient exactly as specified (any kind), without strength, grain or adaptation.
struct CanvasGradientFill: View {
    let spec: GradientSpec

    var body: some View {
        let stops = spec.normalizedStops.map { Gradient.Stop(color: Color(hex: $0.color), location: $0.location) }
        let gradient = Gradient(stops: stops)
        let center = UnitPoint(x: spec.centerX, y: spec.centerY)
        switch spec.kind {
        case .linear:
            let radians = spec.angle * .pi / 180
            let dx = cos(radians) / 2, dy = sin(radians) / 2
            LinearGradient(gradient: gradient, startPoint: UnitPoint(x: 0.5 - dx, y: 0.5 - dy),
                           endPoint: UnitPoint(x: 0.5 + dx, y: 0.5 + dy))
        case .radial:
            GeometryReader { proxy in
                RadialGradient(gradient: gradient, center: center, startRadius: 0,
                               endRadius: max(proxy.size.width, proxy.size.height) * 0.75)
            }
        case .angular:
            AngularGradient(gradient: gradient, center: center, angle: .degrees(spec.angle))
        case .mesh:
            MeshGradientImage(spec: spec)
        }
    }
}

/// A gradient background as the window shows it: its strength over the system window
/// colour, grain, and (unless Raw) dimmed in dark mode or lifted in light mode so the
/// chrome stays legible.
struct CanvasGradientBackground: View {
    @Environment(\.colorScheme) private var colorScheme
    let spec: GradientSpec

    var body: some View {
        ZStack {
            if spec.opacity < 0.999 { Color(nsColor: .windowBackgroundColor) }
            CanvasGradientFill(spec: spec).opacity(min(max(spec.opacity, 0.1), 1))
            if !spec.raw { adaptation }
            GrainOverlay(amount: spec.noise)
        }
    }

    @ViewBuilder private var adaptation: some View {
        let luminance = BackgroundFill.luminance(of: spec.colors)
        if colorScheme == .dark, luminance > 0.55 {
            Color.black.opacity(0.66)
        } else if colorScheme == .light, luminance < 0.35 {
            Color.white.opacity(0.62)
        }
    }
}
