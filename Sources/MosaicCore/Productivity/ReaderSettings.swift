import Foundation

/// Reader view typography and colours, shared by both engines.
///
/// The scales are Firefox's own (`reader.*` preferences), so Gecko's built-in reader view
/// and Mosaic's Chromium reader show the same text for the same settings:
/// font size step 1…9 → 12…28 px, content width step 1…9 → 20…60 em,
/// line spacing step 1…9 → 1.0…2.6 em.
public struct ReaderSettings: Equatable, Codable, Sendable {
    public enum Font: String, Codable, CaseIterable, Sendable {
        case sans = "sans-serif", serif

        public var title: String { self == .sans ? "Sans-serif" : "Serif" }
    }

    public enum Theme: String, Codable, CaseIterable, Sendable {
        case light, sepia, dark

        public var title: String { rawValue.capitalized }
    }

    public static let steps = 1...9

    public var font: Font
    public var fontSize: Int
    public var contentWidth: Int
    public var lineHeight: Int
    public var theme: Theme

    /// Firefox's defaults, except the theme (Firefox follows the system; Mosaic starts light).
    public init(font: Font = .sans, fontSize: Int = 5, contentWidth: Int = 3, lineHeight: Int = 4,
                theme: Theme = .light) {
        self.font = font
        self.fontSize = Self.clamp(fontSize)
        self.contentWidth = Self.clamp(contentWidth)
        self.lineHeight = Self.clamp(lineHeight)
        self.theme = theme
    }

    static func clamp(_ value: Int) -> Int { min(max(value, steps.lowerBound), steps.upperBound) }

    /// Makes out-of-range values (from a hand-edited file or another engine) valid.
    public var clamped: ReaderSettings {
        ReaderSettings(font: font, fontSize: fontSize, contentWidth: contentWidth, lineHeight: lineHeight, theme: theme)
    }

    public var fontSizePixels: Int { 10 + 2 * fontSize }
    public var contentWidthEm: Double { 20 + 5 * Double(contentWidth - 1) }
    public var lineHeightEm: Double { 1 + 0.2 * Double(lineHeight - 1) }

    // MARK: Gecko preferences

    /// The Gecko preferences that give its reader view these settings.
    public var geckoPreferences: [String: Any] {
        [
            "reader.font_type": font.rawValue,
            "reader.font_size": fontSize,
            "reader.content_width": contentWidth,
            "reader.line_height": lineHeight,
            "reader.color_scheme": theme.rawValue,
        ]
    }

    /// Reads back what the user chose in Gecko's own reader toolbar. Values Mosaic does
    /// not offer (Gecko's "auto"/"contrast"/"gray" themes, monospace, its extra-large font
    /// steps) keep the current setting or are clamped.
    public func merging(geckoPreferences prefs: [String: Any]) -> ReaderSettings {
        var result = self
        if let font = (prefs["reader.font_type"] as? String).flatMap(Font.init(rawValue:)) { result.font = font }
        if let size = prefs["reader.font_size"] as? Int { result.fontSize = Self.clamp(size) }
        if let width = prefs["reader.content_width"] as? Int { result.contentWidth = Self.clamp(width) }
        if let height = prefs["reader.line_height"] as? Int { result.lineHeight = Self.clamp(height) }
        if let theme = (prefs["reader.color_scheme"] as? String).flatMap(Theme.init(rawValue:)) { result.theme = theme }
        return result
    }

    // MARK: Stored form

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            font: (try? container.decode(Font.self, forKey: .font)) ?? .sans,
            fontSize: (try? container.decode(Int.self, forKey: .fontSize)) ?? 5,
            contentWidth: (try? container.decode(Int.self, forKey: .contentWidth)) ?? 3,
            lineHeight: (try? container.decode(Int.self, forKey: .lineHeight)) ?? 4,
            theme: (try? container.decode(Theme.self, forKey: .theme)) ?? .light)
    }
}
