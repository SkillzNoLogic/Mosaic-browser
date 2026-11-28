import Foundation

/// A local, offline estimate of how guessable a password is. Nothing leaves the Mac: there is
/// no breach lookup. It is deliberately simple and errs towards calling a password weak.
public enum PasswordStrength: Int, Comparable, Sendable, CaseIterable {
    case weak, fair, strong

    public static func < (lhs: PasswordStrength, rhs: PasswordStrength) -> Bool { lhs.rawValue < rhs.rawValue }

    public var title: String {
        switch self {
        case .weak: "Weak"
        case .fair: "Fair"
        case .strong: "Strong"
        }
    }

    public static func evaluate(_ password: String) -> PasswordStrength {
        let bits = estimatedBits(password)
        if bits < 45 { return .weak }
        if bits < 70 { return .fair }
        return .strong
    }

    /// Rough entropy in bits, after penalties for the patterns people actually use.
    public static func estimatedBits(_ password: String) -> Double {
        let characters = Array(password)
        guard characters.count >= 8 else { return min(Double(characters.count) * 3, 20) }
        let lowered = password.lowercased()
        if commonPasswords.contains(lowered) { return 0 }
        // "Password1!", "qwerty2024": a common word with digits or symbols around it.
        let core = lowered.trimmingCharacters(in: CharacterSet.letters.inverted)
        if core.count >= 4, commonPasswords.contains(core) || commonWords.contains(core) { return 10 }

        var pool = 0
        if characters.contains(where: \.isLowercase) { pool += 26 }
        if characters.contains(where: \.isUppercase) { pool += 26 }
        if characters.contains(where: \.isNumber) { pool += 10 }
        if characters.contains(where: { !$0.isLetter && !$0.isNumber }) { pool += 33 }
        let perCharacter = log2(Double(max(pool, 2)))

        // Count characters that add little: repeats of the previous character and steps in
        // a run ("aaaa", "1234", "abcd", "dcba").
        var predictable = 0
        for index in characters.indices.dropFirst() {
            let previous = characters[index - 1], current = characters[index]
            if previous == current {
                predictable += 1
            } else if let p = previous.asciiValue, let c = current.asciiValue, abs(Int(p) - Int(c)) == 1 {
                predictable += 1
            }
        }
        let distinct = Set(characters).count
        let effective = Double(characters.count - predictable) * 0.85 + Double(predictable) * 0.15
        var bits = effective * perCharacter
        // Very few distinct characters ("abababab") caps the estimate.
        if distinct * 2 < characters.count {
            bits = min(bits, Double(characters.count) * log2(Double(max(distinct, 2))))
        }
        return bits
    }

    /// The most common passwords (lowercased); any of these is weak whatever its length.
    static let commonPasswords: Set<String> = [
        "password", "password1", "password123", "passw0rd", "p@ssw0rd", "p@ssword", "123456", "1234567",
        "12345678", "123456789", "1234567890", "12345", "1234", "111111", "000000", "123123", "654321",
        "qwerty", "qwertyuiop", "qwerty123", "asdfgh", "asdfghjkl", "zxcvbnm", "1q2w3e4r", "1qaz2wsx",
        "abc123", "abcdef", "abcd1234", "iloveyou", "letmein", "welcome", "welcome1", "admin", "admin123",
        "administrator", "root", "toor", "monkey", "dragon", "master", "sunshine", "princess", "football",
        "baseball", "superman", "batman", "trustno1", "shadow", "michael", "jennifer", "hello", "hello123",
        "freedom", "whatever", "starwars", "secret", "changeme", "default", "guest", "login", "access",
        "passpass", "mustang", "charlie", "donald", "loveme", "ninja", "azerty", "solo", "flower",
    ]

    /// Words that make up most "word + number" passwords.
    static let commonWords: Set<String> = [
        "password", "pass", "love", "admin", "welcome", "summer", "winter", "spring", "autumn", "qwerty",
        "dragon", "monkey", "master", "letmein", "football", "baseball", "soccer", "hockey", "secret",
        "sunshine", "princess", "shadow", "hello", "login", "user", "test", "guest", "mosaic", "apple",
        "google", "facebook", "computer", "internet", "changeme",
    ]
}

/// Weak and reused passwords among a set of credentials, computed locally.
public struct PasswordAudit: Equatable, Sendable {
    public var weak: Set<CredentialID> = []
    /// Credentials whose password is also used by at least one other saved credential.
    public var reused: Set<CredentialID> = []

    public init(weak: Set<CredentialID> = [], reused: Set<CredentialID> = []) {
        self.weak = weak
        self.reused = reused
    }

    /// `passwords` maps each credential to its password. The passwords are only compared
    /// in memory and not kept.
    public static func run(_ passwords: [CredentialID: String]) -> PasswordAudit {
        var audit = PasswordAudit()
        var byPassword: [String: [CredentialID]] = [:]
        for (id, password) in passwords {
            if PasswordStrength.evaluate(password) == .weak { audit.weak.insert(id) }
            byPassword[password, default: []].append(id)
        }
        for ids in byPassword.values where ids.count > 1 {
            audit.reused.formUnion(ids)
        }
        return audit
    }
}
