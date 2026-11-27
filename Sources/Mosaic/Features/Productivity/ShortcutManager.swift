import AppKit
import Combine
import MosaicCore

/// Customisable keyboard shortcuts for every command in the main menu.
///
/// The menu is the source of truth for which commands exist and what their default
/// shortcuts are: it is scanned at launch (and again whenever items are added or the menu
/// is replaced), so commands other areas add to the menu appear here without registering.
/// The user's changes (`ShortcutBindings`) are stored in the profile's preferences and
/// written back onto the menu items. Shortcuts pressed while a Gecko page has focus are
/// forwarded by the agent (`setForwardedShortcuts`).
@MainActor
final class ShortcutManager: ObservableObject {
    @Published private(set) var commands: [ShortcutCommand] = []
    @Published private(set) var bindings: ShortcutBindings

    private unowned let model: BrowserModel
    private let store = ShortcutStore(defaults: AppPaths.defaults)
    private var items: [String: NSMenuItem] = [:]
    /// Default shortcut of each command, captured the first time it was seen (before the
    /// user's binding was applied to it).
    private var defaults: [String: KeyCombo?] = [:]
    private var observers: [NSObjectProtocol] = []
    private var mainMenuObservation: NSKeyValueObservation?
    private var geckoStatus: AnyCancellable?
    private var rescanScheduled = false
    private var applying = false

    init(model: BrowserModel) {
        self.model = model
        bindings = store.load()
    }

    func start() {
        rescan()
        // Items added later (by any area) or a whole new main menu: scan again.
        observers.append(NotificationCenter.default.addObserver(
            forName: NSMenu.didAddItemNotification, object: nil, queue: .main
        ) { [weak self] _ in MainActor.assumeIsolated { self?.scheduleRescan() } })
        mainMenuObservation = NSApp.observe(\.mainMenu, options: [.new]) { [weak self] _, _ in
            MainActor.assumeIsolated { self?.scheduleRescan() }
        }
        geckoStatus = model.gecko.$status.removeDuplicates().sink { [weak self] status in
            guard status == .running else { return }
            // After `status` has been set (the publisher fires before the change lands).
            DispatchQueue.main.async { self?.pushToGecko() }
        }
    }

    // MARK: Reading the menu

    private func scheduleRescan() {
        guard !rescanScheduled, !applying else { return }
        rescanScheduled = true
        DispatchQueue.main.async { [weak self] in
            self?.rescanScheduled = false
            self?.rescan()
        }
    }

    /// Collects the menu's commands and applies the user's bindings to them.
    func rescan() {
        guard let menu = NSApp.mainMenu else { return }
        var found: [ShortcutCommand] = []
        var foundItems: [String: NSMenuItem] = [:]
        walk(menu, path: [], into: &found, items: &foundItems)
        items = foundItems
        commands = found
        apply()
    }

    private func walk(_ menu: NSMenu, path: [String], into commands: inout [ShortcutCommand],
                      items: inout [String: NSMenuItem]) {
        for item in menu.items where !item.isSeparatorItem && !item.isHidden {
            if let submenu = item.submenu {
                // The Services menu belongs to the system.
                guard submenu !== NSApp.servicesMenu, !item.title.isEmpty else { continue }
                walk(submenu, path: path + [item.title], into: &commands, items: &items)
                continue
            }
            guard let action = item.action, !Self.systemActions.contains(NSStringFromSelector(action)),
                  !NSStringFromSelector(action).hasPrefix("_"), !item.title.isEmpty, let group = path.first else { continue }
            var id = Self.identifier(of: item, path: path)
            // Items sharing an identifier (AppKit derives it from the action) fall back to their path.
            if items[id] != nil { id = (path + [item.title]).joined(separator: "/") }
            guard items[id] == nil else { continue }
            if defaults[id] == nil {
                defaults[id] = .some(Self.combo(of: item))
            }
            let groupName = path.count > 1 ? path.joined(separator: " › ") : group
            commands.append(ShortcutCommand(id: id, title: item.title, group: groupName, defaultCombo: defaults[id] ?? nil))
            items[id] = item
        }
    }

    /// Items AppKit adds to menus by itself, which are not Mosaic's to rebind.
    private static let systemActions: Set<String> = [
        "startDictation:", "orderFrontCharacterPalette:", "makeKeyAndOrderFront:", "toggleFullScreen:",
        "toggleTabBar:", "toggleTabOverview:", "mergeAllWindows:", "moveTabToNewWindow:",
        "selectNextTab:", "selectPreviousTab:", "arrangeInFront:", "_handleInsertFromContactsCommand:",
        "_handleInsertFromPasswordsCommand:", "showWritingTools:", "submenuAction:",
    ]

    /// A stable id: the item's identifier (set by the area, or derived by AppKit from the
    /// action, e.g. "newTab"), else "Menu/Title".
    static func identifier(of item: NSMenuItem, path: [String]) -> String {
        if let identifier = item.identifier?.rawValue, !identifier.isEmpty, !identifier.hasPrefix("_NS") {
            return identifier
        }
        return (path + [item.title]).joined(separator: "/")
    }

    static func combo(of item: NSMenuItem) -> KeyCombo? {
        guard !item.keyEquivalent.isEmpty else { return nil }
        return KeyCombo(key: item.keyEquivalent, modifiers: modifiers(from: item.keyEquivalentModifierMask))
    }

    static func modifiers(from flags: NSEvent.ModifierFlags) -> KeyCombo.Modifiers {
        var modifiers: KeyCombo.Modifiers = []
        if flags.contains(.command) { modifiers.insert(.command) }
        if flags.contains(.shift) { modifiers.insert(.shift) }
        if flags.contains(.option) { modifiers.insert(.option) }
        if flags.contains(.control) { modifiers.insert(.control) }
        return modifiers
    }

    static func flags(from modifiers: KeyCombo.Modifiers) -> NSEvent.ModifierFlags {
        var flags: NSEvent.ModifierFlags = []
        if modifiers.contains(.command) { flags.insert(.command) }
        if modifiers.contains(.shift) { flags.insert(.shift) }
        if modifiers.contains(.option) { flags.insert(.option) }
        if modifiers.contains(.control) { flags.insert(.control) }
        return flags
    }

    // MARK: Applying

    /// Writes the effective shortcut onto every menu item and tells Gecko.
    private func apply() {
        applying = true
        defer { applying = false }
        for command in commands {
            guard let item = items[command.id] else { continue }
            let combo = bindings.combo(for: command)
            if Self.combo(of: item) != combo {
                item.keyEquivalent = combo?.key ?? ""
                item.keyEquivalentModifierMask = combo.map { Self.flags(from: $0.modifiers) } ?? []
            }
        }
        pushToGecko()
    }

    private func changed() {
        store.save(bindings)
        apply()
        objectWillChange.send()
    }

    // MARK: Queries

    func combo(for command: ShortcutCommand) -> KeyCombo? { bindings.combo(for: command) }

    func command(id: String) -> ShortcutCommand? { commands.first { $0.id == id } }

    /// What the menu item itself carries now (for checking that changes were applied).
    func menuCombo(for commandID: String) -> KeyCombo? { items[commandID].flatMap(Self.combo(of:)) }

    /// The current shortcut of the menu command whose action is `selector` ("newTab").
    func combo(forSelector selector: String) -> KeyCombo? {
        let action = NSSelectorFromString(selector)
        guard let id = items.first(where: { $0.value.action == action })?.key, let command = command(id: id) else { return nil }
        return bindings.combo(for: command)
    }

    func problem(with combo: KeyCombo, for command: ShortcutCommand) -> ShortcutBindings.Problem? {
        bindings.problem(with: combo, assigningTo: command.id, among: commands)
    }

    func title(of commandID: String) -> String {
        command(id: commandID).map { "\($0.group) › \($0.title)" } ?? commandID
    }

    // MARK: Changes

    /// Gives `combo` to `command`, taking it from any command that had it.
    func assign(_ combo: KeyCombo, to command: ShortcutCommand) {
        bindings.assign(combo, to: command, among: commands)
        changed()
    }

    /// Removes the command's shortcut.
    func clear(_ command: ShortcutCommand) {
        bindings.set(nil, for: command)
        changed()
    }

    func reset(_ command: ShortcutCommand) {
        bindings.reset(command)
        changed()
    }

    func resetAll() {
        bindings.resetAll()
        changed()
    }

    /// Runs a command as if its menu item had been chosen.
    func perform(commandID: String) {
        guard let item = items[commandID], let menu = item.menu else { return }
        let index = menu.index(of: item)
        guard index >= 0 else { return }
        if let validator = item.target as? NSMenuItemValidation, !validator.validateMenuItem(item) { return }
        menu.performActionForItem(at: index)
    }

    // MARK: Gecko

    /// Chords the agent forwards by itself, and the command each stands for.
    private static let agentBuiltIns: [String: String] = [
        "accel+l": "focusAddress", "accel+t": "newTab", "accel+n": "newTab", "accel+w": "closeTab",
        "accel+shift+e": "toggleEngine", "accel+shift+b": "toggleBlocker",
    ]

    /// The forwarding table for the agent: every Mosaic command with a shortcut (standard
    /// editing commands, which have no target, stay with the page), minus what the agent
    /// already forwards correctly; built-in chords the user moved elsewhere are swallowed.
    func geckoForwardingPairs() -> [[String]] {
        var pairs: [String: String] = [:]
        for command in commands {
            guard let item = items[command.id], item.target != nil, let combo = bindings.combo(for: command) else { continue }
            let chord = Self.chord(for: combo)
            if let builtIn = Self.agentBuiltIns[chord], item.action == NSSelectorFromString(builtIn) { continue }
            if pairs[chord] == nil { pairs[chord] = "productivity.shortcut:\(command.id)" }
        }
        for chord in Self.agentBuiltIns.keys where pairs[chord] == nil {
            let selector = NSSelectorFromString(Self.agentBuiltIns[chord]!)
            let stillBound = commands.contains { command in
                items[command.id]?.action == selector && bindings.combo(for: command).map(Self.chord(for:)) == chord
            }
            // ⌘N has no menu command in Mosaic; the agent maps it to New Tab, keep that.
            if !stillBound, chord != "accel+n" { pairs[chord] = "productivity.none" }
        }
        return pairs.sorted { $0.key < $1.key }.map { [$0.key, $0.value] }
    }

    /// The agent's chord format: "accel+ctrl+alt+shift+<KeyboardEvent.key>".
    static func chord(for combo: KeyCombo) -> String {
        var parts: [String] = []
        if combo.modifiers.contains(.command) { parts.append("accel") }
        if combo.modifiers.contains(.control) { parts.append("ctrl") }
        if combo.modifiers.contains(.option) { parts.append("alt") }
        if combo.modifiers.contains(.shift) { parts.append("shift") }
        let key = combo.domKey
        parts.append(key.count == 1 ? key.lowercased() : key)
        return parts.joined(separator: "+")
    }

    private func pushToGecko() {
        guard model.gecko.isRunning else { return }
        model.gecko.send("setForwardedShortcuts", geckoForwardingPairs())
    }
}

extension KeyCombo {
    /// The shortcut a key press would trigger, for recording.
    @MainActor
    init?(event: NSEvent) {
        guard var characters = event.charactersIgnoringModifiers, !characters.isEmpty else { return nil }
        if characters == "\u{19}" { characters = "\t" }  // Shift-Tab arrives as back-tab
        if characters == "\u{3}" { characters = "\r" }   // Enter on the keypad
        self.init(key: characters, modifiers: ShortcutManager.modifiers(from: event.modifierFlags))
    }
}
