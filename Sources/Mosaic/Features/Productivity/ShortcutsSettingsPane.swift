import AppKit
import MosaicCore
import SwiftUI

/// Settings › Shortcuts & Gestures: every menu command with its default and current
/// shortcut; click a shortcut to record a new one.
struct ShortcutsSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        ShortcutsList(shortcuts: model.productivity.shortcuts)
    }
}

private struct ShortcutsList: View {
    @ObservedObject var shortcuts: ShortcutManager
    @State private var filter = ""
    @State private var recording: String?
    @State private var pending: Pending?
    @State private var message: (commandID: String, text: String)?
    @State private var confirmResetAll = false

    /// A recorded shortcut that is already used elsewhere, awaiting "Replace".
    struct Pending: Equatable {
        let commandID: String
        let combo: KeyCombo
        let conflicts: [String]
    }

    var body: some View {
        Form {
            Section {
                Text("Every menu command is listed with its default shortcut. Click a shortcut to change it, then press the new keys: Esc cancels, Delete removes the shortcut. A shortcut needs Command or Control (function keys work alone). Changed shortcuts also work while a Mozilla page has focus.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack {
                    TextField("Filter", text: $filter, prompt: Text("Filter commands or keys"))
                        .textFieldStyle(.roundedBorder)
                        .accessibilityLabel("Filter shortcuts")
                    Button("Restore All Defaults…") { confirmResetAll = true }
                        .disabled(shortcuts.bindings.overrides.isEmpty)
                }
            }
            ForEach(groups, id: \.name) { group in
                Section(group.name) {
                    ForEach(group.commands) { command in
                        row(command)
                    }
                }
            }
            Section("Mouse gestures") {
                Text("Mouse gestures are not available yet.")
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .confirmationDialog("Restore every shortcut to its default?", isPresented: $confirmResetAll) {
            Button("Restore Defaults", role: .destructive) {
                shortcuts.resetAll()
                pending = nil
                message = nil
            }
        }
        .background {
            if recording != nil {
                ShortcutRecorder { result in record(result) }
            }
        }
    }

    private var groups: [(name: String, commands: [ShortcutCommand])] {
        let query = filter.trimmingCharacters(in: .whitespaces).lowercased()
        var order: [String] = []
        var byGroup: [String: [ShortcutCommand]] = [:]
        for command in shortcuts.commands {
            if !query.isEmpty {
                let current = shortcuts.combo(for: command)?.displayString.lowercased() ?? ""
                let haystack = "\(command.group) \(command.title)".lowercased()
                guard haystack.contains(query) || current.contains(query) || PaletteMatcher.score(query: query, title: command.title) != nil
                else { continue }
            }
            if byGroup[command.group] == nil { order.append(command.group) }
            byGroup[command.group, default: []].append(command)
        }
        return order.map { ($0, byGroup[$0]!) }
    }

    @ViewBuilder private func row(_ command: ShortcutCommand) -> some View {
        let current = shortcuts.combo(for: command)
        let customised = shortcuts.bindings.isCustomised(command)
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                Text(command.title)
                Spacer()
                // The defaults table: every row names its default.
                Text(command.defaultCombo.map { "Default \($0.displayString)" } ?? "No default")
                    .font(.caption)
                    .foregroundStyle(customised ? .secondary : .tertiary)
                recorderButton(command, current: current)
                Button {
                    shortcuts.reset(command)
                    clearState(for: command)
                } label: {
                    Image(systemName: "arrow.uturn.backward.circle")
                }
                .buttonStyle(.borderless)
                .help("Restore default (\(command.defaultCombo?.displayString ?? "none"))")
                .accessibilityLabel("Restore default shortcut for \(command.title)")
                .opacity(customised ? 1 : 0)
                .disabled(!customised)
            }
            if let pending, pending.commandID == command.id {
                HStack {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.yellow)
                    Text("\(pending.combo.displayString) is used by \(pending.conflicts.map(shortcuts.title(of:)).joined(separator: ", ")).")
                    Spacer()
                    Button("Cancel") { self.pending = nil }
                    Button("Replace") {
                        shortcuts.assign(pending.combo, to: command)
                        self.pending = nil
                    }
                    .keyboardShortcut(.defaultAction)
                }
                .font(.callout)
            } else if let message, message.commandID == command.id {
                Text(message.text).font(.callout).foregroundStyle(.red)
            }
        }
    }

    private func recorderButton(_ command: ShortcutCommand, current: KeyCombo?) -> some View {
        let isRecording = recording == command.id
        return Button {
            pending = nil
            message = nil
            recording = isRecording ? nil : command.id
        } label: {
            Text(isRecording ? "Type shortcut…" : (current?.displayString ?? "None"))
                .font(.system(size: 12, weight: .medium, design: .rounded))
                .foregroundStyle(isRecording ? Color.accentColor : (current == nil ? Color.secondary : Color.primary))
                .frame(minWidth: 96)
                .padding(.vertical, 3)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(isRecording ? 0.0 : 0.06)))
                .overlay(RoundedRectangle(cornerRadius: 6)
                    .strokeBorder(isRecording ? Color.accentColor : Color.primary.opacity(0.15), lineWidth: isRecording ? 2 : 1))
        }
        .buttonStyle(.plain)
        .help(isRecording ? "Press the new shortcut (Esc cancels, Delete removes)" : "Change the shortcut for \(command.title)")
        .accessibilityLabel("Shortcut for \(command.title)")
        .accessibilityValue(isRecording ? "Recording" : (current?.spokenString ?? "None"))
    }

    private func clearState(for command: ShortcutCommand) {
        if pending?.commandID == command.id { pending = nil }
        if message?.commandID == command.id { message = nil }
    }

    private func record(_ result: ShortcutRecorder.Result) {
        guard let id = recording, let command = shortcuts.command(id: id) else { return }
        recording = nil
        switch result {
        case .cancelled:
            break
        case .cleared:
            shortcuts.clear(command)
        case .combo(let combo):
            switch shortcuts.problem(with: combo, for: command) {
            case nil:
                shortcuts.assign(combo, to: command)
            case .needsModifier:
                message = (id, "\(combo.displayString) can’t be a shortcut: add ⌘ or ⌃.")
            case .reserved(let reason):
                message = (id, "macOS uses \(combo.displayString) for \(reason).")
            case .conflict(let ids):
                pending = Pending(commandID: id, combo: combo, conflicts: ids)
            }
        }
    }
}

/// While present, captures the next key press in the app (before menus see it).
struct ShortcutRecorder: NSViewRepresentable {
    enum Result: Equatable {
        case combo(KeyCombo)
        case cleared
        case cancelled
    }

    let onResult: (Result) -> Void

    func makeNSView(context: Context) -> NSView {
        context.coordinator.start(onResult)
        return NSView()
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        context.coordinator.onResult = onResult
    }

    static func dismantleNSView(_ nsView: NSView, coordinator: Coordinator) {
        coordinator.stop()
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    @MainActor
    final class Coordinator {
        var onResult: ((Result) -> Void)?
        private var monitor: Any?

        func start(_ handler: @escaping (Result) -> Void) {
            onResult = handler
            monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .leftMouseDown]) { [weak self] event in
                guard let self else { return event }
                if event.type == .leftMouseDown {
                    // A click elsewhere ends recording; the click itself goes through.
                    DispatchQueue.main.async { self.onResult?(.cancelled) }
                    return event
                }
                let flags = event.modifierFlags.intersection([.command, .shift, .option, .control])
                if event.keyCode == 53, flags.isEmpty {
                    self.onResult?(.cancelled)
                } else if (event.keyCode == 51 || event.keyCode == 117), flags.isEmpty {
                    self.onResult?(.cleared)
                } else if let combo = KeyCombo(event: event) {
                    self.onResult?(.combo(combo))
                }
                return nil
            }
        }

        func stop() {
            if let monitor { NSEvent.removeMonitor(monitor) }
            monitor = nil
        }
    }
}
