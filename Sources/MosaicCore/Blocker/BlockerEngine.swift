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
        if let stats = blocker.invokeMethod("cosmeticStats", withArguments: []), stats.isObject {
            cosmeticStats = CosmeticStats(
                generic: Int(stats.objectForKeyedSubscript("generic")?.toInt32() ?? 0),
                specific: Int(stats.objectForKeyedSubscript("specific")?.toInt32() ?? 0),
                unsupported: Int(stats.objectForKeyedSubscript("unsupported")?.toInt32() ?? 0))
        }

        // From here on a script error is a wrong answer for one request, not a
        // reason to stop; it is logged and that request is let through.
        context.exceptionHandler = { context, exception in
            NSLog("Mosaic blocker: %@", exception?.toString() ?? "unknown JavaScript error")
            context?.exception = nil
        }
    }

    /// Cosmetic filters in effect (see `cosmeticStats`).
    public private(set) var cosmeticStats = CosmeticStats()

    /// Counts of the cosmetic filters compiled from the lists.
    public struct CosmeticStats: Equatable, Sendable {
        /// Generic hiding selectors (`##.ad`), applied on every page.
        public var generic = 0
        /// Site-specific hiding filters and exceptions.
        public var specific = 0
        /// Valid uBO syntax Mosaic does not apply: procedural filters, scriptlets,
        /// HTML and response-header filters, regex/path-based site targets.
        public var unsupported = 0

        public init(generic: Int = 0, specific: Int = 0, unsupported: Int = 0) {
            self.generic = generic
            self.specific = specific
            self.unsupported = unsupported
        }
    }

    /// Cosmetic filtering for one hostname.
    public struct CosmeticRules: Equatable, Sendable {
        /// CSS hiding what site-specific filters name, exceptions applied.
        public var css: String
        /// Generic selectors this site excepts (`example.com#@#.ad`); the generic
        /// stylesheet for this site must leave them out.
        public var genericExceptions: [String]
    }

    /// The site-specific cosmetic rules for `hostname`. Cheap (well under a
    /// millisecond); callers still cache per hostname.
    public func cosmeticRules(forHostname hostname: String) -> CosmeticRules {
        lock.lock()
        defer { lock.unlock() }
        guard let result = blocker.invokeMethod("cosmeticFor", withArguments: [hostname]), result.isObject else {
            return CosmeticRules(css: "", genericExceptions: [])
        }
        let css = result.objectForKeyedSubscript("css")?.toString() ?? ""
        let exceptions = (result.objectForKeyedSubscript("exceptions")?.toArray() as? [String]) ?? []
        return CosmeticRules(css: css, genericExceptions: exceptions)
    }

    /// Every generic hiding selector as a stylesheet (one rule per selector),
    /// leaving out `excluded`. The same for every page apart from exclusions.
    public func genericCosmeticCSS(excluding excluded: [String] = []) -> String {
        lock.lock()
        defer { lock.unlock() }
        return blocker.invokeMethod("cosmeticGeneric", withArguments: [excluded])?.toString() ?? ""
    }

    /// The decision for one request together with the filter that made it, as
    /// uBO's logger writes it (empty when no filter matched).
    public func details(url: String, documentURL: String, type: String) -> (verdict: Verdict, filter: String) {
        lock.lock()
        defer { lock.unlock() }
        guard let result = blocker.invokeMethod("matchDetails", withArguments: [url, documentURL, type]),
              result.isObject
        else { return (.none, "") }
        let verdict = Verdict(rawValue: result.objectForKeyedSubscript("result")?.toInt32() ?? 0) ?? .none
        return (verdict, result.objectForKeyedSubscript("filter")?.toString() ?? "")
    }

    /// Checks filters the user typed (one per line) with uBO's parser.
    public func validate(filters text: String) -> [FilterCheck] {
        lock.lock()
        defer { lock.unlock() }
        guard let result = blocker.invokeMethod("validateFilters", withArguments: [text]),
              let records = result.toArray() as? [[String: Any]]
        else { return [] }
        return records.map { record in
            FilterCheck(
                line: (record["line"] as? NSNumber)?.intValue ?? 0,
                text: record["text"] as? String ?? "",
                kind: FilterCheck.Kind(rawValue: record["kind"] as? String ?? "") ?? .error,
                message: record["message"] as? String ?? "")
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

/// The verdict on one line of the user's own filters.
public struct FilterCheck: Equatable, Sendable {
    public enum Kind: String, Sendable {
        case comment, network, cosmetic
        /// Valid uBO syntax that Mosaic does not apply (scriptlets, procedural
        /// cosmetic filters, modifiers such as `$removeparam`).
        case unsupported
        case error
    }

    /// 1-based line number in the text checked.
    public let line: Int
    public let text: String
    public let kind: Kind
    /// Why it is an error or unsupported, or a note about a valid filter.
    public let message: String

    public init(line: Int, text: String, kind: Kind, message: String) {
        self.line = line
        self.text = text
        self.kind = kind
        self.message = message
    }

    /// Whether the line takes effect (possibly with a note).
    public var isEffective: Bool { kind == .network || kind == .cosmetic }
}
