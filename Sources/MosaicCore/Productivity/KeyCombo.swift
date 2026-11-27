import Foundation

/// A keyboard shortcut in the form AppKit menus use: a key equivalent plus modifiers.
///
/// Normalised so equal shortcuts compare equal however they were written: letters are
/// stored lower-case with `.shift` explicit ("K" becomes shift + "k"), and a symbol that
/// already implies Shift ("}", "?", …) is stored without it, because AppKit treats
/// "⌘}" and "⇧⌘}" as the same key equivalent.
public struct KeyCombo: Hashable, Codable, Sendable, CustomStringConvertible {
    public struct Modifiers: OptionSet, Hashable, Codable, Sendable {
        public let rawValue: Int
        public init(rawValue: Int) { self.rawValue = rawValue }

        public static let command = Modifiers(rawValue: 1 << 0)
        public static let shift = Modifiers(rawValue: 1 << 1)
        public static let option = Modifiers(rawValue: 1 << 2)
        public static let control = Modifiers(rawValue: 1 << 3)
    }

    /// The key equivalent: a lower-case letter, digit or symbol, or one of the special
    /// characters in `SpecialKey`.
    public let key: String
    public let modifiers: Modifiers

    public init(key: String, modifiers: Modifiers) {
        var key = key
        var modifiers = modifiers
        if key == "\u{7f}" { key = SpecialKey.delete }  // the ⌫ key's character → its key equivalent
        if key.count == 1, let character = key.first {
            if character.isLetter, character.isUppercase {
                key = key.lowercased()
                modifiers.insert(.shift)
            } else if Self.shiftedSymbols.contains(character) {
                modifiers.remove(.shift)
            }
        }
        self.key = key
        self.modifiers = modifiers
    }

    /// Characters typed with Shift on a US layout; Shift is implied by the character.
    static let shiftedSymbols = Set("~!@#$%^&*()_+{}|:\"<>?")

    /// Special keys, as AppKit key-equivalent characters.
    public enum SpecialKey {
        public static let up = "\u{F700}", down = "\u{F701}", left = "\u{F702}", right = "\u{F703}"
        public static let delete = "\u{8}", forwardDelete = "\u{F728}"
        public static let home = "\u{F729}", end = "\u{F72B}", pageUp = "\u{F72C}", pageDown = "\u{F72D}"
        public static let returnKey = "\r", tab = "\t", space = " ", escape = "\u{1b}"
        /// F1 is U+F704; F2… follow.
        public static func function(_ number: Int) -> String { String(UnicodeScalar(0xF704 + number - 1)!) }

        static let names: [String: (symbol: String, word: String, dom: String)] = [
            up: ("↑", "up", "ArrowUp"), down: ("↓", "down", "ArrowDown"),
            left: ("←", "left", "ArrowLeft"), right: ("→", "right", "ArrowRight"),
            delete: ("⌫", "delete", "Backspace"), forwardDelete: ("⌦", "forwarddelete", "Delete"),
            home: ("↖", "home", "Home"), end: ("↘", "end", "End"),
            pageUp: ("⇞", "pageup", "PageUp"), pageDown: ("⇟", "pagedown", "PageDown"),
            returnKey: ("↩", "return", "Enter"), tab: ("⇥", "tab", "Tab"),
            space: ("Space", "space", " "), escape: ("⎋", "escape", "Escape"),
        ]

        /// 1…35 for F1…F35, else nil.
        static func functionNumber(_ key: String) -> Int? {
            guard key.unicodeScalars.count == 1, let scalar = key.unicodeScalars.first,
                  (0xF704...0xF726).contains(scalar.value) else { return nil }
            return Int(scalar.value) - 0xF704 + 1
        }
    }

    public var isFunctionKey: Bool { SpecialKey.functionNumber(key) != nil }

    /// The key alone, as menus print it ("K", "↩", "F5").
    public var keyDisplay: String {
        if let number = SpecialKey.functionNumber(key) { return "F\(number)" }
        if let name = SpecialKey.names[key] { return name.symbol }
        return key.uppercased()
    }

    /// "⌃⌥⇧⌘K": modifiers in the order macOS menus use.
    public var displayString: String {
        var text = ""
        if modifiers.contains(.control) { text += "⌃" }
        if modifiers.contains(.option) { text += "⌥" }
        if modifiers.contains(.shift) { text += "⇧" }
        if modifiers.contains(.command) { text += "⌘" }
        return text + keyDisplay
    }

    public var description: String { displayString }

    /// Words for VoiceOver ("Command Shift K").
    public var spokenString: String {
        var words: [String] = []
        if modifiers.contains(.control) { words.append("Control") }
        if modifiers.contains(.option) { words.append("Option") }
        if modifiers.contains(.shift) { words.append("Shift") }
        if modifiers.contains(.command) { words.append("Command") }
        if let name = SpecialKey.names[key] { words.append(name.word.capitalized) } else { words.append(keyDisplay) }
        return words.joined(separator: " ")
    }

    /// A shortcut must use Command or Control (Option alone types characters, Shift alone
    /// types capitals), unless it is a function key.
    public var hasRequiredModifier: Bool {
        isFunctionKey || modifiers.contains(.command) || modifiers.contains(.control)
    }

    // MARK: Text form ("cmd+shift+k"), for automation and settings files

    /// Parses "cmd+shift+k", "ctrl+opt+f5", "cmd+up", "cmd+}" (case-insensitive words).
    public init?(string: String) {
        let parts = string.split(separator: "+", omittingEmptySubsequences: false).map(String.init)
        // "cmd++" means the plus key.
        var tokens = parts
        if string.hasSuffix("++") { tokens = Array(parts.dropLast(2)) + ["+"] }
        guard let last = tokens.last, !last.isEmpty else { return nil }
        var modifiers: Modifiers = []
        for word in tokens.dropLast() {
            switch word.lowercased() {
            case "cmd", "command", "⌘": modifiers.insert(.command)
            case "shift", "⇧": modifiers.insert(.shift)
            case "opt", "option", "alt", "⌥": modifiers.insert(.option)
            case "ctrl", "control", "⌃": modifiers.insert(.control)
            default: return nil
            }
        }
        let lower = last.lowercased()
        let key: String
        if last.count == 1 {
            key = last
        } else if let special = SpecialKey.names.first(where: { $0.value.word == lower })?.key {
            key = special
        } else if lower.hasPrefix("f"), let number = Int(lower.dropFirst()), (1...35).contains(number) {
            key = SpecialKey.function(number)
        } else {
            return nil
        }
        self.init(key: key, modifiers: modifiers)
    }

    /// Inverse of `init(string:)`.
    public var stringValue: String {
        var parts: [String] = []
        if modifiers.contains(.control) { parts.append("ctrl") }
        if modifiers.contains(.option) { parts.append("opt") }
        if modifiers.contains(.shift) { parts.append("shift") }
        if modifiers.contains(.command) { parts.append("cmd") }
        if let number = SpecialKey.functionNumber(key) {
            parts.append("f\(number)")
        } else if let name = SpecialKey.names[key] {
            parts.append(name.word)
        } else {
            parts.append(key)
        }
        return parts.joined(separator: "+")
    }

    /// The key as a DOM `KeyboardEvent.key` value (lower-case for letters), for engines
    /// that forward shortcuts pressed inside a page.
    public var domKey: String {
        if let number = SpecialKey.functionNumber(key) { return "F\(number)" }
        if let name = SpecialKey.names[key] { return name.dom }
        return key
    }

    // MARK: Codable as the text form

    public init(from decoder: Decoder) throws {
        let text = try decoder.singleValueContainer().decode(String.self)
        guard let combo = KeyCombo(string: text) else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "bad shortcut \(text)"))
        }
        self = combo
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(stringValue)
    }

    /// Shortcuts macOS keeps for itself; assigning one would never reach Mosaic.
    public static let systemReserved: [KeyCombo: String] = {
        var reserved: [KeyCombo: String] = [
            KeyCombo(key: SpecialKey.tab, modifiers: .command): "switching apps",
            KeyCombo(key: SpecialKey.tab, modifiers: [.command, .shift]): "switching apps",
            KeyCombo(key: SpecialKey.space, modifiers: .command): "Spotlight",
            KeyCombo(key: SpecialKey.space, modifiers: [.command, .option]): "Finder search",
            KeyCombo(key: SpecialKey.space, modifiers: .control): "switching input sources",
            KeyCombo(key: SpecialKey.space, modifiers: [.control, .command]): "the Character Viewer",
            KeyCombo(key: "`", modifiers: .command): "switching windows",
            KeyCombo(key: SpecialKey.escape, modifiers: [.command, .option]): "Force Quit",
            KeyCombo(key: "q", modifiers: [.command, .control]): "locking the screen",
        ]
        for key in ["3", "4", "5"] {
            reserved[KeyCombo(key: key, modifiers: [.command, .shift])] = "screenshots"
        }
        for key in [SpecialKey.up, SpecialKey.down, SpecialKey.left, SpecialKey.right] {
            reserved[KeyCombo(key: key, modifiers: .control)] = "Mission Control"
        }
        return reserved
    }()
}
