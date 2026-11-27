import Foundation

/// Session files in the profile folder, and the marker that tells a crash from a quit.
///
///     <directory>/session.json           the current session, rewritten as it changes
///     <directory>/previous-session.json  the session a launch did not restore
///     <directory>/running                present while Mosaic runs; holds its process ID
///
/// A launch that finds `running` left behind by a process that no longer exists knows the
/// previous run did not end cleanly (crash, force quit, power loss).
public final class SessionStore {
    public enum PreviousRun: Equatable, Sendable {
        /// No earlier session and no marker: a new profile.
        case firstRun
        case clean
        /// The previous run did not quit normally.
        case crashed
    }

    public let directory: URL
    private let fileManager = FileManager.default

    public init(directory: URL) {
        self.directory = directory
    }

    public var sessionURL: URL { directory.appendingPathComponent("session.json") }
    public var previousSessionURL: URL { directory.appendingPathComponent("previous-session.json") }
    var markerURL: URL { directory.appendingPathComponent("running") }

    // MARK: Run marker

    /// Records that this process is running and reports how the previous run ended.
    /// `isAlive` is replaceable for tests.
    public func beginRun(
        pid: Int32 = ProcessInfo.processInfo.processIdentifier,
        isAlive: (Int32) -> Bool = { kill($0, 0) == 0 }
    ) -> PreviousRun {
        try? fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        var previous: PreviousRun = fileManager.fileExists(atPath: sessionURL.path) ? .clean : .firstRun
        if let text = try? String(contentsOf: markerURL, encoding: .utf8) {
            let owner = Int32(text.trimmingCharacters(in: .whitespacesAndNewlines))
            // A marker held by another live process means a second copy is running on
            // this profile, not a crash.
            if let owner, owner != pid, isAlive(owner) {
                previous = .clean
            } else {
                previous = .crashed
            }
        }
        try? "\(pid)\n".write(to: markerURL, atomically: true, encoding: .utf8)
        return previous
    }

    /// Records a clean exit. Call after the final `save`.
    public func endRun() {
        try? fileManager.removeItem(at: markerURL)
    }

    // MARK: Sessions

    /// The saved session, or nil if there is none. Throws `SessionCodec.DecodeError` for a
    /// file that cannot be used (see `setAsideUnreadable`).
    public func load() throws -> SessionSnapshot? {
        try read(sessionURL)
    }

    public func loadPrevious() throws -> SessionSnapshot? {
        try read(previousSessionURL)
    }

    public func save(_ snapshot: SessionSnapshot) throws {
        try write(snapshot, to: sessionURL)
    }

    /// Keeps `snapshot` as the "previous session" a launch chose not to restore, so it can
    /// still be reopened from the History menu.
    public func savePrevious(_ snapshot: SessionSnapshot) throws {
        try write(snapshot, to: previousSessionURL)
    }

    public func removePrevious() {
        try? fileManager.removeItem(at: previousSessionURL)
    }

    /// Moves an unusable session file aside (e.g. one from a newer Mosaic) instead of
    /// overwriting it. Returns where it went.
    @discardableResult
    public func setAsideUnreadable() -> URL? {
        guard fileManager.fileExists(atPath: sessionURL.path) else { return nil }
        let stamp = Int(Date().timeIntervalSince1970)
        let destination = directory.appendingPathComponent("session-unreadable-\(stamp).json")
        do {
            try fileManager.moveItem(at: sessionURL, to: destination)
            return destination
        } catch {
            return nil
        }
    }

    private func read(_ url: URL) throws -> SessionSnapshot? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try SessionCodec.decode(data)
    }

    private func write(_ snapshot: SessionSnapshot, to url: URL) throws {
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        try SessionCodec.encode(snapshot).write(to: url, options: .atomic)
    }
}
