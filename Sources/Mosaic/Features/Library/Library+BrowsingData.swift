import Foundation
import MosaicCore

// Settings › Privacy › Clear Browsing Data clears Mosaic's history list through this.
extension Library: BrowsingDataClearing {
    var browsingDataName: String { "Mosaic history" }
    var clearableBrowsingData: Set<BrowsingDataKind> { [.history] }

    func clearBrowsingData(_ kinds: Set<BrowsingDataKind>, since: Date?) async {
        guard kinds.contains(.history) else { return }
        clearHistory(since: since)
    }
}
