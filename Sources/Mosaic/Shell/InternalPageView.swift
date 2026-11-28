import SwiftUI

/// Draws an internal page (see `InternalPage`). Shell-owned; each case's view belongs to the
/// area named next to it.
struct InternalPageView: View {
    let page: InternalPage

    var body: some View {
        switch page {
        case .bookmarks: BookmarksPage()        // library
        case .history: HistoryPage()            // library
        case .readingList: ReadingListPage()    // library
        case .downloads: DownloadsPage()        // downloads
        }
    }
}
