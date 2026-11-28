import MosaicCore
import SwiftUI

/// Mount point: the page overlays of the selected tab, drawn over the top of the content
/// area. Owner: **engine**; the shell places it (`.overlay(alignment: .top)` on the content
/// area, in every Mosaic). It shows, when there is something to show:
/// - the find bar (top trailing),
/// - a permission prompt (top leading, under the toolbar),
/// - a blocked pop-up notice (top trailing, under the find bar),
/// - a Chromium page's JavaScript dialog (top centre; Gecko draws its own in the page).
/// Empty areas draw nothing and take no clicks, so the page underneath stays usable.
struct FindBar: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        PageOverlays(features: model.pageFeatures, selectedTabID: model.selectedTabID)
    }
}

private struct PageOverlays: View {
    @ObservedObject var features: PageFeatures
    let selectedTabID: UUID?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let prompt = features.permissionPrompts.first { $0.tabID == selectedTabID }
        let notice = features.popupNotices.first { $0.tabID == selectedTabID }
        let dialog = features.dialogs.first { $0.tabID == selectedTabID }
        let showsFind = features.isFindBarVisible && features.findTabID == selectedTabID
        ZStack(alignment: .top) {
            HStack(alignment: .top, spacing: 10) {
                if let prompt {
                    PermissionPromptCard(features: features, prompt: prompt)
                        .transition(transition(edge: .leading))
                }
                Spacer(minLength: 0)
                VStack(alignment: .trailing, spacing: 8) {
                    if showsFind {
                        FindBarCard(features: features)
                            .transition(transition(edge: .top))
                    }
                    if let notice {
                        PopupNoticeCard(features: features, notice: notice)
                            .transition(transition(edge: .trailing))
                    }
                }
            }
            if let dialog {
                JavaScriptDialogCard(features: features, item: dialog)
                    .id(dialog.id)
                    .transition(transition(edge: .top))
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .animation(reduceMotion ? nil : .easeOut(duration: 0.15), value: prompt?.id)
        .animation(reduceMotion ? nil : .easeOut(duration: 0.15), value: notice?.id)
        .animation(reduceMotion ? nil : .easeOut(duration: 0.15), value: showsFind)
        .animation(reduceMotion ? nil : .easeOut(duration: 0.15), value: dialog?.id)
    }

    private func transition(edge: Edge) -> AnyTransition {
        reduceMotion ? .opacity : .move(edge: edge).combined(with: .opacity)
    }
}

/// Rounded, solid card. Not glass or a blur material: a backdrop effect cannot sample the
/// Chromium page (a remote layer) and turns the whole overlay area opaque white over it.
private struct OverlayCard: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(12)
            .background(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .fill(Color(nsColor: .windowBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Color.primary.opacity(0.12)))
            .shadow(color: .black.opacity(0.18), radius: 12, y: 4)
    }
}

private extension View {
    func overlayCard() -> some View { modifier(OverlayCard()) }
}

// MARK: - Find

private struct FindBarCard: View {
    @ObservedObject var features: PageFeatures
    @FocusState private var fieldFocused: Bool

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary).accessibilityHidden(true)
            TextField("Find in page", text: $features.findText)
                .textFieldStyle(.plain)
                .frame(width: 200)
                .focused($fieldFocused)
                .onSubmit { features.findNext() }
                .onExitCommand { features.closeFindBar() }
                .accessibilityLabel("Find in page")
            if let status = features.findStatus, !features.findText.isEmpty {
                Text(status.label)
                    .font(.callout.monospacedDigit())
                    .foregroundStyle(status.hasMatches ? Color.secondary : Color.red)
                    .accessibilityLabel(status.accessibilityLabel)
                    .fixedSize()
            }
            Divider().frame(height: 18)
            Toggle(isOn: $features.findMatchCase) {
                Text("Aa").font(.callout.weight(.semibold))
            }
            .toggleStyle(.button)
            .buttonStyle(.borderless)
            .help("Match Case")
            .accessibilityLabel("Match case")
            iconButton("chevron.up", "Previous Match (⇧⌘G)") { features.findNext(backwards: true) }
            iconButton("chevron.down", "Next Match (⌘G)") { features.findNext() }
            iconButton("xmark", "Close Find Bar (Esc)") { features.closeFindBar() }
        }
        .overlayCard()
        .onAppear { fieldFocused = true }
        .onChange(of: features.findFocusRequest) { _, _ in fieldFocused = true }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Find in page")
    }

    private func iconButton(_ symbol: String, _ label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 12, weight: .semibold))
                .frame(width: 26, height: 26)
                .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .disabled(features.findText.isEmpty && symbol != "xmark")
        .help(label)
        .accessibilityLabel(label)
    }
}

// MARK: - Permission prompt

private struct PermissionPromptCard: View {
    @ObservedObject var features: PageFeatures
    let prompt: PermissionPrompt
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: symbol)
                    .font(.system(size: 18))
                    .foregroundStyle(Color.accentColor)
                    .frame(width: 24)
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 4) {
                    Text(prompt.request.sentence)
                        .font(.headline)
                        .fixedSize(horizontal: false, vertical: true)
                    ForEach(prompt.notes, id: \.self) { note in
                        Text(note)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                Spacer(minLength: 0)
                Button {
                    features.answer(prompt, with: nil)
                } label: {
                    Image(systemName: "xmark").font(.system(size: 10, weight: .bold)).frame(width: 20, height: 20)
                }
                .buttonStyle(.borderless)
                .help("Decide Later (Esc)")
                .accessibilityLabel("Decide later")
            }
            HStack(spacing: 8) {
                Spacer()
                Button("Block") { features.answer(prompt, with: .block) }
                    .accessibilityHint(prompt.request.isRememberable ? "Mosaic remembers this for the site" : "")
                Button("Allow") { features.answer(prompt, with: .allow) }
                    .focused($focused)
                    .accessibilityHint(prompt.request.isRememberable ? "Mosaic remembers this for the site" : "")
            }
            .controlSize(.regular)
        }
        .frame(width: 340, alignment: .leading)
        .overlayCard()
        .onExitCommand { features.answer(prompt, with: nil) }
        .onAppear { focused = true }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Permission request: \(prompt.request.sentence)")
    }

    private var symbol: String {
        let kinds = Set(prompt.request.kinds)
        if kinds.contains(.camera) { return "video.fill" }
        if kinds.contains(.microphone) { return "mic.fill" }
        if kinds.contains(.screen) { return "rectangle.inset.filled.on.rectangle" }
        if kinds.contains(.location) { return "location.fill" }
        if kinds.contains(.notifications) { return "bell.fill" }
        if kinds.contains(.clipboard) { return "doc.on.clipboard" }
        return "hand.raised.fill"
    }
}

// MARK: - Blocked pop-ups

private struct PopupNoticeCard: View {
    @ObservedObject var features: PageFeatures
    let notice: PopupNotice

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: "macwindow.badge.plus").foregroundStyle(.secondary).accessibilityHidden(true)
                Text(notice.count == 1 ? "Pop-up blocked" : "\(notice.count) pop-ups blocked").font(.headline)
                Spacer(minLength: 0)
                Button {
                    features.dismissPopups(notice)
                } label: {
                    Image(systemName: "xmark").font(.system(size: 10, weight: .bold)).frame(width: 20, height: 20)
                }
                .buttonStyle(.borderless)
                .help("Dismiss (Esc)")
                .accessibilityLabel("Dismiss")
            }
            if let first = notice.urls.first {
                Text(first)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .textSelection(.enabled)
            }
            HStack(spacing: 8) {
                Button(notice.urls.count > 1 ? "Open All" : "Open") { features.openPopups(notice, alwaysAllow: false) }
                    .disabled(notice.urls.isEmpty && notice.engine == .chromium)
                if let site = notice.site {
                    Button("Always Allow on \(SitePermissionStore.displayName(forSite: site))") {
                        features.openPopups(notice, alwaysAllow: true)
                    }
                    .lineLimit(1)
                }
            }
        }
        .frame(width: 300, alignment: .leading)
        .overlayCard()
        .onExitCommand { features.dismissPopups(notice) }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(notice.count == 1 ? "Pop-up blocked" : "\(notice.count) pop-ups blocked")
    }
}

// MARK: - JavaScript dialogs (Chromium)

private struct JavaScriptDialogCard: View {
    @ObservedObject var features: PageFeatures
    let item: PageDialog
    @State private var text = ""
    @FocusState private var focus: Field?

    enum Field { case text, ok }

    var body: some View {
        let dialog = item.dialog
        VStack(alignment: .leading, spacing: 12) {
            Text(dialog.origin.isEmpty ? "This page says" : "\(dialog.origin) says")
                .font(.headline)
            if !dialog.message.isEmpty {
                ScrollView {
                    Text(dialog.message)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                }
                .frame(maxHeight: 220)
                .fixedSize(horizontal: false, vertical: true)
            }
            if dialog.kind == .prompt {
                TextField("", text: $text)
                    .textFieldStyle(.roundedBorder)
                    .focused($focus, equals: .text)
                    .onSubmit { features.finish(item, accepted: true, text: text) }
                    .accessibilityLabel("Answer")
            }
            HStack(spacing: 8) {
                Spacer()
                if dialog.kind != .alert {
                    Button(dialog.kind == .beforeUnload ? "Stay" : "Cancel") {
                        features.finish(item, accepted: false)
                    }
                    .keyboardShortcut(.cancelAction)
                }
                Button(dialog.kind == .beforeUnload ? "Leave" : "OK") {
                    features.finish(item, accepted: true, text: dialog.kind == .prompt ? text : nil)
                }
                .keyboardShortcut(.defaultAction)
                .focused($focus, equals: .ok)
            }
        }
        .frame(width: 420, alignment: .leading)
        .overlayCard()
        .onExitCommand { features.finish(item, accepted: false) }
        .onAppear {
            text = dialog.defaultText
            focus = dialog.kind == .prompt ? .text : .ok
        }
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(.isModal)
        .accessibilityLabel(dialog.origin.isEmpty ? "Dialog from this page" : "Dialog from \(dialog.origin)")
    }
}
