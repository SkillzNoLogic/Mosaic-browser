import Foundation

/// A command that can carry a keyboard shortcut (in Mosaic: one menu item).
public struct ShortcutCommand: Identifiable, Hashable, Sendable {
    /// Stable identifier; for menu items, "Menu/Item".
    public let id: String
    public let title: String
    /// Where it lives, e.g. "File" or "View › Mosaic".
    public let group: String
    /// The shortcut it comes with.
    public let defaultCombo: KeyCombo?

    public init(id: String, title: String, group: String, defaultCombo: KeyCombo?) {
        self.id = id
        self.title = title
        self.group = group
        self.defaultCombo = defaultCombo
    }
}

/// The user's changes to the default shortcuts, and the rules for making them.
public struct ShortcutBindings: Equatable, Codable, Sendable {
    /// One change: a different shortcut, or `combo == nil` for "no shortcut".
    public struct Override: Equatable, Codable, Sendable {
        public var combo: KeyCombo?
        public init(combo: KeyCombo?) { self.combo = combo }
    }

    public enum Problem: Equatable, Sendable {
        /// Needs Command or Control (or a function key).
        case needsModifier
        /// macOS uses it for `String`.
        case reserved(String)
        /// Already assigned to these commands (ids).
        case conflict([String])
    }

    public private(set) var overrides: [String: Override]

    public init(overrides: [String: Override] = [:]) {
        self.overrides = overrides
    }

    /// The shortcut in effect for `command`.
    public func combo(for command: ShortcutCommand) -> KeyCombo? {
        if let override = overrides[command.id] { return override.combo }
        return command.defaultCombo
    }

    public func isCustomised(_ command: ShortcutCommand) -> Bool {
        overrides[command.id] != nil
    }

    /// Commands other than `commandID` that currently use `combo`.
    public func conflicts(for combo: KeyCombo, assigningTo commandID: String,
                          among commands: [ShortcutCommand]) -> [ShortcutCommand] {
        commands.filter { $0.id != commandID && self.combo(for: $0) == combo }
    }

    /// Why `combo` cannot simply be given to `commandID`, or nil if it can.
    public func problem(with combo: KeyCombo, assigningTo commandID: String,
                        among commands: [ShortcutCommand]) -> Problem? {
        if !combo.hasRequiredModifier { return .needsModifier }
        if let reason = KeyCombo.systemReserved[combo] { return .reserved(reason) }
        let clashing = conflicts(for: combo, assigningTo: commandID, among: commands)
        return clashing.isEmpty ? nil : .conflict(clashing.map(\.id))
    }

    /// Gives `combo` (nil: none) to `command`. Setting a command back to its default
    /// removes the override rather than storing a copy of the default.
    public mutating func set(_ combo: KeyCombo?, for command: ShortcutCommand) {
        if combo == command.defaultCombo {
            overrides[command.id] = nil
        } else {
            overrides[command.id] = Override(combo: combo)
        }
    }

    /// Gives `combo` to `command` and takes it away from every other command using it.
    public mutating func assign(_ combo: KeyCombo, to command: ShortcutCommand, among commands: [ShortcutCommand]) {
        for other in conflicts(for: combo, assigningTo: command.id, among: commands) {
            set(nil, for: other)
        }
        set(combo, for: command)
    }

    public mutating func reset(_ command: ShortcutCommand) {
        overrides[command.id] = nil
    }

    public mutating func resetAll() {
        overrides.removeAll()
    }
}

/// Saves `ShortcutBindings` as JSON in a preferences domain.
public struct ShortcutStore {
    public static let defaultsKey = "productivity.shortcuts"
    private let defaults: UserDefaults
    private let key: String

    public init(defaults: UserDefaults, key: String = ShortcutStore.defaultsKey) {
        self.defaults = defaults
        self.key = key
    }

    public func load() -> ShortcutBindings {
        guard let data = defaults.data(forKey: key),
              let bindings = try? JSONDecoder().decode(ShortcutBindings.self, from: data)
        else { return ShortcutBindings() }
        return bindings
    }

    public func save(_ bindings: ShortcutBindings) {
        if bindings.overrides.isEmpty {
            defaults.removeObject(forKey: key)
        } else if let data = try? JSONEncoder().encode(bindings) {
            defaults.set(data, forKey: key)
        }
    }
}
