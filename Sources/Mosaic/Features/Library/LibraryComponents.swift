import AppKit
import MosaicCore
import SwiftUI

/// Shared pieces of the library's pages (Bookmarks, History, Reading List).

/// Opens an address from a library page: in the current tab, or a new one when asked or
/// when ⌘ is held.
@MainActor
func libraryOpen(_ url: String, in model: BrowserModel, newTab: Bool = false) {
    let flags = NSEvent.modifierFlags
    if newTab {
        model.newTab(url: url)
    } else if flags.contains(.command) {
        // As in other browsers: ⌘-click opens in the background, ⇧⌘-click brings it forward.
        model.newTab(url: url, select: flags.contains(.shift))
    } else {
        model.isEditingAddress = false
        model.load(url, in: model.selectedTab ?? model.newTab())
        model.focusContent()
    }
}

/// Title row of a library page.
struct LibraryPageHeader<Trailing: View>: View {
    let title: String
    let subtitle: String
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.system(size: 26, weight: .semibold))
                    .accessibilityAddTraits(.isHeader)
                Text(subtitle)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            Spacer(minLength: 12)
            HStack(spacing: 10) { trailing }
        }
        .padding(.horizontal, 28)
        .padding(.top, 22)
        .padding(.bottom, 14)
    }
}

/// A rounded search field like the one in Finder's and Mail's toolbars.
struct LibrarySearchField: View {
    let prompt: String
    @Binding var text: String
    var focus: FocusState<Bool>.Binding?

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
            field
            if !text.isEmpty {
                Button {
                    text = ""
                } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 9)
        .frame(width: 260, height: 30)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Color.primary.opacity(0.12)))
    }

    @ViewBuilder private var field: some View {
        let base = TextField(prompt, text: $text)
            .textFieldStyle(.plain)
            .font(.system(size: 13))
            .accessibilityLabel(prompt)
        if let focus { base.focused(focus) } else { base }
    }
}

/// A small letter tile standing in for a site icon (Mosaic does not fetch favicons for
/// the library), coloured consistently per site.
struct SiteMonogram: View {
    let url: String
    var size: CGFloat = 24

    var body: some View {
        let name = Self.siteName(of: url)
        let letter = name.first.map { String($0).uppercased() } ?? "•"
        RoundedRectangle(cornerRadius: size * 0.28)
            .fill(Self.color(for: name).gradient)
            .frame(width: size, height: size)
            .overlay(
                Text(letter)
                    .font(.system(size: size * 0.5, weight: .semibold, design: .rounded))
                    .foregroundStyle(.white)
            )
            .accessibilityHidden(true)
    }

    /// The name part of the host: "wikipedia" for en.wikipedia.org, "bbc" for bbc.co.uk.
    static func siteName(of url: String) -> String {
        let host = LibraryURL.host(of: url)
        let labels = host.split(separator: ".").map(String.init)
        guard labels.count >= 2, !labels.allSatisfy({ $0.allSatisfy(\.isNumber) }) else { return host }
        let generic: Set<String> = ["co", "com", "org", "net", "ac", "gov", "edu"]
        if labels.count >= 3, generic.contains(labels[labels.count - 2]) { return labels[labels.count - 3] }
        return labels[labels.count - 2]
    }

    static func color(for host: String) -> Color {
        let palette: [Color] = [
            Color(red: 0.36, green: 0.52, blue: 0.95), Color(red: 0.93, green: 0.45, blue: 0.27),
            Color(red: 0.29, green: 0.69, blue: 0.51), Color(red: 0.62, green: 0.42, blue: 0.86),
            Color(red: 0.86, green: 0.36, blue: 0.52), Color(red: 0.20, green: 0.63, blue: 0.75),
            Color(red: 0.78, green: 0.58, blue: 0.20), Color(red: 0.47, green: 0.53, blue: 0.60),
        ]
        // A stable hash (String.hashValue changes every launch).
        let value = host.unicodeScalars.reduce(UInt32(5381)) { ($0 &* 33) &+ $1.value }
        return palette[Int(value % UInt32(palette.count))]
    }
}

/// A rounded tag label.
struct TagChip: View {
    let tag: String

    var body: some View {
        Text(tag)
            .font(.system(size: 11, weight: .medium))
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .background(Capsule().fill(Color.accentColor.opacity(0.14)))
            .foregroundStyle(Color.accentColor)
    }
}

/// Banner shown on library pages when the database could not be opened.
struct LibraryStorageWarning: View {
    let message: String?

    var body: some View {
        if let message {
            Label(message, systemImage: "exclamationmark.triangle.fill")
                .font(.callout)
                .foregroundStyle(.orange)
                .padding(.horizontal, 28)
                .padding(.bottom, 8)
        }
    }
}

/// The library pages draw opaquely over the content area (Gecko's window is behind it).
extension View {
    func libraryPageBackground() -> some View {
        frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            .background(Color(nsColor: .windowBackgroundColor))
    }
}

/// Bookmarks and history rows are dragged as text carrying this prefix and the bookmark's id,
/// so drops can tell a bookmark being moved from an address dragged in from a page.
enum BookmarkDrag {
    static let prefix = "mosaic-bookmark:"
    static func payload(_ id: String) -> String { prefix + id }
    static func id(from payload: String) -> String? {
        payload.hasPrefix(prefix) ? String(payload.dropFirst(prefix.count)) : nil
    }
}

extension Date {
    /// "14:05" today, "Yesterday", "Mon 3 Nov", "3 Nov 2024".
    var libraryShortDescription: String {
        let calendar = Calendar.current
        if calendar.isDateInToday(self) { return formatted(date: .omitted, time: .shortened) }
        if calendar.isDateInYesterday(self) { return "Yesterday" }
        if calendar.isDate(self, equalTo: Date(), toGranularity: .year) {
            return formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated))
        }
        return formatted(date: .abbreviated, time: .omitted)
    }
}
