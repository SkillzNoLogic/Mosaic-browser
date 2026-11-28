import Foundation

/// The ordered list of languages web pages are asked for (the `Accept-Language` header
/// and `navigator.languages`), as BCP 47 tags: "en-GB", "fr", "zh-Hant-TW".
public struct LanguagePreferences: Codable, Equatable, Sendable {
    public private(set) var tags: [String]

    public init(tags: [String]) {
        self.tags = []
        for tag in tags { add(tag) }
    }

    /// Built from the system's preferred languages (`Locale.preferredLanguages`). A
    /// regional tag is followed by its base language, the way browsers fill the list
    /// ("en-AU" → "en-AU, en"), so sites that only know "en" still match.
    public static func fromSystem(_ preferred: [String]) -> LanguagePreferences {
        var result = LanguagePreferences(tags: [])
        for tag in preferred {
            guard let normalized = normalize(tag) else { continue }
            result.add(normalized)
            if let base = normalized.split(separator: "-").first.map(String.init), base != normalized {
                result.add(base)
            }
        }
        if result.tags.isEmpty { result.add("en-US"); result.add("en") }
        return result
    }

    /// Adds `tag` at the end. Returns false if it is not a language tag or is already listed.
    @discardableResult
    public mutating func add(_ tag: String) -> Bool {
        guard let normalized = Self.normalize(tag), !tags.contains(normalized) else { return false }
        tags.append(normalized)
        return true
    }

    /// The last language cannot be removed: pages must be asked for something.
    @discardableResult
    public mutating func remove(_ tag: String) -> Bool {
        guard tags.count > 1, let index = tags.firstIndex(of: tag) else { return false }
        tags.remove(at: index)
        return true
    }

    public mutating func move(_ tag: String, by offset: Int) {
        guard let index = tags.firstIndex(of: tag) else { return }
        let target = min(max(index + offset, 0), tags.count - 1)
        guard target != index else { return }
        tags.remove(at: index)
        tags.insert(tag, at: target)
    }

    public mutating func move(fromOffsets source: IndexSet, toOffset destination: Int) {
        let moving = source.sorted().map { tags[$0] }
        var remaining = tags.enumerated().filter { !source.contains($0.offset) }.map(\.element)
        let insertAt = destination - source.filter { $0 < destination }.count
        remaining.insert(contentsOf: moving, at: min(max(insertAt, 0), remaining.count))
        tags = remaining
    }

    /// The value of Chromium's and Gecko's `intl.accept_languages` preference: the tags
    /// in order, comma-separated. Both engines derive the header's quality values from
    /// the order themselves.
    public var preferenceValue: String { tags.joined(separator: ",") }

    /// The header pages receive, with quality values as browsers send them
    /// ("en-GB,en;q=0.9,fr;q=0.8"). For display and tests; engines compute their own.
    public var acceptLanguageHeader: String {
        tags.enumerated().map { index, tag in
            guard index > 0 else { return tag }
            let quality = max(0.1, 1.0 - Double(index) * 0.1)
            return "\(tag);q=\(String(format: "%.1f", quality))"
        }.joined(separator: ",")
    }

    /// Canonical case for a BCP 47 tag ("EN_us" → "en-US", "zh-hant-tw" → "zh-Hant-TW"),
    /// or nil if `input` is not shaped like one.
    public static func normalize(_ input: String) -> String? {
        let parts = input.trimmingCharacters(in: .whitespaces)
            .replacingOccurrences(of: "_", with: "-")
            .split(separator: "-", omittingEmptySubsequences: false)
            .map(String.init)
        guard let language = parts.first, (2...3).contains(language.count),
              language.allSatisfy({ $0.isASCII && $0.isLetter })
        else { return nil }
        var result = [language.lowercased()]
        for part in parts.dropFirst() {
            guard (1...8).contains(part.count), part.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber) })
            else { return nil }
            if part.count == 4, part.allSatisfy(\.isLetter) {
                result.append(part.prefix(1).uppercased() + part.dropFirst().lowercased())  // script
            } else if part.count == 2, part.allSatisfy(\.isLetter) {
                result.append(part.uppercased())  // region
            } else {
                result.append(part.lowercased())
            }
        }
        return result.joined(separator: "-")
    }

    /// Languages offered in the "Add Language" menu: common web languages and regional
    /// variants. Anything else can be typed as a tag.
    public static let common: [String] = [
        "af", "ar", "bg", "bn", "ca", "cs", "cy", "da", "de", "de-AT", "de-CH", "de-DE", "el",
        "en", "en-AU", "en-CA", "en-GB", "en-IN", "en-NZ", "en-US", "es", "es-ES", "es-MX", "es-419",
        "et", "eu", "fa", "fi", "fil", "fr", "fr-CA", "fr-CH", "fr-FR", "ga", "gl", "gu", "he", "hi",
        "hr", "hu", "hy", "id", "is", "it", "ja", "ka", "kk", "km", "kn", "ko", "lt", "lv", "mk",
        "ml", "mr", "ms", "mt", "nb", "ne", "nl", "nl-BE", "pa", "pl", "pt", "pt-BR", "pt-PT", "ro",
        "ru", "si", "sk", "sl", "sq", "sr", "sv", "sw", "ta", "te", "th", "tr", "uk", "ur", "uz",
        "vi", "zh", "zh-CN", "zh-HK", "zh-TW",
    ]

    /// "English (United Kingdom)" for "en-GB", in the given display locale.
    public static func displayName(for tag: String, in locale: Locale = Locale(identifier: "en")) -> String {
        locale.localizedString(forIdentifier: tag) ?? tag
    }
}
