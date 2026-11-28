import Foundation

/// Pages Mosaic draws itself, opened as ordinary tabs (address `mosaic://<name>`), so the
/// library and downloads live inside the window rather than in separate windows. The
/// shell renders a tab whose `internalPage` is set with `InternalPageView`; each page's
/// view belongs to its feature area.
enum InternalPage: String, CaseIterable, Codable {
    case bookmarks, history, downloads, readingList = "reading-list"

    static let scheme = "mosaic"

    var url: String { "\(Self.scheme)://\(rawValue)" }

    var title: String {
        switch self {
        case .bookmarks: "Bookmarks"
        case .history: "History"
        case .downloads: "Downloads"
        case .readingList: "Reading List"
        }
    }

    init?(url: String) {
        guard let components = URLComponents(string: url), components.scheme == Self.scheme,
              let host = components.host, let page = InternalPage(rawValue: host) else { return nil }
        self = page
    }
}
