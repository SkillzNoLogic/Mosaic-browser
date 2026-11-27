import MosaicCore
import SwiftUI

/// Settings › Languages: the languages websites are asked for, in order, applied to both
/// engines (Gecko's and Chromium's `intl.accept_languages`).
struct LanguagesSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        LanguagesForm(languages: model.platform.languages)
    }
}

private struct LanguagesForm: View {
    @ObservedObject var languages: LanguageSettings
    @State private var customTag = ""
    @State private var customError: String?

    var body: some View {
        let tags = languages.preferences.tags
        Form {
            Section {
                SettingsNote("""
                    Websites that offer several languages use this list, top first, to pick one. Both engines \
                    send it (the Accept-Language header and navigator.languages). Mosaic’s own interface is in \
                    English for now.
                    """)
            }

            Section {
                ForEach(Array(tags.enumerated()), id: \.element) { index, tag in
                    HStack(spacing: 10) {
                        Text("\(index + 1).").monospacedDigit().foregroundStyle(.secondary).frame(width: 22, alignment: .trailing)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(LanguagePreferences.displayName(for: tag))
                            Text(tag).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button {
                            languages.edit { $0.move(tag, by: -1) }
                        } label: { Image(systemName: "chevron.up") }
                            .disabled(index == 0)
                            .help("Move up")
                            .accessibilityLabel("Move \(LanguagePreferences.displayName(for: tag)) up")
                        Button {
                            languages.edit { $0.move(tag, by: 1) }
                        } label: { Image(systemName: "chevron.down") }
                            .disabled(index == tags.count - 1)
                            .help("Move down")
                            .accessibilityLabel("Move \(LanguagePreferences.displayName(for: tag)) down")
                        Button {
                            languages.edit { $0.remove(tag) }
                        } label: { Image(systemName: "minus.circle") }
                            .disabled(tags.count == 1)
                            .help("Remove")
                            .accessibilityLabel("Remove \(LanguagePreferences.displayName(for: tag))")
                    }
                    .buttonStyle(.borderless)
                }
                .onMove { source, destination in
                    languages.edit { $0.move(fromOffsets: source, toOffset: destination) }
                }
            } header: {
                Text("Preferred Languages")
            } footer: {
                Text("Pages receive: Accept-Language: \(languages.preferences.acceptLanguageHeader)")
                    .font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }

            Section("Add a Language") {
                HStack(spacing: 8) {
                    Menu("Add Language") {
                        ForEach(available, id: \.self) { tag in
                            Button("\(LanguagePreferences.displayName(for: tag)) (\(tag))") {
                                languages.edit { $0.add(tag) }
                            }
                        }
                    }
                    .menuStyle(.button)
                    .fixedSize()
                    Spacer()
                    Text("Or a tag:").foregroundStyle(.secondary)
                    TextField("Language tag", text: $customTag, prompt: Text("e.g. gsw-CH"))
                        .labelsHidden()
                        .frame(maxWidth: 140)
                        .onSubmit(addCustom)
                        .accessibilityLabel("Language tag to add")
                    Button("Add", action: addCustom)
                        .disabled(customTag.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                if let customError {
                    Text(customError).font(.callout).foregroundStyle(.red)
                }
                if languages.isCustom {
                    Button("Use macOS Language Order") { languages.useSystemLanguages() }
                } else {
                    SettingsNote("Following your Mac’s language order (System Settings › General › Language & Region).")
                }
            }

            Section {
                SettingsNote("Changes apply to pages loaded from now on; reload a page to see it in a new language.")
                if let error = languages.chromiumError {
                    Label("Chromium: \(error)", systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                }
                if let error = languages.geckoError {
                    Label("Mozilla: \(error)", systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                }
            }
        }
        .formStyle(.grouped)
    }

    private var available: [String] {
        let listed = Set(languages.preferences.tags)
        return LanguagePreferences.common
            .filter { !listed.contains($0) }
            .sorted { LanguagePreferences.displayName(for: $0) < LanguagePreferences.displayName(for: $1) }
    }

    private func addCustom() {
        guard let tag = LanguagePreferences.normalize(customTag) else {
            customError = "“\(customTag)” isn’t a language tag. Use a code such as fr, pt-BR or zh-Hant-TW."
            return
        }
        if languages.preferences.tags.contains(tag) {
            customError = "\(LanguagePreferences.displayName(for: tag)) is already in the list."
            return
        }
        languages.edit { $0.add(tag) }
        customTag = ""
        customError = nil
    }
}
