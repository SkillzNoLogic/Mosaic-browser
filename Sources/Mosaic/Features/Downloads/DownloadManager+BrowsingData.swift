import Foundation
import MosaicCore

// Settings › Privacy › Clear Browsing Data clears the downloads list through this. Files on
// disk are never touched; running and paused downloads stay.
extension DownloadManager: BrowsingDataClearing {
    var browsingDataName: String { "Downloads list" }
    var clearableBrowsingData: Set<BrowsingDataKind> { [.downloads] }

    func clearBrowsingData(_ kinds: Set<BrowsingDataKind>, since: Date?) async {
        guard kinds.contains(.downloads) else { return }
        guard let since else {
            clearFinished()
            return
        }
        for record in items where !record.state.isActive && record.startedAt >= since {
            remove(record.id)
        }
    }
}
