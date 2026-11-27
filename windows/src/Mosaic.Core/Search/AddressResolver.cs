using System.Collections.Frozen;
using System.Text;

namespace Mosaic.Core.Search;

/// <summary>
/// Turns whatever was typed into the address bar into a URL: either the address itself,
/// or a search on the current search engine.
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Search/AddressResolver.swift</c>; the expected behaviour is
/// pinned down by <c>Tests/MosaicCoreTests/SearchTests.swift</c>. URLs are returned as strings
/// exactly as typed (plus a scheme when one was missing), never re-normalised. The one change
/// made to an address is the one the macOS version makes through Foundation's <c>URL</c>:
/// characters a URL cannot contain are percent-encoded and an international host name is
/// converted to Punycode (see <see cref="FoundationUrl"/>).
/// </remarks>
public static class AddressResolver
{
    /// <summary>Schemes that are passed through untouched when typed explicitly.</summary>
    private static readonly FrozenSet<string> PassthroughSchemes = FrozenSet.ToFrozenSet(
    [
        "http", "https", "file", "about", "data", "blob", "view-source",
        "chrome", "chrome-extension", "moz-extension", "ftp", "mailto",
    ]);

    /// <summary>Resolves address bar input to the URL to load.</summary>
    /// <param name="input">The text as typed; surrounding whitespace is ignored.</param>
    /// <param name="searchEngine">The engine to search with when the input is not an address.</param>
    /// <returns>The URL to load, or null for empty input.</returns>
    public static string? Resolve(string input, SearchEngine searchEngine)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(searchEngine);

        var text = SwiftText.Trim(input);
        if (text.Length == 0) return null;

        var characters = SwiftText.Characters(text);
        return ExplicitUrl(text, characters) ?? HostLikeUrl(text, characters) ?? searchEngine.SearchUrl(text);
    }

    /// <summary>True when the input would be opened as an address rather than searched.</summary>
    public static bool IsAddress(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = SwiftText.Trim(input);
        var characters = SwiftText.Characters(text);
        return ExplicitUrl(text, characters) is not null || HostLikeUrl(text, characters) is not null;
    }

    /// <summary>An address typed with one of the <see cref="PassthroughSchemes"/>.</summary>
    private static string? ExplicitUrl(string text, ReadOnlySpan<string> characters)
    {
        var colon = characters.IndexOf(":");
        if (colon < 0) return null;

        var scheme = string.Concat(characters[..colon]);
        if (!Ascii.IsValid(scheme) || !PassthroughSchemes.Contains(scheme.ToLowerInvariant())) return null;

        // "about:blank" and "data:..." legitimately contain spaces rarely; anything
        // hierarchical with whitespace is a search ("https is great: why").
        var rest = characters[(colon + 1)..];
        if (rest is ["/", "/", ..] && ContainsWhitespace(characters)) return null;
        if (rest.IsEmpty) return null;

        return (FoundationUrl.Parse(text) ?? FoundationUrl.Parse(FoundationUrl.EscapeForFragment(text)))?.AbsoluteString;
    }

    /// <summary>A bare <c>host[:port][/path]</c>, given the scheme the user left out.</summary>
    private static string? HostLikeUrl(string text, ReadOnlySpan<string> characters)
    {
        if (ContainsWhitespace(characters)) return null;

        // Split "host[:port]" from "/path?query#fragment".
        var authorityEnd = characters.IndexOfAny("/", "?", "#");
        var authority = authorityEnd < 0 ? characters : characters[..authorityEnd];
        if (authority.IsEmpty || authority.Contains("@")) return null;

        var host = authority;
        var colon = authority.LastIndexOf(":");
        if (colon >= 0)
        {
            if (!IsPort(authority[(colon + 1)..])) return null;
            host = authority[..colon];
        }
        if (host.IsEmpty) return null;

        var isLocal = EqualsIgnoringAsciiCase(host, "localhost") || EndsWithIgnoringAsciiCase(host, ".localhost") || IsIPv4(host);
        if (!isLocal && !LooksLikeDomain(host)) return null;

        return FoundationUrl.Parse((isLocal ? "http://" : "https://") + text)?.AbsoluteString;
    }

    /// <summary>One to any number of ASCII digits whose value fits a TCP port.</summary>
    private static bool IsPort(ReadOnlySpan<string> characters)
    {
        if (characters.IsEmpty) return false;

        var port = 0;
        foreach (var character in characters)
        {
            if (character is not [>= '0' and <= '9']) return false;
            port = port * 10 + (character[0] - '0');
            if (port > ushort.MaxValue) return false;
        }
        return true;
    }

    private static bool IsIPv4(ReadOnlySpan<string> host)
    {
        var parts = 0;
        foreach (var part in Split(host))
        {
            parts++;
            // Digits of other scripts are numbers too, but no IPv4 address is written with them.
            // (The macOS version traps on them; here they simply are not an address.)
            if (part.Length is 0 or > 3 || part.Any(character => character is not [>= '0' and <= '9'])) return false;
            if (int.Parse(string.Concat(part)) > 255) return false;
        }
        return parts == 4;
    }

    private static bool LooksLikeDomain(ReadOnlySpan<string> host)
    {
        var labels = Split(host);
        if (labels.Count < 2) return false;

        // A TLD is letters only (or punycode); "3.14" and "v1.2" are searches.
        var tld = labels[^1];
        var tldOk = tld.Length >= 2 && (tld.All(SwiftText.IsLetter) || StartsWithIgnoringAsciiCase(tld, "xn--"));
        return tldOk && labels.All(label =>
            label.Length is > 0 and <= 63
            && label.All(character => SwiftText.IsLetter(character) || SwiftText.IsNumber(character) || character is "-" or "_")
            && label[0] != "-" && label[^1] != "-");
    }

    private static bool ContainsWhitespace(ReadOnlySpan<string> characters)
    {
        foreach (var character in characters)
        {
            if (SwiftText.IsWhitespace(character)) return true;
        }
        return false;
    }

    /// <summary>Splits on "." keeping empty pieces, so "a..b" has an empty label.</summary>
    private static List<string[]> Split(ReadOnlySpan<string> characters)
    {
        var pieces = new List<string[]>();
        while (true)
        {
            var dot = characters.IndexOf(".");
            if (dot < 0) break;
            pieces.Add(characters[..dot].ToArray());
            characters = characters[(dot + 1)..];
        }
        pieces.Add(characters.ToArray());
        return pieces;
    }

    // Swift lower-cases before comparing with these ASCII names. Unicode case folding is not
    // equivalent ("ſ" upper-cases to "S" but never lower-cases to "s"), so only ASCII is folded.

    private static bool EqualsIgnoringAsciiCase(ReadOnlySpan<string> characters, string ascii)
    {
        if (characters.Length != ascii.Length) return false;
        for (var i = 0; i < ascii.Length; i++)
        {
            if (characters[i] is not [var c] || !char.IsAscii(c) || char.ToLowerInvariant(c) != ascii[i]) return false;
        }
        return true;
    }

    private static bool StartsWithIgnoringAsciiCase(ReadOnlySpan<string> characters, string ascii) =>
        characters.Length >= ascii.Length && EqualsIgnoringAsciiCase(characters[..ascii.Length], ascii);

    private static bool EndsWithIgnoringAsciiCase(ReadOnlySpan<string> characters, string ascii) =>
        characters.Length >= ascii.Length && EqualsIgnoringAsciiCase(characters[^ascii.Length..], ascii);
}
