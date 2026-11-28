import Foundation

/// A filter list Mosaic's blocker uses.
public struct FilterList: Hashable, Sendable, Identifiable {
    /// uBlock Origin's identifier for the list; also its file name stem.
    public let id: String
    public let title: String
    public let url: URL
    /// uBO's catalogue group: default, ads, privacy, malware, annoyances, multipurpose.
    public var group: String = "default"
    /// One line on what the list is for (optional lists only).
    public var summary: String = ""

    public var fileName: String { "\(id).txt" }

    public init(id: String, title: String, url: URL, group: String = "default", summary: String = "") {
        self.id = id
        self.title = title
        self.url = url
        self.group = group
        self.summary = summary
    }
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

    /// Stricter lists from uBO's catalogue (`assets/assets.json`, entries marked
    /// `off`), offered in Settings. Not shipped with the app: downloaded when the
    /// user turns one on. Left out: lists made mostly of things Mosaic does not
    /// apply (AdGuard URL Tracking is all `$removeparam`; Mosaic strips tracking
    /// parameters itself) and regional lists.
    public static let optional: [FilterList] = [
        uAssets("ublock-experimental", "uBlock filters – Experimental", "filters/experimental.txt", "default",
                "Filters uBO is still testing. May break sites."),
        FilterList(
            id: "adguard-generic", title: "AdGuard – Ads",
            url: URL(string: "https://filters.adtidy.org/extension/ublock/filters/2_without_easylist.txt")!,
            group: "ads", summary: "AdGuard’s ad filters, without the parts EasyList already covers."),
        uAssets("block-lan", "Block Outsider Intrusion into LAN", "filters/lan-block.txt", "privacy",
                "Stops public websites from reaching devices on your local network."),
        FilterList(
            id: "curben-phishing", title: "Phishing URL Blocklist",
            url: URL(string: "https://malware-filter.gitlab.io/phishing-filter/phishing-filter.txt")!,
            group: "malware", summary: "Known phishing sites."),
        uAssets("fanboy-cookiemonster", "EasyList – Cookie Notices", "thirdparties/easylist-cookies.txt",
                "annoyances", "Hides cookie consent banners."),
        uAssets("ublock-cookies-easylist", "uBlock filters – Cookie Notices", "filters/annoyances-cookies.txt",
                "annoyances", "uBO’s additions to the cookie notice lists."),
        FilterList(
            id: "adguard-cookies", title: "AdGuard – Cookie Notices",
            url: URL(string: "https://filters.adtidy.org/extension/ublock/filters/18.txt")!,
            group: "annoyances", summary: "AdGuard’s cookie consent banner filters."),
        uAssets("fanboy-social", "EasyList – Social Widgets", "thirdparties/easylist-social.txt", "annoyances",
                "Share buttons and social media embeds."),
        FilterList(
            id: "adguard-popup-overlays", title: "AdGuard – Popup Overlays",
            url: URL(string: "https://filters.adtidy.org/extension/ublock/filters/19.txt")!,
            group: "annoyances", summary: "Subscription and sign-up pop-overs."),
        uAssets("easylist-newsletters", "EasyList – Newsletter Notices", "thirdparties/easylist-newsletters.txt",
                "annoyances", "Newsletter sign-up boxes."),
        uAssets("easylist-notifications", "EasyList – Notifications", "thirdparties/easylist-notifications.txt",
                "annoyances", "Requests to show notifications."),
        uAssets("easylist-chat", "EasyList – Chat Widgets", "thirdparties/easylist-chat.txt", "annoyances",
                "Support and sales chat bubbles."),
        uAssets("easylist-annoyances", "EasyList – Other Annoyances", "thirdparties/easylist-annoyances.txt",
                "annoyances", "Other page clutter."),
        uAssets("ublock-annoyances", "uBlock filters – Annoyances", "filters/annoyances.txt", "annoyances",
                "uBO’s own annoyance filters."),
        FilterList(
            id: "dpollock-0", title: "Dan Pollock’s hosts file",
            url: URL(string: "https://someonewhocares.org/hosts/hosts")!,
            group: "multipurpose", summary: "A long-running hosts-format block list."),
    ]

    /// Every list Mosaic knows, defaults first.
    public static var all: [FilterList] { defaults + optional }

    /// The lists for a set of enabled IDs, in catalogue order.
    public static func lists(enabled: Set<String>) -> [FilterList] {
        all.filter { enabled.contains($0.id) }
    }

    /// The name the user's own filters are compiled under.
    public static let userFiltersName = "user-filters"

    private static func uAssets(
        _ id: String, _ title: String, _ path: String, _ group: String = "default", _ summary: String = ""
    ) -> FilterList {
        FilterList(id: id, title: title, url: URL(string: "https://ublockorigin.github.io/uAssets/\(path)")!,
                   group: group, summary: summary)
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
    /// The user's own filters ("My filters"), compiled after every list.
    public var userFiltersURL: URL { directory.appendingPathComponent("\(FilterListCatalog.userFiltersName).txt") }

    /// The user's own filters, or "" when there are none.
    public func userFilters() -> String {
        (try? String(contentsOf: userFiltersURL, encoding: .utf8)) ?? ""
    }

    public func saveUserFilters(_ text: String) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try Data(text.utf8).write(to: userFiltersURL, options: .atomic)
    }

    /// Enabled lists that have not been downloaded yet.
    public func missingLists() -> [FilterList] {
        lists.filter { !FileManager.default.fileExists(atPath: fileURL(for: $0).path) }
    }

    public func modificationDate(of list: FilterList) -> Date? {
        (try? fileURL(for: list).resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
    }
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

    /// Every list that is on disk, in catalogue order, then the user's filters.
    public func loadLists() -> [BlockerEngine.List] {
        var loaded: [BlockerEngine.List] = lists.compactMap { list in
            guard let text = try? String(contentsOf: fileURL(for: list), encoding: .utf8), !text.isEmpty else {
                return nil
            }
            return BlockerEngine.List(name: list.id, text: text)
        }
        let user = userFilters()
        if !user.isEmpty { loaded.append(BlockerEngine.List(name: FilterListCatalog.userFiltersName, text: user)) }
        return loaded
    }

    /// Paths of the lists that are on disk, in catalogue order, then the user's filters.
    public func availableListPaths() -> [String] {
        var paths = lists.map { fileURL(for: $0).path }.filter { FileManager.default.fileExists(atPath: $0) }
        if !userFilters().isEmpty { paths.append(userFiltersURL.path) }
        return paths
    }

    /// Which loaded list contains `filter` as a line, for the dashboard ("My
    /// filters" for the user's own). uBO's logger shows filters in a normalised
    /// form, so this finds most but not all of them; nil when not found.
    public func listContaining(filter: String) -> String? {
        guard !filter.isEmpty else { return nil }
        let candidates = Set([filter, filter.hasPrefix("||") ? filter : "||\(filter)^"])
        let user = userFilters()
        if user.split(whereSeparator: \.isNewline).contains(where: { candidates.contains($0.trimmingCharacters(in: .whitespaces)) }) {
            return "My filters"
        }
        for list in lists {
            guard let text = try? String(contentsOf: fileURL(for: list), encoding: .utf8) else { continue }
            for line in text.split(whereSeparator: \.isNewline) where candidates.contains(String(line)) {
                return list.title
            }
        }
        return nil
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
    /// - Parameter only: refresh just these lists (by ID), not the suffix list.
    public func update(using session: URLSession = .shared, only: Set<String>? = nil) async -> UpdateResult {
        var result = UpdateResult()
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        var targets: [(name: String, url: URL, destination: URL, isSuffixList: Bool)] = []
        if only == nil {
            targets.append((FilterListCatalog.suffixListFileName, FilterListCatalog.suffixListURL, suffixListURL, true))
        }
        targets += lists.filter { only?.contains($0.id) ?? true }.map { ($0.id, $0.url, fileURL(for: $0), false) }

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
