import Foundation

/// Cosmetic filtering's stylesheets, computed from an engine on demand and
/// cached: the generic sheet once, each hostname's rules the first time a page
/// on it loads. Safe to use from any thread.
public final class CosmeticStyleCache: @unchecked Sendable {
    private let engine: BlockerEngine
    private let lock = NSLock()
    private var generic: String?
    /// Generic sheets with a site's exceptions left out, keyed by the exceptions.
    private var genericVariants: [[String]: String] = [:]
    private var byHost: [String: BlockerEngine.CosmeticRules] = [:]
    private var hostOrder: [String] = []
    private let capacity: Int

    public init(engine: BlockerEngine, capacity: Int = 256) {
        self.engine = engine
        self.capacity = capacity
    }

    /// Site-specific rules for `hostname` (lower-cased, without a port).
    public func rules(forHostname hostname: String) -> BlockerEngine.CosmeticRules {
        let host = hostname.lowercased()
        if let cached = lock.withLock({ byHost[host] }) { return cached }
        let rules = engine.cosmeticRules(forHostname: host)
        lock.withLock {
            if byHost[host] == nil {
                hostOrder.append(host)
                if hostOrder.count > capacity { byHost[hostOrder.removeFirst()] = nil }
            }
            byHost[host] = rules
        }
        return rules
    }

    /// The generic stylesheet, without `excluded`.
    public func genericCSS(excluding excluded: [String] = []) -> String {
        let key = excluded.sorted()
        if key.isEmpty, let generic = lock.withLock({ generic }) { return generic }
        if !key.isEmpty, let variant = lock.withLock({ genericVariants[key] }) { return variant }
        let css = engine.genericCosmeticCSS(excluding: key)
        lock.withLock {
            if key.isEmpty {
                generic = css
            } else {
                if genericVariants.count >= 8 { genericVariants.removeAll() }
                genericVariants[key] = css
            }
        }
        return css
    }

    /// Everything to hide on a page on `hostname`: its site-specific rules and,
    /// with `includeGeneric`, the generic sheet minus the site's exceptions.
    public func stylesheet(forHostname hostname: String, includeGeneric: Bool) -> String {
        let rules = rules(forHostname: hostname)
        guard includeGeneric else { return rules.css }
        let generic = genericCSS(excluding: rules.genericExceptions)
        if rules.css.isEmpty { return generic }
        if generic.isEmpty { return rules.css }
        return generic + "\n" + rules.css
    }
}

/// The script Mosaic runs in a Chromium frame to apply cosmetic filtering.
public enum CosmeticInjection {
    /// JavaScript that adds `css` to the document once. It uses a constructed
    /// stylesheet (`adoptedStyleSheets`), which a page's Content Security Policy
    /// does not restrict, and falls back to a `<style>` element.
    public static func script(css: String) -> String? {
        guard !css.isEmpty,
              let data = try? JSONSerialization.data(withJSONObject: [css]),
              let array = String(data: data, encoding: .utf8)
        else { return nil }
        let literal = String(array.dropFirst().dropLast())
        return """
        (() => {
        const key = Symbol.for('mosaic.cosmetic'), doc = document;
        if (doc[key]) return;
        Object.defineProperty(doc, key, { value: true });
        const css = \(literal);
        try {
        const sheet = new CSSStyleSheet();
        sheet.replaceSync(css);
        doc.adoptedStyleSheets = [...doc.adoptedStyleSheets, sheet];
        } catch (_) {
        const style = doc.createElement('style');
        style.textContent = css;
        (doc.head || doc.documentElement).appendChild(style);
        }
        })();
        """
    }
}

/// Requests the blocker stopped in Chromium tabs, for the blocker dashboard.
/// Written on Chromium's network thread, read on the main thread.
///
/// Chromium tells the request filter the page's *site* (its first party for
/// cookies, e.g. `https://example.com/` for a page on `news.example.com`), not
/// the page itself, so entries are kept per site host and looked up from a
/// page's address by host. Only the request is recorded; which filter matched
/// is worked out when the dashboard asks (`BlockerEngine.details`), off the
/// request path.
public final class BlockLog: @unchecked Sendable {
    public struct Entry: Hashable, Sendable {
        public let url: String
        public let type: String
        /// The page's site as Chromium reported it; what to match filters against.
        public let documentURL: String
        public internal(set) var count: Int
    }

    private let lock = NSLock()
    private var sites: [String: [Entry]] = [:]
    private var siteOrder: [String] = []
    private let maxSites: Int
    private let maxEntries: Int

    public init(maxPages: Int = 64, maxEntries: Int = 500) {
        self.maxSites = maxPages
        self.maxEntries = maxEntries
    }

    private static func host(_ url: String) -> String {
        (URLComponents(string: url)?.host ?? url).lowercased()
    }

    /// Notes that a request from the page whose site is `documentURL` was blocked.
    public func record(url: String, documentURL: String, type: String) {
        let key = Self.host(documentURL)
        lock.withLock {
            if sites[key] == nil {
                siteOrder.append(key)
                if siteOrder.count > maxSites { sites[siteOrder.removeFirst()] = nil }
                sites[key] = []
            }
            if let index = sites[key]?.firstIndex(where: { $0.url == url && $0.type == type }) {
                sites[key]?[index].count += 1
            } else if (sites[key]?.count ?? 0) < maxEntries {
                sites[key]?.append(Entry(url: url, type: type, documentURL: documentURL, count: 1))
            }
        }
    }

    private func keys(for pageURL: String) -> [String] {
        let host = Self.host(pageURL)
        return siteOrder.filter { host == $0 || host.hasSuffix("." + $0) }
    }

    /// Blocked requests of the page at `pageURL` (of its site), in the order first seen.
    public func entries(forPage pageURL: String) -> [Entry] {
        lock.withLock { keys(for: pageURL).flatMap { sites[$0] ?? [] } }
    }

    /// Forgets a page's site (a page there is loading again).
    public func clear(page pageURL: String) {
        lock.withLock {
            for key in keys(for: pageURL) {
                sites[key] = nil
                siteOrder.removeAll { $0 == key }
            }
        }
    }
}
