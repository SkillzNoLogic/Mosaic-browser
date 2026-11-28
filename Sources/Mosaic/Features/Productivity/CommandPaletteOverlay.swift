import AppKit
import MosaicCore
import SwiftUI

/// Mount point: the command palette (⌘K), drawn over the whole window.
/// Owner: **productivity**. The shell places it once, above everything else in the window
/// (`.overlay { CommandPaletteOverlay() }`), with `BrowserModel` in the environment. It draws
/// nothing and takes no clicks while the palette is closed.
struct CommandPaletteOverlay: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        PaletteHost(palette: model.productivity.palette)
    }
}

private struct PaletteHost: View {
    @ObservedObject var palette: CommandPalette
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ZStack(alignment: .top) {
            if palette.isPresented {
                // Dims the window (and the Gecko page behind it) and closes on a click outside.
                Color.black.opacity(0.18)
                    .ignoresSafeArea()
                    .contentShape(Rectangle())
                    .onTapGesture { palette.dismiss() }
                    .accessibilityHidden(true)
                PalettePanel(palette: palette)
                    .padding(.top, 64)
                    .padding(.horizontal, 24)
                    .transition(reduceMotion ? .opacity : .opacity.combined(with: .scale(scale: 0.98, anchor: .top)))
            }
        }
        .animation(reduceMotion ? nil : .easeOut(duration: 0.12), value: palette.isPresented)
    }
}

private struct PalettePanel: View {
    @ObservedObject var palette: CommandPalette
    @FocusState private var fieldFocused: Bool
    @State private var keyMonitor: Any?
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    var body: some View {
        VStack(spacing: 0) {
            searchField
            Divider().opacity(palette.results.isEmpty && palette.query.isEmpty ? 0 : 1)
            resultsList
            footer
        }
        .frame(maxWidth: 640)
        .background { panelBackground }
        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Color.primary.opacity(0.12)))
        .shadow(color: .black.opacity(0.25), radius: 30, y: 12)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Command palette")
        .accessibilityAddTraits(.isModal)
        .onAppear {
            installKeyMonitor()
            DispatchQueue.main.async { fieldFocused = true }
        }
        .onDisappear(perform: removeKeyMonitor)
    }

    @ViewBuilder private var panelBackground: some View {
        if reduceTransparency {
            Color(nsColor: .windowBackgroundColor)
        } else {
            Rectangle().fill(.regularMaterial)
        }
    }

    private var searchField: some View {
        HStack(spacing: 10) {
            Image(systemName: "command")
                .font(.system(size: 15, weight: .medium))
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            TextField("Search actions, tabs, bookmarks, history, settings", text: $palette.query)
                .textFieldStyle(.plain)
                .font(.system(size: 18))
                .focused($fieldFocused)
                .onSubmit { palette.performSelection() }
                .accessibilityLabel("Command palette search")
            if !palette.query.isEmpty {
                Button {
                    palette.query = ""
                } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary)
                }
                .buttonStyle(.plain)
                .help("Clear")
                .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 16)
        .frame(height: 54)
    }

    private var resultsList: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 2) {
                    ForEach(Array(palette.results.enumerated()), id: \.element.id) { index, action in
                        PaletteRow(action: action, isSelected: index == palette.selection)
                            .id(action.id)
                            .onTapGesture { palette.perform(action) }
                            .onHover { if $0 { palette.selection = index } }
                    }
                }
                .padding(6)
            }
            .frame(maxHeight: 400)
            .fixedSize(horizontal: false, vertical: true)
            .overlay {
                if palette.results.isEmpty, !palette.query.isEmpty {
                    Text("No matches").foregroundStyle(.secondary).padding(24)
                }
            }
            .onChange(of: palette.selection) { _, selection in
                guard palette.results.indices.contains(selection) else { return }
                proxy.scrollTo(palette.results[selection].id)
            }
        }
    }

    private var footer: some View {
        HStack(spacing: 14) {
            hint("↑↓", "Select")
            hint("↩", "Open")
            hint("esc", "Close")
            Spacer()
            if palette.results.count > 0 {
                Text("\(palette.results.count) result\(palette.results.count == 1 ? "" : "s")")
            }
        }
        .font(.system(size: 11))
        .foregroundStyle(.secondary)
        .padding(.horizontal, 14)
        .frame(height: 28)
        .background(Color.primary.opacity(0.04))
        .accessibilityHidden(true)
    }

    private func hint(_ key: String, _ label: String) -> some View {
        HStack(spacing: 4) {
            Text(key)
                .font(.system(size: 10, weight: .semibold, design: .rounded))
                .padding(.horizontal, 5)
                .padding(.vertical, 1)
                .background(RoundedRectangle(cornerRadius: 4).fill(Color.primary.opacity(0.08)))
            Text(label)
        }
    }

    /// Arrow keys, Return and Escape go to the palette whatever has focus inside it
    /// (the text field would otherwise move its caret).
    private func installKeyMonitor() {
        removeKeyMonitor()
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard palette.isPresented else { return event }
            let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
            switch event.keyCode {
            case 126: palette.moveSelection(-1); return nil                     // ↑
            case 125: palette.moveSelection(1); return nil                      // ↓
            case 36, 76: palette.performSelection(); return nil                 // ↩, enter
            case 53: palette.dismiss(); return nil                              // esc
            case 45 where flags == .control: palette.moveSelection(1); return nil   // ⌃N
            case 35 where flags == .control: palette.moveSelection(-1); return nil  // ⌃P
            default: return event
            }
        }
    }

    private func removeKeyMonitor() {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
    }
}

private struct PaletteRow: View {
    let action: PaletteAction
    let isSelected: Bool

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: action.symbol)
                .font(.system(size: 14, weight: .medium))
                .foregroundStyle(isSelected ? Color.white : tint)
                .frame(width: 30, height: 30)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(isSelected ? Color.white.opacity(0.18) : tint.opacity(0.14)))
            VStack(alignment: .leading, spacing: 1) {
                Text(action.title)
                    .font(.system(size: 14))
                    .lineLimit(1)
                if let subtitle = action.subtitle, !subtitle.isEmpty {
                    Text(subtitle)
                        .font(.system(size: 11.5))
                        .foregroundStyle(isSelected ? Color.white.opacity(0.8) : .secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
            }
            Spacer(minLength: 8)
            if let shortcut = action.shortcut {
                Text(shortcut.displayString)
                    .font(.system(size: 12, weight: .medium, design: .rounded))
                    .foregroundStyle(isSelected ? Color.white.opacity(0.9) : .secondary)
                    .accessibilityLabel(shortcut.spokenString)
            } else if let label = action.kind.label {
                Text(label)
                    .font(.system(size: 11))
                    .foregroundStyle(isSelected ? Color.white.opacity(0.8) : .secondary)
            }
        }
        .foregroundStyle(isSelected ? Color.white : Color.primary)
        .padding(.horizontal, 8)
        .frame(minHeight: 42)
        .background(RoundedRectangle(cornerRadius: 9, style: .continuous)
            .fill(isSelected ? Color.accentColor : Color.clear))
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }

    private var tint: Color {
        switch action.kind {
        case .action: .accentColor
        case .tab: .blue
        case .bookmark: .yellow
        case .history: .purple
        case .setting: .gray
        case .web: .green
        }
    }
}
