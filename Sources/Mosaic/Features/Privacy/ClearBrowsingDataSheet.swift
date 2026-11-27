import MosaicCore
import SwiftUI

/// History › Clear Browsing Data… (⇧⌘⌫): picks a time range and kinds of data,
/// then clears them from both engines and from Mosaic's own lists.
struct ClearBrowsingDataSheet: View {
    @ObservedObject var privacy: PrivacyController
    @Environment(\.dismiss) private var dismiss
    @State private var range: BrowsingDataTimeRange = .last24Hours
    @State private var kinds: Set<BrowsingDataKind> = [.history, .cookiesAndSiteData, .cache]
    @State private var isClearing = false

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Clear Browsing Data").font(.title2.bold())
            Picker("Time range", selection: $range) {
                ForEach(BrowsingDataTimeRange.allCases) { range in
                    Text(range.title).tag(range)
                }
            }
            .fixedSize()

            VStack(alignment: .leading, spacing: 10) {
                ForEach(BrowsingDataKind.allCases) { kind in
                    Toggle(isOn: Binding(
                        get: { kinds.contains(kind) },
                        set: { on in if on { kinds.insert(kind) } else { kinds.remove(kind) } }
                    )) {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(kind.title)
                            Text(note(for: kind)).font(.caption).foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    .toggleStyle(.checkbox)
                }
            }

            Text("Private tabs keep nothing to clear: their data disappears when the last one closes.")
                .font(.caption).foregroundStyle(.secondary)

            HStack {
                if isClearing { ProgressView().controlSize(.small) }
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Clear", role: .destructive) {
                    isClearing = true
                    Task {
                        await privacy.clearBrowsingData(kinds, range: range)
                        isClearing = false
                        dismiss()
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(kinds.isEmpty || isClearing)
            }
        }
        .padding(22)
        .frame(width: 460)
    }

    private func note(for kind: BrowsingDataKind) -> String {
        let registered = BrowsingDataRegistry.shared.coveredKinds.contains(kind)
        switch kind {
        case .history:
            return registered
                ? "Mosaic’s history and Mozilla’s. Chromium keeps no history of its own."
                : "Mozilla’s history. Chromium keeps no history of its own."
        case .cookiesAndSiteData:
            return range == .allTime
                ? "Signs you out of most sites, in both engines."
                : "Mozilla: data stored in this period. Chromium: cookies set in this period (other site storage is cleared only for “All time”)."
        case .cache:
            return "Both engines’ caches are emptied completely, whatever the range."
        case .downloads:
            return registered
                ? "Mosaic’s downloads list and Mozilla’s. Downloaded files are not deleted."
                : "Mozilla’s download history. Downloaded files are not deleted."
        case .formData:
            return "Entries Mozilla remembers for form fields. Passwords are not affected."
        }
    }
}

/// The mark of a private tab: shell views draw it on private tabs' chips and
/// address bar (`BrowserTab.isPrivate`), and the privacy settings use it too.
struct PrivateBrowsingBadge: View {
    var compact = false

    var body: some View {
        Image(systemName: "eyeglasses")
            .font(.system(size: compact ? 10 : 13, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: compact ? 16 : 24, height: compact ? 16 : 24)
            .background(Circle().fill(PrivateBrowsingBadge.tint))
            .accessibilityLabel("Private tab")
            .help("Private tab: nothing is saved after it closes")
    }

    /// The colour private tabs are drawn in.
    static let tint = Color(red: 0.42, green: 0.25, blue: 0.75)
}
