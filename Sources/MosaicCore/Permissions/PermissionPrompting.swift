import Foundation

/// What a page asked for, in engine-neutral terms, and how Mosaic words and decides it.
/// Owner: **engine**.
public struct PermissionRequestInfo: Equatable, Sendable {
    public enum Kind: String, CaseIterable, Sendable {
        case camera, microphone, location, notifications, clipboard
        /// Screen or tab sharing.
        case screen
        /// Something else, named in `otherNames`.
        case other

        /// The remembered permission this kind is stored as; nil for kinds that are asked
        /// every time.
        public var storedAs: SitePermissionStore.Permission? {
            switch self {
            case .camera: .camera
            case .microphone: .microphone
            case .location: .location
            case .notifications: .notifications
            case .clipboard: .clipboard
            case .screen, .other: nil
            }
        }
    }

    /// The requesting origin ("https://meet.example"); may be empty or "null" for pages
    /// without one (data: URLs).
    public var origin: String
    public var kinds: [Kind]
    /// Engine names of `other` permissions ("midi-sysex", "persistent-storage", …).
    public var otherNames: [String]

    public init(origin: String, kinds: [Kind], otherNames: [String] = []) {
        self.origin = origin
        self.kinds = kinds
        self.otherNames = otherNames
    }

    /// The site decisions are remembered for, or nil if the page has none. Engines always give
    /// an origin with a scheme; anything else ("null" for opaque origins) has no site.
    public var site: String? { origin.contains("://") ? SitePermissionStore.site(for: origin) : nil }

    /// True when every requested kind can be remembered for the site.
    public var isRememberable: Bool {
        site != nil && !kinds.isEmpty && kinds.allSatisfy { $0.storedAs != nil }
    }

    /// What to do without asking: allow if every kind is allowed for the site, deny if any
    /// is blocked, nil to ask.
    public func automaticDecision(in store: SitePermissionStore) -> SitePermissionStore.Decision? {
        guard isRememberable, let site else { return nil }
        let decisions = kinds.compactMap(\.storedAs).map { store.decision(for: $0, site: site) }
        if decisions.contains(.block) { return .block }
        if decisions.allSatisfy({ $0 == .allow }) { return .allow }
        return nil
    }

    /// "example.com wants to use your camera and microphone".
    public var sentence: String {
        let who = site.map(SitePermissionStore.displayName(forSite:)) ?? "This page"
        return "\(who) wants to \(Self.phrase(for: kinds, otherNames: otherNames))"
    }

    static func phrase(for kinds: [Kind], otherNames: [String]) -> String {
        var parts: [String] = []
        let set = Set(kinds)
        if set.contains(.camera) && set.contains(.microphone) {
            parts.append("use your camera and microphone")
        } else if set.contains(.camera) {
            parts.append("use your camera")
        } else if set.contains(.microphone) {
            parts.append("use your microphone")
        }
        if set.contains(.screen) { parts.append("share your screen") }
        if set.contains(.location) { parts.append("know your location") }
        if set.contains(.notifications) { parts.append("show notifications") }
        if set.contains(.clipboard) { parts.append("see text and images you copy") }
        if set.contains(.other) {
            let named = otherNames.map(otherPhrase)
            parts.append(contentsOf: named.isEmpty ? [otherPhrase("")] : named)
        }
        var unique: [String] = []
        for part in parts where !unique.contains(part) { unique.append(part) }
        switch unique.count {
        case 0: return otherPhrase("")
        case 1: return unique[0]
        default: return unique.dropLast().joined(separator: ", ") + " and " + unique.last!
        }
    }

    static func otherPhrase(_ name: String) -> String {
        switch name {
        case "midi", "midi-sysex": "control your MIDI devices"
        case "storage-access": "use its cookies while embedded in other sites"
        case "persistent-storage": "keep its data on this Mac permanently"
        case "multiple-downloads": "download multiple files"
        case "xr": "use virtual-reality devices"
        case "local-network", "loopback-network": "connect to devices on your local network"
        case "window-management": "place windows on all your displays"
        case "local-fonts": "use the fonts installed on this Mac"
        case "idle-detection": "know when you are using this Mac"
        case "keyboard-lock": "take over keyboard shortcuts"
        case "pointer-lock": "hide and lock your pointer"
        case "file-system": "edit files on this Mac"
        case "sensors": "use motion sensors"
        case "serial": "connect to serial devices"
        default: "use a feature that needs your permission"
        }
    }
}
