import Foundation

/// The decision point for Chromium requests: the current engine and the user's
/// settings, readable from any thread.
///
/// Chromium asks on its network thread, for every request, while the main
/// thread may be swapping in a freshly built engine or changing settings; this
/// type is the one place those meet.
public final class RequestFilter: @unchecked Sendable {
    private let lock = NSLock()
    private var engine: BlockerEngine?
    private var settings = BlockerSettings()

    public init() {}

    public func use(engine: BlockerEngine?) {
        lock.withLock { self.engine = engine }
    }

    public func use(settings: BlockerSettings) {
        lock.withLock { self.settings = settings }
    }

    /// Whether a request made by the page at `documentURL` is to be blocked.
    /// False while no engine is loaded, when blocking is off, and for sites the
    /// user has exempted.
    public func shouldBlock(url: String, documentURL: String, type: String) -> Bool {
        let (engine, settings) = lock.withLock { (self.engine, self.settings) }
        guard let engine, settings.filters(pageURL: documentURL) else { return false }
        return engine.verdict(url: url, documentURL: documentURL, type: type) == .block
    }
}
