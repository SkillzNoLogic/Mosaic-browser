import Foundation
import JavaScriptCore

/// uBlock Origin's network filtering engine, hosted in a JavaScriptCore context.
///
/// The engine itself is uBO's own code (`static-net-filtering.js` and its
/// dependencies), bundled from uBO's source by `Scripts/bootstrap.sh` together
/// with `Resources/blocker/engine.js`, which exposes it as a global
/// `MosaicBlocker`. This class loads that bundle and marshals calls to it.
///
/// Mosaic consults it for every Chromium sub-resource request, on Chromium's
/// network thread, so `verdict` is safe to call from any thread. (Mozilla tabs
/// load the same bundle inside Gecko's own process; see `agent.sys.mjs`.)
public final class BlockerEngine: @unchecked Sendable {
    public enum Verdict: Int32, Sendable {
        /// No filter matched.
        case none = 0
        /// A blocking filter matched.
        case block = 1
        /// An exception filter (`@@…`) matched; the request is explicitly allowed.
        case allow = 2
    }

    public enum EngineError: Error, LocalizedError {
        case bundleUnreadable(URL)
        case script(String)

        public var errorDescription: String? {
            switch self {
            case .bundleUnreadable(let url): "The content blocker's engine could not be read from \(url.path)."
            case .script(let message): "The content blocker's engine reported an error: \(message)"
            }
        }
    }

    /// A filter list's name and text.
    public struct List: Sendable {
        public let name: String
        public let text: String

        public init(name: String, text: String) {
            self.name = name
            self.text = text
        }
    }

    /// Version of uBlock Origin the engine was built from.
    public let version: String
    /// Number of network filters in effect.
    public private(set) var filterCount = 0

    private let lock = NSLock()
    private let context: JSContext
    private let blocker: JSValue
    private let matchFunction: JSValue

    /// Loads the engine and compiles `lists` into it. Takes on the order of a
    /// second for uBO's default lists; call it off the main thread.
    /// - Parameters:
    ///   - bundle: `ublock-engine.js`.
    ///   - suffixList: text of the Public Suffix List, which the engine needs
    ///     to tell first-party requests from third-party ones.
    public init(bundle: URL, suffixList: String, lists: [List]) throws {
        guard let source = try? String(contentsOf: bundle, encoding: .utf8) else {
            throw EngineError.bundleUnreadable(bundle)
        }
        guard let context = JSContext(virtualMachine: JSVirtualMachine()) else {
            throw EngineError.script("JavaScriptCore is unavailable")
        }
        self.context = context
        context.name = "Mosaic content blocker"

        var failure: String?
        context.exceptionHandler = { _, exception in
            failure = exception?.toString() ?? "unknown JavaScript error"
        }
        let log: @convention(block) (String, String) -> Void = { level, text in
            if level == "error" || level == "warn" { NSLog("Mosaic blocker [%@]: %@", level, text) }
        }
        context.setObject(log, forKeyedSubscript: "__mosaicLog" as NSString)

        context.evaluateScript(source, withSourceURL: bundle)
        if let failure { throw EngineError.script(failure) }
        guard let blocker = context.objectForKeyedSubscript("MosaicBlocker"), blocker.isObject,
              let matchFunction = blocker.objectForKeyedSubscript("match"), !matchFunction.isUndefined
        else { throw EngineError.script("the engine bundle did not define MosaicBlocker") }
        self.blocker = blocker
        self.matchFunction = matchFunction
        version = blocker.objectForKeyedSubscript("version")?.toString() ?? "?"

        blocker.invokeMethod("init", withArguments: [suffixList])
        if let failure { throw EngineError.script(failure) }

        let payload = lists.map { ["name": $0.name, "raw": $0.text] }
        let count = blocker.invokeMethod("loadLists", withArguments: [payload])
        if let failure { throw EngineError.script(failure) }
        filterCount = Int(count?.toInt32() ?? 0)

        // From here on a script error is a wrong answer for one request, not a
        // reason to stop; it is logged and that request is let through.
        context.exceptionHandler = { context, exception in
            NSLog("Mosaic blocker: %@", exception?.toString() ?? "unknown JavaScript error")
            context?.exception = nil
        }
    }

    /// The engine's decision for one request.
    /// - Parameters:
    ///   - url: the request URL.
    ///   - documentURL: URL of the page (top-level document) making the request.
    ///   - type: a webRequest resource type (`script`, `image`, `xmlhttprequest`, …).
    public func verdict(url: String, documentURL: String, type: String) -> Verdict {
        lock.lock()
        defer { lock.unlock() }
        guard let result = matchFunction.call(withArguments: [url, documentURL, type]), result.isNumber else {
            return .none
        }
        return Verdict(rawValue: result.toInt32()) ?? .none
    }
}
