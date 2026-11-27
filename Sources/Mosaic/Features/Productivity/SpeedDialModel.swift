import AppKit
import Combine
import MosaicCore

/// The new-tab page's Speed Dial: the user's pinned sites (stored in the profile) followed
/// by the library's most visited sites.
@MainActor
final class SpeedDialModel: ObservableObject {
    @Published private(set) var dial: SpeedDial
    /// The last save error, shown on the page.
    @Published private(set) var saveError: String?

    private let store: SpeedDialStore
    /// Most tiles the grid shows (pinned sites are never cut).
    static let topSiteLimit = 8

    init(store: SpeedDialStore) {
        self.store = store
        dial = store.load()
    }

    /// Pinned sites, then top sites from `library`.
    func tiles(library: Library) -> (pinned: [SpeedDialSite], topSites: [SpeedDialSite]) {
        let candidates = library.topSites(limit: Self.topSiteLimit * 2).map { (title: $0.title, url: $0.value) }
        return (dial.pinned, dial.visibleTopSites(candidates, limit: Self.topSiteLimit))
    }

    @discardableResult
    func add(title: String, url: String, at index: Int? = nil) throws -> SpeedDialSite {
        let site = try dial.add(title: title, url: url, at: index)
        save()
        return site
    }

    func update(_ id: UUID, title: String, url: String) throws {
        try dial.update(id, title: title, url: url)
        save()
    }

    func remove(_ id: UUID) {
        dial.remove(id)
        save()
    }

    func move(_ id: UUID, to index: Int) {
        dial.move(id, to: index)
        save()
    }

    func shift(_ id: UUID, by offset: Int) {
        dial.shift(id, by: offset)
        save()
    }

    /// Pins a top site where it is shown, so it stays.
    func pin(_ site: SpeedDialSite) {
        _ = try? add(title: site.title, url: site.url)
    }

    func hideTopSite(_ site: SpeedDialSite) {
        dial.hideTopSite(url: site.url)
        save()
    }

    func restoreHiddenTopSites() {
        dial.restoreHiddenTopSites()
        save()
    }

    func setShowsTopSites(_ shows: Bool) {
        dial.showsTopSites = shows
        save()
    }

    func setShowsIcons(_ shows: Bool) {
        dial.showsIcons = shows
        save()
    }

    private func save() {
        do {
            try store.save(dial)
            saveError = nil
        } catch {
            saveError = "Speed Dial could not be saved: \(error.localizedDescription)"
        }
    }

    static func message(for error: Error) -> String {
        switch error as? SpeedDial.EditError {
        case .invalidAddress: "Enter a web address, like example.com."
        case .duplicate: "That site is already in Speed Dial."
        case nil: error.localizedDescription
        }
    }
}
