using System.Text;

namespace Mosaic.Core.Passwords;

/// <summary>
/// A local, offline estimate of how guessable a password is. Nothing leaves the PC: there is no
/// breach lookup. Deliberately simple; errs towards calling a password weak.
/// Port of <c>Sources/MosaicCore/Passwords/PasswordStrength.swift</c> (same thresholds and lists).
/// </summary>
public enum PasswordStrength { Weak, Fair, Strong }

public static class PasswordStrengths
{
    public static string Title(this PasswordStrength strength) => strength switch
    {
        PasswordStrength.Weak => "Weak",
        PasswordStrength.Fair => "Fair",
        _ => "Strong",
    };

    public static PasswordStrength Evaluate(string password)
    {
        var bits = EstimatedBits(password);
        if (bits < 45) return PasswordStrength.Weak;
        if (bits < 70) return PasswordStrength.Fair;
        return PasswordStrength.Strong;
    }

    /// <summary>Rough entropy in bits, after penalties for the patterns people actually use.</summary>
    public static double EstimatedBits(string password)
    {
        var characters = password.EnumerateRunes().ToArray();
        if (characters.Length < 8) return Math.Min(characters.Length * 3.0, 20);
        var lowered = password.ToLowerInvariant();
        if (CommonPasswords.Contains(lowered)) return 0;
        // "Password1!", "qwerty2024": a common word with digits or symbols around it.
        var core = TrimNonLetters(lowered);
        if (core.Length >= 4 && (CommonPasswords.Contains(core) || CommonWords.Contains(core))) return 10;

        var pool = 0;
        if (characters.Any(Rune.IsLower)) pool += 26;
        if (characters.Any(Rune.IsUpper)) pool += 26;
        if (characters.Any(Rune.IsNumber)) pool += 10;
        if (characters.Any(c => !Rune.IsLetter(c) && !Rune.IsNumber(c))) pool += 33;
        var perCharacter = Math.Log2(Math.Max(pool, 2));

        // Characters that add little: repeats of the previous one and steps in a run
        // ("aaaa", "1234", "abcd", "dcba").
        var predictable = 0;
        for (var i = 1; i < characters.Length; i++)
        {
            Rune previous = characters[i - 1], current = characters[i];
            if (previous == current) predictable++;
            else if (previous.IsAscii && current.IsAscii && Math.Abs(previous.Value - current.Value) == 1) predictable++;
        }
        var distinct = characters.Distinct().Count();
        var effective = (characters.Length - predictable) * 0.85 + predictable * 0.15;
        var bits = effective * perCharacter;
        // Very few distinct characters ("abababab") caps the estimate.
        if (distinct * 2 < characters.Length)
        {
            bits = Math.Min(bits, characters.Length * Math.Log2(Math.Max(distinct, 2)));
        }
        return bits;
    }

    private static string TrimNonLetters(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && !char.IsLetter(text[start])) start++;
        while (end > start && !char.IsLetter(text[end - 1])) end--;
        return text[start..end];
    }

    /// <summary>The most common passwords (lowercased); any of these is weak whatever its length.</summary>
    internal static readonly HashSet<string> CommonPasswords =
    [
        "password", "password1", "password123", "passw0rd", "p@ssw0rd", "p@ssword", "123456", "1234567",
        "12345678", "123456789", "1234567890", "12345", "1234", "111111", "000000", "123123", "654321",
        "qwerty", "qwertyuiop", "qwerty123", "asdfgh", "asdfghjkl", "zxcvbnm", "1q2w3e4r", "1qaz2wsx",
        "abc123", "abcdef", "abcd1234", "iloveyou", "letmein", "welcome", "welcome1", "admin", "admin123",
        "administrator", "root", "toor", "monkey", "dragon", "master", "sunshine", "princess", "football",
        "baseball", "superman", "batman", "trustno1", "shadow", "michael", "jennifer", "hello", "hello123",
        "freedom", "whatever", "starwars", "secret", "changeme", "default", "guest", "login", "access",
        "passpass", "mustang", "charlie", "donald", "loveme", "ninja", "azerty", "solo", "flower",
    ];

    /// <summary>Words that make up most "word + number" passwords.</summary>
    internal static readonly HashSet<string> CommonWords =
    [
        "password", "pass", "love", "admin", "welcome", "summer", "winter", "spring", "autumn", "qwerty",
        "dragon", "monkey", "master", "letmein", "football", "baseball", "soccer", "hockey", "secret",
        "sunshine", "princess", "shadow", "hello", "login", "user", "test", "guest", "mosaic", "apple",
        "google", "facebook", "computer", "internet", "changeme",
    ];
}

/// <summary>Weak and reused passwords among a set of credentials, computed locally.</summary>
public sealed class PasswordAudit
{
    public HashSet<CredentialID> Weak { get; } = [];

    /// <summary>Credentials whose password is also used by at least one other saved credential.</summary>
    public HashSet<CredentialID> Reused { get; } = [];

    /// <summary>The passwords are only compared in memory and not kept.</summary>
    public static PasswordAudit Run(IReadOnlyDictionary<CredentialID, string> passwords)
    {
        var audit = new PasswordAudit();
        var byPassword = new Dictionary<string, List<CredentialID>>(StringComparer.Ordinal);
        foreach (var (id, password) in passwords)
        {
            if (PasswordStrengths.Evaluate(password) == PasswordStrength.Weak) audit.Weak.Add(id);
            if (!byPassword.TryGetValue(password, out var ids)) byPassword[password] = ids = [];
            ids.Add(id);
        }
        foreach (var ids in byPassword.Values)
        {
            if (ids.Count > 1) audit.Reused.UnionWith(ids);
        }
        return audit;
    }
}
