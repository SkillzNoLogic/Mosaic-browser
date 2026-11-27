import SwiftUI

/// Settings, inside the main window's page area: a searchable category list on the left,
/// the selected category's page (`SettingsPaneView`) on the right. Esc or the close button
/// returns to the page. Opaque, so a Gecko page never shows through (the model also hides
/// the Gecko window while settings are open).
struct SettingsView: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    let selection: SettingsCategory
    @State private var query = ""
    @FocusState private var searchFocused: Bool

    private static let groups: [[SettingsCategory]] = [
        [.mosaics, .general, .tabs, .search, .engine],
        [.privacy, .passwords, .downloads, .extensions],
        [.shortcuts, .languages, .accessibility, .performance, .about],
    ]

    var body: some View {
        HStack(spacing: 0) {
            sidebar
                .frame(width: 236)
            Rectangle().fill(Color.primary.opacity(0.08)).frame(width: 1)
            VStack(spacing: 0) {
                header
                SettingsPaneView(category: selection)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                    .id(selection)
            }
            .background(Color(nsColor: .windowBackgroundColor))
        }
        .background(ShellColors.page)
        .onExitCommand { model.dismissSettings() }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Settings")
    }

    // MARK: Sidebar

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary).accessibilityHidden(true)
                TextField("Search Settings", text: $query)
                    .textFieldStyle(.plain)
                    .focused($searchFocused)
                    .onSubmit {
                        if let first = SettingsSearchIndex.search(query).first { select(first.category) }
                    }
                    .accessibilityLabel("Search settings")
                if !query.isEmpty {
                    Button {
                        query = ""
                    } label: {
                        Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Clear search")
                }
            }
            .padding(.horizontal, 10)
            .frame(height: 32)
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(ShellColors.control))
            .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(searchFocused ? theme.accent : .clear, lineWidth: 2))
            .padding(.horizontal, 12)
            .padding(.top, 14)

            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    if query.isEmpty {
                        ForEach(Array(Self.groups.enumerated()), id: \.offset) { index, group in
                            if index > 0 { Divider().padding(.vertical, 6).padding(.horizontal, 8) }
                            ForEach(group) { categoryRow($0, labels: []) }
                        }
                    } else {
                        let matches = SettingsSearchIndex.search(query)
                        if matches.isEmpty {
                            Text("No settings match “\(query)”.")
                                .font(.callout)
                                .foregroundStyle(.secondary)
                                .padding(10)
                        }
                        ForEach(matches) { categoryRow($0.category, labels: $0.labels) }
                    }
                }
                .padding(.horizontal, 8)
                .padding(.bottom, 12)
            }
            .scrollIndicators(.never)
        }
        .background(Color(nsColor: .underPageBackgroundColor).opacity(0.5))
    }

    private func categoryRow(_ category: SettingsCategory, labels: [String]) -> some View {
        let isSelected = category == selection
        return Button {
            select(category)
        } label: {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 10) {
                    Image(systemName: category.symbol)
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(isSelected ? Color.white : theme.accent)
                        .frame(width: 26, height: 26)
                        .background(RoundedRectangle(cornerRadius: 7, style: .continuous)
                            .fill(isSelected ? theme.accent : theme.accent.opacity(0.13)))
                    Text(category.title)
                        .font(.system(size: 13, weight: isSelected ? .semibold : .regular))
                    Spacer(minLength: 0)
                }
                ForEach(labels.prefix(4), id: \.self) { label in
                    Text(label)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .padding(.leading, 36)
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(isSelected ? Color.primary.opacity(0.08) : .clear))
        }
        .buttonStyle(HoverRowStyle(cornerRadius: 8))
        .accessibilityLabel(category.title)
        .accessibilityAddTraits(isSelected ? [.isSelected] : [])
    }

    // MARK: Header

    private var header: some View {
        HStack(spacing: 10) {
            Image(systemName: selection.symbol)
                .font(.system(size: 17, weight: .medium))
                .foregroundStyle(theme.accent)
                .accessibilityHidden(true)
            Text(selection.title)
                .font(.title2.weight(.semibold))
                .accessibilityAddTraits(.isHeader)
            Spacer()
            Button {
                model.dismissSettings()
            } label: {
                Label("Done", systemImage: "xmark")
                    .labelStyle(.titleAndIcon)
                    .padding(.horizontal, 4)
            }
            .controlSize(.large)
            .keyboardShortcut(.cancelAction)
            .help("Close Settings (Esc)")
            .accessibilityLabel("Close settings")
        }
        .padding(.horizontal, 24)
        .frame(height: 58)
        .overlay(alignment: .bottom) { Rectangle().fill(Color.primary.opacity(0.07)).frame(height: 1) }
    }

    private func select(_ category: SettingsCategory) {
        model.showSettings(category)
    }
}

// MARK: - Shared pieces for settings pages

/// A short explanatory line under a setting.
struct SettingsNote: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }
}
