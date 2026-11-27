import Foundation

// Named configurations (a layout plus its appearance, saved under a name), their library,
// and `.json` export/import with validation. Importing never throws away the user's work
// and never crashes: unreadable files are refused with a reason, readable ones are repaired
// (unknown components dropped, numbers clamped, `usable` applied) and every repair is
// reported.
//
// Owner: **canvas**.

/// A saved layout + appearance.
public struct LayoutConfiguration: Codable, Hashable, Identifiable, Sendable {
    public var id: String
    public var name: String
    public var created: Date
    public var modified: Date
    public var layout: MosaicLayout

    public init(id: String = UUID().uuidString, name: String, created: Date = Date(), modified: Date = Date(),
                layout: MosaicLayout) {
        self.id = id
        self.name = name
        self.created = created
        self.modified = modified
        self.layout = layout
    }
}

/// The user's saved configurations, in the order they were made.
public struct ConfigurationLibrary: Codable, Equatable, Sendable {
    public static let maxConfigurations = 200
    public static let maxNameLength = 60

    public private(set) var configurations: [LayoutConfiguration] = []
    /// The configuration last applied or saved, while the window still shows it unchanged
    /// (the controller clears it when the layout is edited).
    public var activeID: String?

    public init(configurations: [LayoutConfiguration] = [], activeID: String? = nil) {
        self.configurations = configurations
        self.activeID = activeID
    }

    public func configuration(_ id: String) -> LayoutConfiguration? { configurations.first { $0.id == id } }

    /// A configuration by ID, or by name (ignoring case).
    public func find(_ key: String) -> LayoutConfiguration? {
        configuration(key) ?? configurations.first { $0.name.caseInsensitiveCompare(key) == .orderedSame }
    }

    /// `name` trimmed, without control characters, at most `maxNameLength` long; nil if empty.
    public static func cleanName(_ name: String) -> String? {
        let cleaned = String(String.UnicodeScalarView(name.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }))
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleaned.isEmpty else { return nil }
        return String(cleaned.prefix(maxNameLength))
    }

    /// `name`, or "name 2", "name 3"… so it is unique (ignoring `excluding`).
    public func uniqueName(_ name: String, excluding id: String? = nil) -> String {
        let base = Self.cleanName(name) ?? "Configuration"
        func taken(_ candidate: String) -> Bool {
            configurations.contains { $0.id != id && $0.name.caseInsensitiveCompare(candidate) == .orderedSame }
        }
        guard taken(base) else { return base }
        var n = 2
        while taken("\(base) \(n)") { n += 1 }
        return "\(base) \(n)"
    }

    /// Saves `layout` under `name` (made unique) and makes it the active configuration.
    @discardableResult
    public mutating func save(name: String, layout: MosaicLayout, now: Date = Date()) -> LayoutConfiguration? {
        guard configurations.count < Self.maxConfigurations else { return nil }
        var stored = LayoutCanvasRules.usable(layout)
        let unique = uniqueName(name)
        stored.name = unique
        let configuration = LayoutConfiguration(name: unique, created: now, modified: now, layout: stored)
        configurations.append(configuration)
        activeID = configuration.id
        return configuration
    }

    /// Overwrites a configuration's layout with `layout`.
    @discardableResult
    public mutating func update(_ id: String, layout: MosaicLayout, now: Date = Date()) -> Bool {
        guard let index = configurations.firstIndex(where: { $0.id == id }) else { return false }
        var stored = LayoutCanvasRules.usable(layout)
        stored.name = configurations[index].name
        configurations[index].layout = stored
        configurations[index].modified = now
        activeID = id
        return true
    }

    @discardableResult
    public mutating func rename(_ id: String, to name: String, now: Date = Date()) -> Bool {
        guard let index = configurations.firstIndex(where: { $0.id == id }), Self.cleanName(name) != nil else { return false }
        let unique = uniqueName(name, excluding: id)
        configurations[index].name = unique
        configurations[index].layout.name = unique
        configurations[index].modified = now
        return true
    }

    @discardableResult
    public mutating func delete(_ id: String) -> Bool {
        guard let index = configurations.firstIndex(where: { $0.id == id }) else { return false }
        configurations.remove(at: index)
        if activeID == id { activeID = nil }
        return true
    }

    // MARK: Persistence

    private enum CodingKeys: String, CodingKey { case configurations, activeID }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        // One unreadable entry must not lose the others.
        let entries = (try? c.decodeIfPresent([FailableConfiguration].self, forKey: .configurations)) ?? []
        configurations = entries.compactMap(\.value).map { entry in
            var entry = entry
            entry.layout = LayoutCanvasRules.usable(entry.layout)
            entry.name = Self.cleanName(entry.name) ?? "Configuration"
            return entry
        }
        activeID = try? c.decodeIfPresent(String.self, forKey: .activeID)
        if let id = activeID, configuration(id) == nil { activeID = nil }
    }

    private struct FailableConfiguration: Decodable {
        let value: LayoutConfiguration?
        init(from decoder: Decoder) throws { value = try? LayoutConfiguration(from: decoder) }
    }

    public static func load(from url: URL) -> ConfigurationLibrary {
        guard let data = try? Data(contentsOf: url) else { return ConfigurationLibrary() }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode(ConfigurationLibrary.self, from: data)) ?? ConfigurationLibrary()
    }

    public func write(to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(self)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
    }
}

// MARK: - Export and import

public enum LayoutTransfer {
    /// Identifies Mosaic layout files.
    public static let format = "dev.mosaic.layout"
    public static let version = 1
    /// Larger files are refused before parsing (a layout is a few kilobytes).
    public static let maxFileSize = 512 * 1024

    /// The exported file: a small envelope around the layout.
    public struct Document: Codable {
        public var format: String
        public var version: Int
        public var name: String
        public var exported: Date
        public var layout: MosaicLayout
    }

    public static func export(name: String, layout: MosaicLayout, now: Date = Date()) throws -> Data {
        var stored = LayoutCanvasRules.usable(layout)
        let clean = ConfigurationLibrary.cleanName(name) ?? stored.name
        stored.name = clean
        let document = Document(format: format, version: version, name: clean, exported: now, layout: stored)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(document)
    }

    public enum ImportError: Error, Equatable, LocalizedError {
        case empty
        case tooLarge(Int)
        case notJSON
        case notALayout

        public var errorDescription: String? {
            switch self {
            case .empty: "The file is empty."
            case .tooLarge(let bytes): "The file is too large to be a layout (\(bytes / 1024) KB; layouts are a few KB)."
            case .notJSON: "The file isn’t valid JSON."
            case .notALayout: "The file doesn’t contain a Mosaic layout."
            }
        }
    }

    public struct ImportReport: Equatable, Sendable {
        /// The repaired, usable layout.
        public var layout: MosaicLayout
        public var name: String
        /// What was dropped or repaired, for the user (empty when the file was clean).
        public var messages: [String]
    }

    /// Reads a layout file (exported by Mosaic, or a bare layout as written by the shell's
    /// `exportLayout`), repairing what it can and reporting every repair.
    public static func importLayout(_ data: Data) -> Result<ImportReport, ImportError> {
        guard !data.isEmpty else { return .failure(.empty) }
        guard data.count <= maxFileSize else { return .failure(.tooLarge(data.count)) }
        guard let root = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) else {
            return .failure(.notJSON)
        }
        guard let object = root as? [String: Any] else { return .failure(.notALayout) }

        var messages: [String] = []
        let raw: [String: Any]
        var name: String?
        if let layout = object["layout"] as? [String: Any] {
            raw = layout
            name = object["name"] as? String
            if let fmt = object["format"] as? String, fmt != format {
                messages.append("The file says it is “\(fmt)”, not a Mosaic layout; it was read as one anyway.")
            }
            if let v = object["version"] as? Int, v > version {
                messages.append("Written by a newer Mosaic: settings this version doesn’t know were ignored.")
            }
        } else if object["regions"] != nil || object["preset"] != nil || object["tabPlacement"] != nil {
            raw = object
        } else {
            return .failure(.notALayout)
        }

        // What the forgiving decoder would silently skip.
        if let preset = raw["preset"] as? String, MosaicPresetID(rawValue: preset) == nil {
            messages.append("Unknown preset “\(preset)”: started from Chrome.")
        }
        if let regions = raw["regions"] {
            if let dict = regions as? [String: Any] {
                var unknownComponents: [String] = []
                var unknownRegions: [String] = []
                var unreadable = false
                for (key, value) in dict.sorted(by: { $0.key < $1.key }) {
                    guard ShellRegion(rawValue: key) != nil else { unknownRegions.append(key); continue }
                    guard let names = value as? [Any] else { unreadable = true; continue }
                    for item in names {
                        guard let text = item as? String else { unreadable = true; continue }
                        if ShellComponent(rawValue: text) == nil { unknownComponents.append(text) }
                    }
                }
                if !unknownComponents.isEmpty {
                    messages.append("Dropped unknown components: \(unknownComponents.map { "“\($0)”" }.joined(separator: ", ")).")
                }
                if !unknownRegions.isEmpty {
                    messages.append("Ignored unknown regions: \(unknownRegions.map { "“\($0)”" }.joined(separator: ", ")).")
                }
                if unreadable {
                    messages.append("Some region entries weren’t lists of names and were skipped.")
                }
            } else {
                messages.append("The regions couldn’t be read; the preset’s arrangement was used.")
            }
        }

        // Decode the sanitised object (forgiving: unknown values fall back to the preset's).
        var sanitized = raw
        if let dict = raw["regions"] as? [String: Any] {
            var clean: [String: [String]] = [:]
            for (key, value) in dict {
                guard ShellRegion(rawValue: key) != nil, let names = value as? [Any] else { continue }
                clean[key] = names.compactMap { $0 as? String }.filter { ShellComponent(rawValue: $0) != nil }
            }
            sanitized["regions"] = clean
        }
        guard JSONSerialization.isValidJSONObject(sanitized),
              let cleanData = try? JSONSerialization.data(withJSONObject: sanitized),
              let decoded = try? JSONDecoder().decode(MosaicLayout.self, from: cleanData)
        else { return .failure(.notALayout) }

        let layout = LayoutCanvasRules.usable(decoded)
        messages += repairs(from: decoded, to: layout)
        let finalName = (name.flatMap(ConfigurationLibrary.cleanName)) ?? ConfigurationLibrary.cleanName(layout.name) ?? layout.preset.title
        var named = layout
        named.name = finalName
        return .success(ImportReport(layout: named, name: finalName, messages: messages))
    }

    /// Human descriptions of what `usable` changed.
    static func repairs(from decoded: MosaicLayout, to layout: MosaicLayout) -> [String] {
        var messages: [String] = []
        let before = ShellRegion.allCases.flatMap { region in decoded.components(in: region).filter(region.accepts) }
        let duplicates = Dictionary(grouping: before.filter { $0 != .flexibleSpace }, by: { $0 }).filter { $0.value.count > 1 }.keys
        if !duplicates.isEmpty {
            messages.append("Removed duplicates of: \(duplicates.map(\.title).sorted().joined(separator: ", ")).")
        }
        let misplaced = ShellRegion.allCases.flatMap { region in
            decoded.components(in: region).filter { !region.accepts($0) }
        }
        if !misplaced.isEmpty {
            messages.append("Removed components from rows that can’t hold them: \(Set(misplaced).map(\.title).sorted().joined(separator: ", ")).")
        }
        let added = Set(ShellComponent.allCases.filter { layout.contains($0) && !decoded.contains($0) })
        if added.contains(.addressField) { messages.append("Added the address field, which every layout needs.") }
        if added.contains(.tabStrip) { messages.append("Added the tab strip, which tabs on top need.") }
        if added.contains(.menu) { messages.append("Added the browser menu so Settings stays reachable.") }
        if added.contains(.sidebarToggle) { messages.append("Added the sidebar button, which a collapsible sidebar needs.") }
        if decoded.region(of: .addressField) != layout.region(of: .addressField), decoded.contains(.addressField) {
            messages.append("Moved the address field to the \(layout.region(of: .addressField)?.title ?? "toolbar") so it stays visible.")
        }

        func changed(_ a: Double, _ b: Double) -> Bool { !a.isFinite || abs(a - b) > 0.0001 }
        var clamped: [String] = []
        if changed(decoded.sidebarWidth, layout.sidebarWidth) { clamped.append("sidebar width") }
        let da = decoded.appearance, la = layout.appearance
        if changed(da.cornerRadius, la.cornerRadius) { clamped.append("corner radius") }
        if changed(da.contentInset, la.contentInset) { clamped.append("page inset") }
        if changed(da.contentCornerRadius, la.contentCornerRadius) { clamped.append("page corners") }
        if !clamped.isEmpty { messages.append("Brought out-of-range values back into range: \(clamped.joined(separator: ", ")).") }
        if da.accent != la.accent { messages.append("The accent colour “\(da.accent)” isn’t a colour; the system accent is used.") }
        if da.background != la.background || da.newTabBackground != la.newTabBackground
            || da.workspaceBackgrounds != la.workspaceBackgrounds {
            messages.append("Repaired the background (invalid colours removed, values brought into range).")
        }
        return messages
    }
}
