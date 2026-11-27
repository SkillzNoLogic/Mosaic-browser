import AppKit
import Combine
import MosaicCore

/// Site icons for Speed Dial tiles.
///
/// Privacy: an icon is fetched only for a site already on the user's Speed Dial, directly
/// from that site (never through a third-party icon service), with no cookies, no cache
/// and no referrer, and kept in the profile (`Productivity/Favicons/<host>.png`) for 30
/// days. Speed Dial's "Show Site Icons" switch turns fetching off; tiles then show letters.
@MainActor
final class FaviconCache: ObservableObject {
    @Published private(set) var images: [String: NSImage] = [:]

    private let directory: URL
    private var inFlight: Set<String> = []
    /// Hosts that had no usable icon, with when we gave up (retried after a day).
    private var failures: [String: Date] = [:]
    private static let maxAge: TimeInterval = 30 * 24 * 3600
    private static let retryAfter: TimeInterval = 24 * 3600

    private static let session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldSetCookies = false
        configuration.httpCookieAcceptPolicy = .never
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 8
        configuration.timeoutIntervalForResource = 15
        configuration.httpAdditionalHeaders = ["User-Agent": "Mozilla/5.0 (Macintosh) Mosaic"]
        return URLSession(configuration: configuration)
    }()

    init(directory: URL) {
        self.directory = directory
    }

    /// The icon for `url`'s site if known; when `fetch` is set, starts getting it.
    func image(for url: String, fetch: Bool) -> NSImage? {
        guard let host = SpeedDial.host(of: url) else { return nil }
        if let image = images[host] { return image }
        let file = fileURL(for: host)
        if let attributes = try? FileManager.default.attributesOfItem(atPath: file.path),
           let modified = attributes[.modificationDate] as? Date,
           Date().timeIntervalSince(modified) < Self.maxAge,
           let image = NSImage(contentsOf: file) {
            // Published from outside the view update that asked.
            DispatchQueue.main.async { self.images[host] = image }
            return image
        }
        if fetch { start(host: host, pageURL: url) }
        return nil
    }

    /// Forgets every cached icon (files included).
    func removeAll() {
        images.removeAll()
        failures.removeAll()
        try? FileManager.default.removeItem(at: directory)
    }

    private func fileURL(for host: String) -> URL {
        let safe = host.unicodeScalars.map { CharacterSet.alphanumerics.contains($0) || $0 == "." || $0 == "-" ? String($0) : "_" }.joined()
        return directory.appendingPathComponent("\(safe).png")
    }

    private func start(host: String, pageURL: String) {
        guard !inFlight.contains(host) else { return }
        if let failed = failures[host], Date().timeIntervalSince(failed) < Self.retryAfter { return }
        guard let components = URLComponents(string: pageURL), let scheme = components.scheme?.lowercased(),
              scheme == "https" || scheme == "http", let realHost = components.host,
              let root = URL(string: "\(scheme)://\(realHost)\(components.port.map { ":\($0)" } ?? "")/")
        else { return }
        inFlight.insert(host)
        Task {
            let image = await Self.fetchIcon(siteRoot: root)
            inFlight.remove(host)
            guard let image, let png = Self.png(image) else {
                failures[host] = Date()
                return
            }
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try? png.write(to: fileURL(for: host), options: .atomic)
            images[host] = image
        }
    }

    // MARK: Fetching

    private static func get(_ url: URL, limit: Int) async -> (Data, URL)? {
        var request = URLRequest(url: url)
        request.setValue(nil, forHTTPHeaderField: "Referer")
        guard let (data, response) = try? await session.data(for: request),
              let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode),
              data.count <= limit else { return nil }
        return (data, http.url ?? url)
    }

    /// The site's best declared icon (apple-touch-icon, then the largest `rel=icon`), or
    /// /favicon.ico.
    private static func fetchIcon(siteRoot: URL) async -> NSImage? {
        var candidates: [URL] = []
        if let (data, finalURL) = await get(siteRoot, limit: 2_000_000),
           let html = String(data: data.prefix(512_000), encoding: .utf8) ?? String(data: data.prefix(512_000), encoding: .isoLatin1) {
            candidates = iconLinks(in: html, base: finalURL)
        }
        if let ico = URL(string: "/favicon.ico", relativeTo: siteRoot)?.absoluteURL { candidates.append(ico) }
        for url in candidates {
            guard let (data, _) = await get(url, limit: 1_000_000), let image = NSImage(data: data),
                  image.isValid, image.size.width >= 16 else { continue }
            return image
        }
        return nil
    }

    /// `<link rel="…icon…" href="…">` targets, best first. SVG is skipped (not every macOS
    /// release can rasterise it).
    static func iconLinks(in html: String, base: URL) -> [URL] {
        guard let linkPattern = try? NSRegularExpression(pattern: "<link\\b[^>]*>", options: [.caseInsensitive]) else { return [] }
        var found: [(url: URL, rank: Int)] = []
        let range = NSRange(html.startIndex..., in: html)
        for match in linkPattern.matches(in: html, range: range) {
            guard let tagRange = Range(match.range, in: html) else { continue }
            let tag = String(html[tagRange])
            guard let rel = attribute("rel", in: tag)?.lowercased(), rel.contains("icon"),
                  let href = attribute("href", in: tag), !href.lowercased().hasSuffix(".svg"),
                  !href.hasPrefix("data:"), let url = URL(string: href, relativeTo: base)?.absoluteURL,
                  ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { continue }
            if attribute("type", in: tag)?.lowercased().contains("svg") == true { continue }
            let size = attribute("sizes", in: tag).flatMap { Int($0.lowercased().split(separator: "x").first ?? "") } ?? 16
            let rank = rel.contains("apple-touch-icon") ? 1000 + size : size
            found.append((url, rank))
        }
        return found.sorted { $0.rank > $1.rank }.map(\.url)
    }

    private static func attribute(_ name: String, in tag: String) -> String? {
        let pattern = "\\b\(name)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))"
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]),
              let match = regex.firstMatch(in: tag, range: NSRange(tag.startIndex..., in: tag)) else { return nil }
        for group in 1...3 {
            if let range = Range(match.range(at: group), in: tag) { return String(tag[range]) }
        }
        return nil
    }

    /// Re-encodes as a PNG of at most 128 points, so nothing the site sent is stored as is.
    private static func png(_ image: NSImage) -> Data? {
        let side = min(128, max(image.size.width, image.size.height))
        let size = NSSize(width: side, height: side)
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(side), pixelsHigh: Int(side),
                                   bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                   colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        guard let rep else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        image.draw(in: NSRect(origin: .zero, size: size), from: .zero, operation: .copy, fraction: 1)
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }
}
