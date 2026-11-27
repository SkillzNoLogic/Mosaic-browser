import Foundation

/// How generated passwords look. The default (20 characters from all four sets) gives about
/// 120 bits of entropy, far beyond what can be guessed.
public struct PasswordGeneratorOptions: Codable, Equatable, Sendable {
    public static let lengthRange = 8...128

    public var length: Int
    public var lowercase: Bool
    public var uppercase: Bool
    public var digits: Bool
    public var symbols: Bool
    /// Leaves out characters that are easy to confuse when read or typed (l, 1, I, O, 0).
    public var avoidAmbiguous: Bool

    public init(length: Int = 20, lowercase: Bool = true, uppercase: Bool = true, digits: Bool = true,
                symbols: Bool = true, avoidAmbiguous: Bool = true) {
        self.length = length
        self.lowercase = lowercase
        self.uppercase = uppercase
        self.digits = digits
        self.symbols = symbols
        self.avoidAmbiguous = avoidAmbiguous
    }

    public static let `default` = PasswordGeneratorOptions()

    /// The character sets in use. At least one is always on: with every set switched off,
    /// lowercase letters are used.
    var characterSets: [[Character]] {
        var sets: [[Character]] = []
        if lowercase { sets.append(Array(avoidAmbiguous ? "abcdefghijkmnopqrstuvwxyz" : "abcdefghijklmnopqrstuvwxyz")) }
        if uppercase { sets.append(Array(avoidAmbiguous ? "ABCDEFGHJKLMNPQRSTUVWXYZ" : "ABCDEFGHIJKLMNOPQRSTUVWXYZ")) }
        if digits { sets.append(Array(avoidAmbiguous ? "23456789" : "0123456789")) }
        // Symbols that sites accept almost everywhere and that survive copy and paste.
        if symbols { sets.append(Array("-_.!@#$%&*+=?")) }
        return sets.isEmpty ? [Array("abcdefghijkmnopqrstuvwxyz")] : sets
    }

    /// `length` limited to `lengthRange`, and never shorter than one character per set.
    public var effectiveLength: Int {
        max(min(max(length, Self.lengthRange.lowerBound), Self.lengthRange.upperBound), characterSets.count)
    }

    /// Strength of a password made with these options, in bits (length × log2(alphabet)).
    public var entropyBits: Double {
        let alphabet = Set(characterSets.joined()).count
        return Double(effectiveLength) * log2(Double(alphabet))
    }
}

/// Makes random passwords.
public enum PasswordGenerator {
    /// A password with at least one character from every enabled set, the rest drawn
    /// uniformly from all of them. The system generator is cryptographically secure
    /// (`arc4random`); tests pass a seeded one.
    public static func generate<R: RandomNumberGenerator>(_ options: PasswordGeneratorOptions, using random: inout R) -> String {
        let sets = options.characterSets
        let all = Array(sets.joined())
        var characters = sets.map { $0.randomElement(using: &random)! }
        while characters.count < options.effectiveLength {
            characters.append(all.randomElement(using: &random)!)
        }
        // So the guaranteed characters are not always at the front.
        characters.shuffle(using: &random)
        return String(characters)
    }

    public static func generate(_ options: PasswordGeneratorOptions = .default) -> String {
        var random = SystemRandomNumberGenerator()
        return generate(options, using: &random)
    }
}
