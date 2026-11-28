import Foundation

/// "Frecency": how frequently and how recently an address was visited, the score
/// Firefox's address bar ranks history by. Each of the latest visits earns points by age
/// (typed visits count double); the average is scaled by the total number of visits.
public enum Frecency {
    /// How many of the latest visits are sampled.
    public static let sampleSize = 10

    public static func points(age: TimeInterval, typed: Bool) -> Double {
        let days = age / 86_400
        let base: Double
        switch days {
        case ..<4: base = 100
        case ..<14: base = 70
        case ..<31: base = 50
        case ..<90: base = 30
        default: base = 10
        }
        return typed ? base * 2 : base
    }

    /// `visits` are the latest visits (time, typed), any order, at most `sampleSize` used.
    public static func score(visitCount: Int, visits: [(time: Date, typed: Bool)], now: Date = Date()) -> Double {
        guard visitCount > 0, !visits.isEmpty else { return 0 }
        let sample = visits.sorted { $0.time > $1.time }.prefix(sampleSize)
        let total = sample.reduce(0) { $0 + points(age: max(0, now.timeIntervalSince($1.time)), typed: $1.typed) }
        return (Double(visitCount) * total / Double(sample.count)).rounded()
    }
}

/// Ranks address-bar candidates (history, bookmarks, open tabs) against what was typed.
public enum SuggestionRanker {
    public struct Candidate: Hashable, Sendable {
        public enum Source: Hashable, Sendable { case history, bookmark, openTab }

        public var url: String
        public var title: String
        public var frecency: Double
        public var source: Source
        public var isBookmarked: Bool
        /// Open tabs only: which tab.
        public var tabID: UUID?

        public init(url: String, title: String, frecency: Double, source: Source, isBookmarked: Bool = false,
                    tabID: UUID? = nil) {
            self.url = url
            self.title = title
            self.frecency = frecency
            self.source = source
            self.isBookmarked = isBookmarked
            self.tabID = tabID
        }
    }

    public struct Ranked: Hashable, Sendable {
        public var candidate: Candidate
        public var score: Double
        /// The first word typed starts the site's host ("git" for github.com), so the
        /// address bar could complete it inline.
        public var matchesHostPrefix: Bool
    }

    /// Lower-cased terms, split on whitespace.
    public static func terms(_ input: String) -> [String] {
        input.lowercased().split(whereSeparator: \.isWhitespace).map(String.init)
    }

    /// How well `candidate` matches `terms`, or nil when some term is missing.
    public static func match(_ terms: [String], url: String, title: String) -> (quality: Double, hostPrefix: Bool)? {
        guard let first = terms.first else { return nil }
        let lowerURL = url.lowercased()
        let lowerTitle = title.lowercased()
        let display = LibraryURL.display(lowerURL)
        let host = LibraryURL.host(of: lowerURL)
        for term in terms where !lowerURL.contains(term) && !lowerTitle.contains(term) && !display.contains(term) {
            return nil
        }
        var quality = 1.0
        var hostPrefix = false
        if host.hasPrefix(first) || display.hasPrefix(first) {
            quality = 4
            hostPrefix = host.hasPrefix(first)
        } else if startsWord(first, in: lowerTitle) || hostLabelPrefix(first, host: host) {
            quality = 2
        } else if startsWord(first, in: display) {
            quality = 1.5
        }
        // More of the typed words found at word starts in the title: a little better.
        let wordHits = terms.dropFirst().filter { startsWord($0, in: lowerTitle) || startsWord($0, in: display) }.count
        quality += 0.25 * Double(wordHits)
        return (quality, hostPrefix)
    }

    /// Best first. Candidates with the same address are merged (an open tab, a bookmark
    /// and a history entry for one page become one row, preferring the open tab).
    public static func rank(_ input: String, candidates: [Candidate], limit: Int) -> [Ranked] {
        let terms = terms(input)
        guard !terms.isEmpty else { return [] }
        var best: [String: Ranked] = [:]
        for candidate in candidates {
            guard let (quality, hostPrefix) = match(terms, url: candidate.url, title: candidate.title) else { continue }
            var base = candidate.frecency + 1
            if candidate.isBookmarked || candidate.source == .bookmark { base += 140 }
            if candidate.source == .openTab { base += 60 }
            let ranked = Ranked(candidate: candidate, score: base * quality, matchesHostPrefix: hostPrefix)
            let key = LibraryURL.normalized(candidate.url)
            if let existing = best[key] {
                var merged = existing.score >= ranked.score ? existing : ranked
                merged.score = max(existing.score, ranked.score)
                // Keep the strongest kind of row: an open tab beats a bookmark beats history.
                if existing.candidate.source == .openTab || ranked.candidate.source == .openTab {
                    merged.candidate = existing.candidate.source == .openTab ? existing.candidate : ranked.candidate
                }
                merged.candidate.isBookmarked = existing.candidate.isBookmarked || ranked.candidate.isBookmarked
                    || existing.candidate.source == .bookmark || ranked.candidate.source == .bookmark
                if merged.candidate.title.isEmpty {
                    merged.candidate.title = existing.candidate.title.isEmpty ? ranked.candidate.title : existing.candidate.title
                }
                best[key] = merged
            } else {
                best[key] = ranked
            }
        }
        return best.values
            .sorted { $0.score != $1.score ? $0.score > $1.score : $0.candidate.url < $1.candidate.url }
            .prefix(limit)
            .map { $0 }
    }

    private static func startsWord(_ term: String, in text: String) -> Bool {
        var searchStart = text.startIndex
        while let range = text.range(of: term, range: searchStart..<text.endIndex) {
            if range.lowerBound == text.startIndex { return true }
            let before = text[text.index(before: range.lowerBound)]
            if !before.isLetter && !before.isNumber { return true }
            searchStart = text.index(after: range.lowerBound)
        }
        return false
    }

    private static func hostLabelPrefix(_ term: String, host: String) -> Bool {
        host.split(separator: ".").contains { $0.hasPrefix(term) }
    }
}
