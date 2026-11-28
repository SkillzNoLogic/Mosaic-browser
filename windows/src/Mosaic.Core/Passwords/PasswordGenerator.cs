using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Mosaic.Core.Passwords;

/// <summary>
/// How generated passwords look. The default (20 characters from all four sets) gives about
/// 120 bits of entropy. Port of <c>PasswordGeneratorOptions</c> in
/// <c>Sources/MosaicCore/Passwords/PasswordGenerator.swift</c>; same JSON keys.
/// </summary>
public sealed record PasswordGeneratorOptions
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    [JsonPropertyName("length")] public int Length { get; init; } = 20;
    [JsonPropertyName("lowercase")] public bool Lowercase { get; init; } = true;
    [JsonPropertyName("uppercase")] public bool Uppercase { get; init; } = true;
    [JsonPropertyName("digits")] public bool Digits { get; init; } = true;
    [JsonPropertyName("symbols")] public bool Symbols { get; init; } = true;
    /// <summary>Leaves out characters that are easy to confuse when read or typed (l, 1, I, O, 0).</summary>
    [JsonPropertyName("avoidAmbiguous")] public bool AvoidAmbiguous { get; init; } = true;

    public static PasswordGeneratorOptions Default { get; } = new();

    /// <summary>Symbols that sites accept almost everywhere and that survive copy and paste.</summary>
    public const string SymbolSet = "-_.!@#$%&*+=?";

    /// <summary>The character sets in use. At least one is always on: with every set off, lowercase letters are used.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> CharacterSets
    {
        get
        {
            var sets = new List<string>();
            if (Lowercase) sets.Add(AvoidAmbiguous ? "abcdefghijkmnopqrstuvwxyz" : "abcdefghijklmnopqrstuvwxyz");
            if (Uppercase) sets.Add(AvoidAmbiguous ? "ABCDEFGHJKLMNPQRSTUVWXYZ" : "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
            if (Digits) sets.Add(AvoidAmbiguous ? "23456789" : "0123456789");
            if (Symbols) sets.Add(SymbolSet);
            return sets.Count == 0 ? ["abcdefghijkmnopqrstuvwxyz"] : sets;
        }
    }

    /// <summary><see cref="Length"/> limited to 8…128, and never shorter than one character per set.</summary>
    [JsonIgnore]
    public int EffectiveLength => Math.Max(Math.Clamp(Length, MinLength, MaxLength), CharacterSets.Count);

    /// <summary>Strength of a password made with these options, in bits (length × log2(alphabet)).</summary>
    [JsonIgnore]
    public double EntropyBits => EffectiveLength * Math.Log2(string.Concat(CharacterSets).Distinct().Count());
}

/// <summary>Makes random passwords.</summary>
public static class PasswordGenerator
{
    /// <summary>
    /// A password with at least one character from every enabled set, the rest drawn uniformly
    /// from all of them. <paramref name="next"/> returns a uniform integer in [0, n); the default
    /// is the system's cryptographically secure generator; tests pass a seeded one.
    /// </summary>
    public static string Generate(PasswordGeneratorOptions? options = null, Func<int, int>? next = null)
    {
        options ??= PasswordGeneratorOptions.Default;
        next ??= RandomNumberGenerator.GetInt32;
        var sets = options.CharacterSets;
        var all = string.Concat(sets);
        var characters = sets.Select(set => set[next(set.Length)]).ToList();
        while (characters.Count < options.EffectiveLength) characters.Add(all[next(all.Length)]);
        // So the guaranteed characters are not always at the front (Fisher–Yates).
        for (var i = characters.Count - 1; i > 0; i--)
        {
            var j = next(i + 1);
            (characters[i], characters[j]) = (characters[j], characters[i]);
        }
        return new string([.. characters]);
    }
}
