using System.Globalization;
using System.Text;

namespace Mosaic.Core.Platform;

/// <summary>
/// The ordered list of languages web pages are asked for (the <c>Accept-Language</c> header and
/// <c>navigator.languages</c>), as BCP 47 tags: "en-GB", "fr", "zh-Hant-TW". Port of
/// <c>Sources/MosaicCore/Platform/LanguagePreferences.swift</c>.
/// </summary>
public sealed class LanguagePreferences : IEquatable<LanguagePreferences>
{
    private readonly List<string> tags = [];

    public LanguagePreferences(IEnumerable<string> tags)
    {
        foreach (var tag in tags) Add(tag);
    }

    public IReadOnlyList<string> Tags => tags;

    /// <summary>
    /// Built from the system's preferred languages (on Windows, the user's language list in
    /// Settings › Time &amp; language). A regional tag is followed by its base language, the way
    /// browsers fill the list ("en-AU" → "en-AU, en"), so sites that only know "en" still match.
    /// </summary>
    public static LanguagePreferences FromSystem(IEnumerable<string> preferred)
    {
        var result = new LanguagePreferences([]);
        foreach (var tag in preferred)
        {
            if (Normalize(tag) is not { } normalized) continue;
            result.Add(normalized);
            var languageBase = normalized.Split('-')[0];
            if (languageBase != normalized) result.Add(languageBase);
        }
        if (result.tags.Count == 0)
        {
            result.Add("en-US");
            result.Add("en");
        }
        return result;
    }

    /// <summary>Adds <paramref name="tag"/> at the end. False if it is not a language tag or is already listed.</summary>
    public bool Add(string tag)
    {
        if (Normalize(tag) is not { } normalized || tags.Contains(normalized)) return false;
        tags.Add(normalized);
        return true;
    }

    /// <summary>The last language cannot be removed: pages must be asked for something.</summary>
    public bool Remove(string tag)
    {
        if (tags.Count <= 1) return false;
        return tags.Remove(tag);
    }

    public void Move(string tag, int offset)
    {
        var index = tags.IndexOf(tag);
        if (index < 0) return;
        var target = Math.Clamp(index + offset, 0, tags.Count - 1);
        if (target == index) return;
        tags.RemoveAt(index);
        tags.Insert(target, tag);
    }

    public LanguagePreferences Copy() => new(tags);

    /// <summary>
    /// The value of Gecko's <c>intl.accept_languages</c> preference and of Chromium's
    /// <c>--accept-lang</c> switch: the tags in order, comma-separated. Both engines derive the
    /// header's quality values from the order themselves.
    /// </summary>
    public string PreferenceValue => string.Join(',', tags);

    /// <summary>The header pages receive, with quality values as browsers send them ("en-GB,en;q=0.9,fr;q=0.8").</summary>
    public string AcceptLanguageHeader
    {
        get
        {
            var builder = new StringBuilder();
            for (var index = 0; index < tags.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append(tags[index]);
                if (index > 0)
                {
                    var quality = Math.Max(0.1, 1.0 - index * 0.1);
                    builder.Append(";q=").Append(quality.ToString("0.0", CultureInfo.InvariantCulture));
                }
            }
            return builder.ToString();
        }
    }

    /// <summary>Canonical case for a BCP 47 tag ("EN_us" → "en-US", "zh-hant-tw" → "zh-Hant-TW"), or null if it is not shaped like one.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null) return null;
        var parts = input.Trim().Replace('_', '-').Split('-');
        var language = parts[0];
        if (language.Length is < 2 or > 3 || !language.All(char.IsAsciiLetter)) return null;
        var result = new List<string> { language.ToLowerInvariant() };
        foreach (var part in parts.Skip(1))
        {
            if (part.Length is < 1 or > 8 || !part.All(char.IsAsciiLetterOrDigit)) return null;
            if (part.Length == 4 && part.All(char.IsAsciiLetter))
            {
                result.Add(char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant());  // script
            }
            else if (part.Length == 2 && part.All(char.IsAsciiLetter))
            {
                result.Add(part.ToUpperInvariant());  // region
            }
            else
            {
                result.Add(part.ToLowerInvariant());
            }
        }
        return string.Join('-', result);
    }

    /// <summary>Languages offered in the "Add language" menu (same list as macOS). Anything else can be typed as a tag.</summary>
    public static readonly IReadOnlyList<string> Common =
    [
        "af", "ar", "bg", "bn", "ca", "cs", "cy", "da", "de", "de-AT", "de-CH", "de-DE", "el",
        "en", "en-AU", "en-CA", "en-GB", "en-IN", "en-NZ", "en-US", "es", "es-ES", "es-MX", "es-419",
        "et", "eu", "fa", "fi", "fil", "fr", "fr-CA", "fr-CH", "fr-FR", "ga", "gl", "gu", "he", "hi",
        "hr", "hu", "hy", "id", "is", "it", "ja", "ka", "kk", "km", "kn", "ko", "lt", "lv", "mk",
        "ml", "mr", "ms", "mt", "nb", "ne", "nl", "nl-BE", "pa", "pl", "pt", "pt-BR", "pt-PT", "ro",
        "ru", "si", "sk", "sl", "sq", "sr", "sv", "sw", "ta", "te", "th", "tr", "uk", "ur", "uz",
        "vi", "zh", "zh-CN", "zh-HK", "zh-TW",
    ];

    /// <summary>"English (United Kingdom)" for "en-GB"; the tag itself when the system does not know it.</summary>
    public static string DisplayName(string tag)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            // An unknown tag can come back as a culture whose name is the tag in brackets.
            return culture.EnglishName is { Length: > 0 } name && !name.StartsWith("Unknown", StringComparison.Ordinal)
                && !culture.EnglishName.Equals(tag, StringComparison.OrdinalIgnoreCase)
                ? name
                : tag;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    public bool Equals(LanguagePreferences? other) => other is not null && tags.SequenceEqual(other.tags);
    public override bool Equals(object? obj) => obj is LanguagePreferences other && Equals(other);
    public override int GetHashCode() => tags.Count == 0 ? 0 : tags[0].GetHashCode(StringComparison.Ordinal);
    public override string ToString() => PreferenceValue;
}
