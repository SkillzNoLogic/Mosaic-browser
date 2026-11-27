using System.Globalization;
using System.Text;

namespace Mosaic.Core.Search;

/// <summary>
/// Text classification with the semantics of the Swift standard library, so that the address bar
/// makes the same address-or-search decision on Windows as the macOS implementation does.
/// </summary>
/// <remarks>
/// Swift strings are sequences of <c>Character</c>s (extended grapheme clusters), and a
/// <c>Character</c>'s properties are those of its first Unicode scalar. That matters for real
/// input: in "उदाहरण.भारत" the vowel signs are combining marks, not letters, yet every
/// <c>Character</c> of the name is a letter. The helpers here therefore work on grapheme
/// clusters, each represented as its own string.
/// </remarks>
internal static class SwiftText
{
    /// <summary>Splits text into Swift <c>Character</c>s (extended grapheme clusters).</summary>
    public static string[] Characters(string text)
    {
        var characters = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            characters.Add(enumerator.GetTextElement());
        }
        return [.. characters];
    }

    /// <summary>Swift's <c>Character.isWhitespace</c>: the Unicode <c>White_Space</c> property.</summary>
    public static bool IsWhitespace(string character) => IsWhitespace(FirstScalar(character));

    /// <summary>Swift's <c>Character.isLetter</c>: the Unicode <c>Alphabetic</c> property.</summary>
    /// <remarks>
    /// .NET does not expose <c>Alphabetic</c>, so it is derived from the general category: all
    /// letters, letter-numbers (Ⅷ) and the enclosed Latin letters (Ⓐ). Combining marks that are
    /// alphabetic are not covered; they only ever lead a grapheme cluster in malformed text.
    /// </remarks>
    public static bool IsLetter(string character)
    {
        var scalar = FirstScalar(character);
        return Rune.GetUnicodeCategory(scalar) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber => true,
            UnicodeCategory.OtherSymbol => scalar.Value is (>= 0x24B6 and <= 0x24E9) or (>= 0x1F130 and <= 0x1F149)
                or (>= 0x1F150 and <= 0x1F169) or (>= 0x1F170 and <= 0x1F189),
            _ => false,
        };
    }

    /// <summary>Swift's <c>Character.isNumber</c>: any scalar with a numeric type (7, ⅚, ८, Ⅷ).</summary>
    public static bool IsNumber(string character) =>
        Rune.GetUnicodeCategory(FirstScalar(character)) is UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    /// <summary>
    /// Swift's <c>trimmingCharacters(in: .whitespacesAndNewlines)</c>. Foundation's set is
    /// <c>White_Space</c> plus the zero-width space, which pasted text often carries.
    /// </summary>
    public static string Trim(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsTrimmed(text[start])) start++;
        while (end > start && IsTrimmed(text[end - 1])) end--;
        return text[start..end];

        static bool IsTrimmed(char c) => c == '​' || (!char.IsSurrogate(c) && IsWhitespace(new Rune(c)));
    }

    private static bool IsWhitespace(Rune scalar) =>
        scalar.Value is (>= 0x09 and <= 0x0D) or 0x85
        || Rune.GetUnicodeCategory(scalar) is UnicodeCategory.SpaceSeparator
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>The first scalar of a grapheme cluster; U+FFFD for an unpaired surrogate.</summary>
    private static Rune FirstScalar(string character)
    {
        Rune.DecodeFromUtf16(character, out var scalar, out _);
        return scalar;
    }
}
