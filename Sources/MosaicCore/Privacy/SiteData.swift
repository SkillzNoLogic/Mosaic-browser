import Foundation

/// The Public Suffix List (publicsuffix.org), for grouping hosts into sites:
/// `news.example.co.uk` and `shop.example.co.uk` both belong to `example.co.uk`.
/// Mosaic already ships the list for the content blocker.
public struct PublicSuffixList: Sendable {
    private var rules: Set<String> = []
    private var wildcards: Set<String> = []
    private var exceptions: Set<String> = []

    public init(text: String) {
        for line in text.split(whereSeparator: \.isNewline) {
            let rule = line.split(separator: " ", maxSplits: 1).first.map(String.init) ?? ""
            if rule.isEmpty || rule.hasPrefix("//") { continue }
            let lowered = rule.lowercased()
            if lowered.hasPrefix("!") {
                exceptions.insert(String(lowered.dropFirst()))
            } else if lowered.hasPrefix("*.") {
                wildcards.insert(String(lowered.dropFirst(2)))
            } else {
                rules.insert(lowered)
            }
        }
    }

    /// The public suffix of `host` (`co.uk` for `a.example.co.uk`); unknown
    /// top-level domains count as suffixes, as the list's algorithm says.
    public func publicSuffix(of host: String) -> String {
        let labels = host.lowercased().split(separator: ".").map(String.init)
        guard !labels.isEmpty else { return host }
        for start in 0..<labels.count {
            let candidate = labels[start...].joined(separator: ".")
            if exceptions.contains(candidate) {
                return labels[(start + 1)...].joined(separator: ".")
            }
            if rules.contains(candidate) { return candidate }
            if start > 0, wildcards.contains(candidate) {
                return labels[(start - 1)...].joined(separator: ".")
            }
        }
        return labels.last ?? host
    }

    /// The site a host belongs to: its public suffix plus one label. An IP
    /// address, a single-label name or a bare suffix is its own site.
    public func site(of host: String) -> String {
        var host = host.lowercased()
        while host.hasPrefix(".") { host.removeFirst() }
        if host.isEmpty || !host.contains(".") || host.allSatisfy({ $0.isNumber || $0 == "." || $0 == ":" }) {
            return host
        }
        let suffix = publicSuffix(of: host)
        guard host.count > suffix.count, host.hasSuffix("." + suffix) else { return host }
        let rest = host.dropLast(suffix.count + 1)
        let label = rest.split(separator: ".").last.map(String.init) ?? String(rest)
        return label + "." + suffix
    }
}

/// One site that has data stored, as the Cookies and Site Data list shows it.
public struct SiteDataEntry: Identifiable, Hashable, Sendable {
    public var site: String
    /// Hosts the data was stored under (`example.com`, `.example.com`, `www.example.com`).
    public var hosts: Set<String> = []
    public var chromiumCookies = 0
    public var geckoCookies = 0
    /// Bytes of Gecko storage (local storage, IndexedDB, cache storage…).
    public var geckoStorageBytes: Int64 = 0

    public var id: String { site }
    public var cookieCount: Int { chromiumCookies + geckoCookies }

    public init(site: String) { self.site = site }

    /// Origins whose storage to clear in Chromium for this site.
    public var chromiumOrigins: [String] {
        var names = hosts.map { $0.hasPrefix(".") ? String($0.dropFirst()) : $0 }
        names.append(site)
        names.append("www." + site)
        return Set(names).sorted().flatMap { ["https://\($0)", "http://\($0)"] }
    }
}

public enum SiteDataSummary {
    /// One cookie or storage record from an engine.
    public struct Record: Sendable {
        public enum Engine: Sendable { case chromium, gecko }
        public var engine: Engine
        public var host: String
        public var cookies: Int
        public var storageBytes: Int64

        public init(engine: Engine, host: String, cookies: Int = 0, storageBytes: Int64 = 0) {
            self.engine = engine
            self.host = host
            self.cookies = cookies
            self.storageBytes = storageBytes
        }
    }

    /// Groups engine records by site, sorted by site name.
    public static func merge(_ records: [Record], suffixList: PublicSuffixList?) -> [SiteDataEntry] {
        var bySite: [String: SiteDataEntry] = [:]
        for record in records {
            var host = record.host.lowercased()
            while host.hasPrefix(".") { host.removeFirst() }
            guard !host.isEmpty else { continue }
            let site = suffixList?.site(of: host) ?? (host.hasPrefix("www.") ? String(host.dropFirst(4)) : host)
            var entry = bySite[site] ?? SiteDataEntry(site: site)
            entry.hosts.insert(record.host.lowercased())
            switch record.engine {
            case .chromium: entry.chromiumCookies += record.cookies
            case .gecko:
                entry.geckoCookies += record.cookies
                entry.geckoStorageBytes += record.storageBytes
            }
            bySite[site] = entry
        }
        return bySite.values.sorted { $0.site < $1.site }
    }
}
