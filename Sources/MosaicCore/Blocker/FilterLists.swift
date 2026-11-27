import Foundation

/// A filter list Mosaic's blocker uses.
public struct FilterList: Hashable, Sendable, Identifiable {
    /// uBlock Origin's identifier for the list; also its file name stem.
    public let id: String
    public let title: String
    public let url: URL

    public var fileName: String { "\(id).txt" }
}

/// The lists uBlock Origin enables out of the box (its `assets.json`, entries
/// that are not marked `off`), fetched from the same places uBO fetches them.
public enum FilterListCatalog {
    public static let suffixListFileName = "public_suffix_list.dat"
    public static let suffixListURL = URL(string: "https://publicsuffix.org/list/public_suffix_list.dat")!

    public static let defaults: [FilterList] = [
        uAssets("ublock-filters", "uBlock filters – Ads", "filters/filters.txt"),
        uAssets("ublock-badware", "uBlock filters – Badware risks", "filters/badware.txt"),
        uAssets("ublock-privacy", "uBlock filters – Privacy", "filters/privacy.txt"),
        uAssets("ublock-unbreak", "uBlock filters – Unbreak", "filters/unbreak.txt"),
        uAssets("ublock-quick-fixes", "uBlock filters – Quick fixes", "filters/quick-fixes.txt"),
        uAssets("easylist", "EasyList", "thirdparties/easylist.txt"),
        uAssets("easyprivacy", "EasyPrivacy", "thirdparties/easyprivacy.txt"),
        FilterList(
            id: "urlhaus-1", title: "Online Malicious URL Blocklist",
            url: URL(string: "https://malware-filter.gitlab.io/urlhaus-filter/urlhaus-filter-vivaldi-online.txt")!),
        FilterList(
            id: "plowe-0", title: "Peter Lowe’s Ad and tracking server list",
            url: URL(string: "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=1&mimetype=plaintext")!),
    ]

    private static func uAssets(_ id: String, _ title: String, _ path: String) -> FilterList {
        FilterList(id: id, title: title, url: URL(string: "https://ublockorigin.github.io/uAssets/\(path)")!)
    }
}

/// The filter lists on disk.
///
///     <directory>/public_suffix_list.dat
///     <directory>/<list id>.txt
///
/// The app ships a snapshot of every list (taken when it was built) so blocking
/// works from the first launch, offline; `seed` copies it here. `update`
/// refreshes the files from the network.
public struct FilterListStore: Sendable {
    public let directory: URL
    public let lists: [FilterList]

    public init(directory: URL, lists: [FilterList] = FilterListCatalog.defaults) {
        self.directory = directory
        self.lists = lists
    }

    public var suffixListURL: URL { directory.appendingPathComponent(FilterListCatalog.suffixListFileName) }
    public func fileURL(for list: FilterList) -> URL { directory.appendingPathComponent(list.fileName) }

    /// Copies any list that is missing here from the snapshot in `bundled`.
    public func seed(from bundled: URL) throws {
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        let names = [FilterListCatalog.suffixListFileName] + lists.map(\.fileName)
        for name in names {
            let destination = directory.appendingPathComponent(name)
            let source = bundled.appendingPathComponent(name)
            if !fileManager.fileExists(atPath: destination.path), fileManager.fileExists(atPath: source.path) {
                try fileManager.copyItem(at: source, to: destination)
            }
        }
    }

    /// The Public Suffix List's text, or nil if it is not on disk.
    public func suffixList() -> String? {
        try? String(contentsOf: suffixListURL, encoding: .utf8)
    }

    /// Every list that is on disk, in catalogue order.
    public func loadLists() -> [BlockerEngine.List] {
        lists.compactMap { list in
            guard let text = try? String(contentsOf: fileURL(for: list), encoding: .utf8), !text.isEmpty else {
                return nil
            }
            return BlockerEngine.List(name: list.id, text: text)
        }
    }

    /// Paths of the lists that are on disk, in catalogue order.
    public func availableListPaths() -> [String] {
        lists.map { fileURL(for: $0).path }.filter { FileManager.default.fileExists(atPath: $0) }
    }

    /// When the least recently refreshed list was written; nil if any is missing.
    public func oldestModification() -> Date? {
        var oldest: Date?
        for url in [suffixListURL] + lists.map(fileURL(for:)) {
            guard let date = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?
                .contentModificationDate else { return nil }
            oldest = min(oldest ?? date, date)
        }
        return oldest
    }

    /// Outcome of refreshing one file.
    public struct UpdateResult: Sendable {
        public var updated: [String] = []
        public var failed: [String] = []
    }

    /// Downloads fresh copies of every list. A download that fails, or that does
    /// not look like the list it should be, leaves the existing file untouched.
    public func update(using session: URLSession = .shared) async -> UpdateResult {
        var result = UpdateResult()
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        var targets: [(name: String, url: URL, destination: URL, isSuffixList: Bool)] = [
            (FilterListCatalog.suffixListFileName, FilterListCatalog.suffixListURL, suffixListURL, true)
        ]
        targets += lists.map { ($0.id, $0.url, fileURL(for: $0), false) }

        for target in targets {
            do {
                var request = URLRequest(url: target.url)
                request.timeoutInterval = 60
                let (data, response) = try await session.data(for: request)
                guard (response as? HTTPURLResponse)?.statusCode == 200,
                      Self.looksValid(data, isSuffixList: target.isSuffixList)
                else {
                    result.failed.append(target.name)
                    continue
                }
                try data.write(to: target.destination, options: .atomic)
                result.updated.append(target.name)
            } catch {
                result.failed.append(target.name)
            }
        }
        return result
    }

    /// Guards against saving an error page or a truncated download as a list.
    static func looksValid(_ data: Data, isSuffixList: Bool) -> Bool {
        guard data.count >= 1024, let head = String(data: data.prefix(4096), encoding: .utf8) else { return false }
        let lowered = head.lowercased()
        if lowered.contains("<html") || lowered.contains("<!doctype") { return false }
        if isSuffixList { return head.contains("publicsuffix.org") || head.contains("===BEGIN ICANN DOMAINS===") }
        // Filter lists open with a header or comment line: "[Adblock Plus 2.0]", "! Title: …", "# …".
        let firstLine = head.split(whereSeparator: \.isNewline).first.map(String.init) ?? ""
        return firstLine.hasPrefix("[") || firstLine.hasPrefix("!") || firstLine.hasPrefix("#")
    }
}
