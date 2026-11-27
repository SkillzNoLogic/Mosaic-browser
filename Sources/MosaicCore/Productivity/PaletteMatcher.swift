import Foundation

/// Matching and ranking for the command palette: how well a typed query fits an item's
/// title and keywords. Engine- and UI-independent so it can be unit-tested.
///
/// Every whitespace-separated word of the query has to match somewhere (title or keywords);
/// each word is scored by the best kind of match it found, from an exact title to a loose
/// in-order ("fuzzy") match, and the item's score is the average. Ties go to shorter titles.
public enum PaletteMatcher {
    /// Kinds of match, best first. The raw values are the scores they contribute.
    enum Grade: Double {
        case exact = 100
        /// The text starts with the token as a whole word ("tab" in "Tab Groups").
        case leadingWord = 92
        /// A whole word of the text ("tab" in "New Tab").
        case word = 86
        /// The start of the first word ("tab" in "Tabs & Workspaces").
        case prefix = 84
        case wordPrefix = 76
        case initials = 66
        case substring = 52
        case fuzzy = 34
    }

    /// Keywords count for a little less than the title, so "Close Tab" beats an item
    /// that only lists "close" as a keyword.
    static let keywordWeight = 0.8

    /// Lower-cased and diacritic-folded, so "Ecosia" matches "écosia".
    public static func normalize(_ text: String) -> String {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil)
            .lowercased()
    }

    /// Score of `query` for an item, or nil when it does not match. Higher is better.
    /// An empty query matches everything with score 0.
    public static func score(query: String, title: String, keywords: [String] = []) -> Double? {
        let tokens = normalize(query).split(whereSeparator: \.isWhitespace).map(String.init)
        guard !tokens.isEmpty else { return 0 }
        let normalizedTitle = normalize(title)
        let normalizedKeywords = keywords.map(normalize)

        var total = 0.0
        for token in tokens {
            var best = grade(token, in: normalizedTitle).map { $0 }
            for keyword in normalizedKeywords {
                if let value = grade(token, in: keyword).map({ $0 * keywordWeight }), value > (best ?? -1) {
                    best = value
                }
            }
            guard let best else { return nil }
            total += best
        }
        var result = total / Double(tokens.count)
        // The whole query as the start of the title is the strongest signal of intent.
        let phrase = tokens.joined(separator: " ")
        if tokens.count > 1, normalizedTitle.hasPrefix(phrase) { result += 8 }
        // Prefer shorter titles among equals ("Tab" over "Tab Groups Settings").
        result -= Double(normalizedTitle.count) * 0.05
        return result
    }

    /// Score of one query word against one normalised text.
    static func grade(_ token: String, in text: String) -> Double? {
        guard !text.isEmpty else { return nil }
        if text == token { return Grade.exact.rawValue }
        let words = words(of: text)
        if words.first.map({ $0 == token }) == true { return Grade.leadingWord.rawValue }
        if words.contains(where: { $0 == token }) { return Grade.word.rawValue }
        if text.hasPrefix(token) { return Grade.prefix.rawValue }
        if words.dropFirst().contains(where: { $0.hasPrefix(token) }) { return Grade.wordPrefix.rawValue }
        if token.count >= 2, initialsMatch(token, words: words) { return Grade.initials.rawValue }
        if text.contains(token) { return Grade.substring.rawValue }
        if let tightness = subsequenceTightness(token, in: text) {
            // 0…1, 1 being every character adjacent; a loose match is worth less.
            return Grade.fuzzy.rawValue * (0.5 + 0.5 * tightness)
        }
        return nil
    }

    static func words(of text: String) -> [Substring] {
        text.split(whereSeparator: { !$0.isLetter && !$0.isNumber })
    }

    /// "nt" → "New Tab", "sbe" → "Switch Blocker Engine": the token spells the leading
    /// letters of consecutive words, starting at any word.
    static func initialsMatch(_ token: String, words: [Substring]) -> Bool {
        let initials = words.compactMap(\.first)
        guard initials.count >= token.count else { return false }
        return String(initials).contains(token)
    }

    /// If every character of `token` appears in `text` in order, how tightly (1 = adjacent).
    static func subsequenceTightness(_ token: String, in text: String) -> Double? {
        let needle = Array(token), haystack = Array(text)
        guard needle.count >= 2, needle.count <= haystack.count else { return nil }
        var index = 0
        var first: Int?
        var last = 0
        for (position, character) in haystack.enumerated() where index < needle.count {
            if character == needle[index] {
                if first == nil { first = position }
                last = position
                index += 1
            }
        }
        guard index == needle.count, let first else { return nil }
        let span = last - first + 1
        return Double(needle.count) / Double(span)
    }
}

/// One thing the palette could show, reduced to what ranking needs.
public struct PaletteCandidate: Equatable, Sendable {
    public let id: String
    public let title: String
    public let keywords: [String]
    /// Added to the match score: lets open tabs outrank history entries of the same page.
    public let boost: Double

    public init(id: String, title: String, keywords: [String] = [], boost: Double = 0) {
        self.id = id
        self.title = title
        self.keywords = keywords
        self.boost = boost
    }
}

public enum PaletteRanker {
    /// Points for having been chosen recently; the most recent gets the full amount.
    static let recencyBonus = 14.0

    /// Matching candidates, best first. `recent` lists ids chosen recently, newest first.
    /// With an empty query, every candidate is returned: recent ones first, then in the order
    /// given (boosts only matter when something is typed).
    public static func rank(_ candidates: [PaletteCandidate], query: String, recent: [String] = [],
                            limit: Int = .max) -> [PaletteCandidate] {
        var recency: [String: Double] = [:]
        for (index, id) in recent.enumerated() where recency[id] == nil {
            recency[id] = recencyBonus * (1 - Double(index) / Double(max(recent.count, 1)))
        }
        let empty = query.trimmingCharacters(in: .whitespaces).isEmpty
        var scored: [(candidate: PaletteCandidate, score: Double, order: Int)] = []
        for (order, candidate) in candidates.enumerated() {
            guard let match = PaletteMatcher.score(query: query, title: candidate.title, keywords: candidate.keywords)
            else { continue }
            let bonus = recency[candidate.id] ?? 0
            let score = empty ? bonus : match + candidate.boost + bonus
            scored.append((candidate, score, order))
        }
        scored.sort { lhs, rhs in
            lhs.score != rhs.score ? lhs.score > rhs.score : lhs.order < rhs.order
        }
        return scored.prefix(limit).map(\.candidate)
    }
}

/// Ids of recently chosen palette items, newest first, capped.
public struct PaletteRecents: Equatable, Sendable {
    public private(set) var ids: [String]
    public let capacity: Int

    public init(ids: [String] = [], capacity: Int = 20) {
        self.capacity = capacity
        self.ids = Array(ids.prefix(capacity))
    }

    public mutating func note(_ id: String) {
        ids.removeAll { $0 == id }
        ids.insert(id, at: 0)
        if ids.count > capacity { ids.removeLast(ids.count - capacity) }
    }
}
