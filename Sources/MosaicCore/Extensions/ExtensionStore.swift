import Foundation

/// A Chrome extension that has been unpacked and converted for Gecko.
public struct InstalledExtension: Codable, Identifiable, Hashable, Sendable {
    /// Chrome extension ID (32 letters a–p).
    public let id: String
    /// Add-on ID the converted extension is registered under in Gecko.
    public let geckoID: String
    public var name: String
    public var version: String
    public var summary: String
    public var manifestVersion: Int
    public var enabled: Bool
    public var notes: [CompatibilityNote]
    public var installedAt: Date
    /// Path of the largest icon, relative to the converted extension's root.
    public var iconPath: String?
    public var hasPopup: Bool
    public var hasOptions: Bool

    public var worstSeverity: CompatibilityNote.Severity? {
        if notes.contains(where: { $0.severity == .blocker }) { return .blocker }
        if notes.contains(where: { $0.severity == .warning }) { return .warning }
        return notes.isEmpty ? nil : .info
    }
}

/// On-disk library of converted extensions.
///
///     <root>/index.json
///     <root>/<chrome id>/source.crx      original package, kept for the Chromium engine
///     <root>/<chrome id>/gecko/          unpacked, Gecko-ready copy
public final class ExtensionStore {
    public enum StoreError: Error, LocalizedError {
        case missingManifest
        case invalidManifest(String)
        case unzipFailed(String)

        public var errorDescription: String? {
            switch self {
            case .missingManifest: "The package has no manifest.json, so it is not an extension."
            case .invalidManifest(let why): "The extension's manifest.json could not be read: \(why)"
            case .unzipFailed(let why): "The extension package could not be unpacked: \(why)"
            }
        }
    }

    public let root: URL
    public private(set) var extensions: [InstalledExtension] = []

    private let fileManager = FileManager.default
    private var indexURL: URL { root.appendingPathComponent("index.json") }

    public init(root: URL) throws {
        self.root = root
        try fileManager.createDirectory(at: root, withIntermediateDirectories: true)
        if let data = try? Data(contentsOf: indexURL) {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            extensions = (try? decoder.decode([InstalledExtension].self, from: data)) ?? []
            extensions.removeAll { !fileManager.fileExists(atPath: geckoDirectory(for: $0.id).path) }
        }
    }

    public func directory(for id: String) -> URL { root.appendingPathComponent(id, isDirectory: true) }
    public func geckoDirectory(for id: String) -> URL {
        directory(for: id).appendingPathComponent("gecko", isDirectory: true)
    }
    public func sourcePackage(for id: String) -> URL { directory(for: id).appendingPathComponent("source.crx") }

    public func iconURL(for ext: InstalledExtension) -> URL? {
        ext.iconPath.map { geckoDirectory(for: ext.id).appendingPathComponent($0) }
    }

    /// Unpacks a `.crx` (or zipped extension), converts it for Gecko and records it.
    /// Installing an ID that already exists replaces it and keeps its enabled state.
    @discardableResult
    public func install(package data: Data, knownID: String? = nil) throws -> InstalledExtension {
        let archive = try CRXArchive.parse(data)

        // Unpack somewhere private first so a bad package never disturbs an installed one.
        let staging = root.appendingPathComponent(".staging-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fileManager.removeItem(at: staging) }

        let zipURL = staging.appendingPathComponent("payload.zip")
        let unpacked = staging.appendingPathComponent("gecko", isDirectory: true)
        try archive.zipData.write(to: zipURL)
        try unzip(zipURL, to: unpacked)

        let manifestURL = unpacked.appendingPathComponent("manifest.json")
        guard fileManager.fileExists(atPath: manifestURL.path) else { throw StoreError.missingManifest }
        let manifest = try Self.readManifest(at: manifestURL)

        let id = knownID
            ?? archive.extensionID
            ?? (manifest["key"] as? String).flatMap(CRXArchive.extensionID(fromManifestKey:))
            ?? Self.fallbackID(for: data)

        // Chrome's signature data; meaningless to Gecko.
        try? fileManager.removeItem(at: unpacked.appendingPathComponent("_metadata"))

        var result = ManifestConverter.convert(manifest, chromeID: id) { path in
            let url = unpacked.appendingPathComponent(path)
            guard url.standardizedFileURL.path.hasPrefix(unpacked.standardizedFileURL.path) else { return nil }
            return try? String(contentsOf: url, encoding: .utf8)
        }
        result.notes += scanScripts(in: unpacked)
        result.notes = Self.unique(result.notes)

        let converted = try JSONSerialization.data(
            withJSONObject: result.manifest, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        try converted.write(to: manifestURL)
        try Data(ManifestConverter.shimSource.utf8)
            .write(to: unpacked.appendingPathComponent(ManifestConverter.shimFileName))

        let messages = Self.localeMessages(in: unpacked, manifest: manifest)
        let settings = result.manifest["browser_specific_settings"] as? [String: Any]
        let geckoID = ((settings?["gecko"] as? [String: Any])?["id"] as? String)
            ?? ManifestConverter.geckoID(forChromeID: id)
        let action = (result.manifest["action"] ?? result.manifest["browser_action"]) as? [String: Any]

        let previous = extensions.first { $0.id == id }
        let record = InstalledExtension(
            id: id,
            geckoID: geckoID,
            name: Self.localized(manifest["name"] as? String, messages) ?? id,
            version: (manifest["version"] as? String) ?? "0",
            summary: Self.localized(manifest["description"] as? String, messages) ?? "",
            manifestVersion: (manifest["manifest_version"] as? Int) ?? 2,
            enabled: previous?.enabled ?? true,
            notes: result.notes,
            installedAt: Date(),
            iconPath: Self.largestIcon(in: manifest),
            hasPopup: (action?["default_popup"] as? String).map { !$0.isEmpty } ?? false,
            hasOptions: result.manifest["options_ui"] != nil)

        // Swap the finished copy into place.
        let home = directory(for: id)
        try? fileManager.removeItem(at: home)
        try fileManager.createDirectory(at: home, withIntermediateDirectories: true)
        try fileManager.moveItem(at: unpacked, to: geckoDirectory(for: id))
        try data.write(to: sourcePackage(for: id))

        extensions.removeAll { $0.id == id }
        extensions.append(record)
        extensions.sort { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        try save()
        return record
    }

    public func remove(id: String) throws {
        extensions.removeAll { $0.id == id }
        try? fileManager.removeItem(at: directory(for: id))
        try save()
    }

    public func setEnabled(_ enabled: Bool, id: String) throws {
        guard let index = extensions.firstIndex(where: { $0.id == id }) else { return }
        extensions[index].enabled = enabled
        try save()
    }

    private func save() throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(extensions).write(to: indexURL, options: .atomic)
    }

    // MARK: - Unpacking

    private func unzip(_ zip: URL, to destination: URL) throws {
        try fileManager.createDirectory(at: destination, withIntermediateDirectories: true)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        // -qq quiet, -o overwrite without prompting. unzip refuses "../" entries itself.
        process.arguments = ["-qq", "-o", zip.path, "-d", destination.path]
        let errors = Pipe()
        process.standardError = errors
        process.standardOutput = FileHandle.nullDevice
        try process.run()
        let errorData = errors.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        // Exit status 1 is "completed with warnings" (e.g. CRX padding bytes); the files are fine.
        guard process.terminationStatus <= 1 else {
            let message = String(data: errorData, encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            throw StoreError.unzipFailed(message.isEmpty ? "unzip exited with \(process.terminationStatus)" : message)
        }
    }

    /// Chrome tolerates `//` comments and a byte-order mark in manifest.json.
    static func readManifest(at url: URL) throws -> [String: Any] {
        var data = try Data(contentsOf: url)
        if data.starts(with: [0xEF, 0xBB, 0xBF]) { data.removeFirst(3) }
        do {
            let object = try JSONSerialization.jsonObject(with: data, options: [.json5Allowed])
            guard let manifest = object as? [String: Any] else {
                throw StoreError.invalidManifest("top level is not an object")
            }
            return manifest
        } catch let error as StoreError {
            throw error
        } catch {
            throw StoreError.invalidManifest(error.localizedDescription)
        }
    }

    private func scanScripts(in directory: URL) -> [CompatibilityNote] {
        guard let walker = fileManager.enumerator(
            at: directory, includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey]) else { return [] }
        var notes: [CompatibilityNote] = []
        for case let url as URL in walker where url.pathExtension == "js" {
            let values = try? url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
            guard values?.isRegularFile == true, (values?.fileSize ?? 0) < 8_000_000,
                  let source = try? String(contentsOf: url, encoding: .utf8) else { continue }
            notes += ManifestConverter.apiNotes(inScript: source)
        }
        return notes
    }

    // MARK: - Manifest helpers

    /// Resolves `__MSG_name__` placeholders against the extension's default locale.
    static func localized(_ value: String?, _ messages: [String: String]) -> String? {
        guard let value, !value.isEmpty else { return nil }
        guard value.hasPrefix("__MSG_"), value.hasSuffix("__"), value.count > 8 else { return value }
        let key = String(value.dropFirst(6).dropLast(2)).lowercased()
        return messages[key] ?? value
    }

    static func localeMessages(in directory: URL, manifest: [String: Any]) -> [String: String] {
        guard let locale = manifest["default_locale"] as? String else { return [:] }
        let url = directory.appendingPathComponent("_locales/\(locale)/messages.json")
        guard var data = try? Data(contentsOf: url) else { return [:] }
        if data.starts(with: [0xEF, 0xBB, 0xBF]) { data.removeFirst(3) }
        guard let object = try? JSONSerialization.jsonObject(with: data, options: [.json5Allowed]),
              let entries = object as? [String: Any] else { return [:] }
        var messages: [String: String] = [:]
        for (key, value) in entries {
            if let message = (value as? [String: Any])?["message"] as? String {
                messages[key.lowercased()] = message
            }
        }
        return messages
    }

    static func largestIcon(in manifest: [String: Any]) -> String? {
        guard let icons = manifest["icons"] as? [String: Any] else { return nil }
        let best = icons.compactMap { key, value -> (Int, String)? in
            guard let size = Int(key), let path = value as? String else { return nil }
            return (size, path)
        }.max { $0.0 < $1.0 }
        guard let path = best?.1 else { return nil }
        return path.hasPrefix("/") ? String(path.dropFirst()) : path
    }

    /// A stable, valid-looking ID for packages that carry none (a hand-made ZIP).
    static func fallbackID(for data: Data) -> String {
        CRXArchive.extensionID(fromPublicKey: data)
    }

    private static func unique(_ notes: [CompatibilityNote]) -> [CompatibilityNote] {
        var seen = Set<CompatibilityNote>()
        return notes.filter { seen.insert($0).inserted }
    }
}
