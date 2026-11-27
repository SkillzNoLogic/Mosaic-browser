using System.Globalization;
using System.Text;

namespace Mosaic.Core.Search;

/// <summary>
/// A URL string split and repaired the way Apple's Foundation does it in <c>URL(string:)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The macOS implementation passes everything it opens through <c>URL(string:)</c> and hands the
/// engines its <c>absoluteString</c>. Foundation keeps a valid RFC 3986 string byte for byte, and
/// otherwise repairs it one component at a time: characters a component may not contain are
/// percent-encoded as UTF-8, and a host with such characters is converted to its IDNA (Punycode)
/// form. A string it cannot repair is rejected. This type reproduces exactly that, so both
/// platforms hand the engines identical URLs; it is a port of <c>RFC3986Parser</c> in
/// swift-foundation (<c>Sources/FoundationEssentials/URL/URLParser.swift</c>).
/// </para>
/// <para>
/// <see cref="System.Uri"/> is deliberately not involved: it lower-cases hosts, removes default
/// ports and dot segments, and re-escapes paths, none of which Foundation does.
/// </para>
/// </remarks>
internal sealed class FoundationUrl
{
    private const string Alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const string Digit = "0123456789";
    private const string Unreserved = Alpha + Digit + "-._~";
    private const string SubDelimiters = "!$&'()*+,;=";

    private static readonly UInt128 SchemeAllowed = Mask(Alpha + Digit + "+-.");
    private static readonly UInt128 UserInfoAllowed = Mask(Unreserved + SubDelimiters);
    private static readonly UInt128 HostAllowed = Mask(Unreserved + SubDelimiters);
    private static readonly UInt128 IpLiteralAllowed = Mask(Unreserved + SubDelimiters + ":");
    private static readonly UInt128 UnreservedAllowed = Mask(Unreserved);
    private static readonly UInt128 PathAllowed = Mask(Unreserved + SubDelimiters + ":@/");
    private static readonly UInt128 PathFirstSegmentAllowed = Mask(Unreserved + SubDelimiters + "@/");
    private static readonly UInt128 QueryAllowed = Mask(Unreserved + SubDelimiters + ":@/?");

    private static readonly UInt128 QueryItemAllowed = Mask(Unreserved + "!$'()*+,;" + ":@/?");

    /// <summary>Foundation's <c>CharacterSet.urlFragmentAllowed</c>.</summary>
    private static readonly UInt128 FragmentAllowed = QueryAllowed;

    /// <summary>
    /// Schemes whose host Foundation percent-encodes instead of converting with IDNA.
    /// </summary>
    private static readonly HashSet<string> SchemesWithPercentEncodedHost =
    [
        "tel", "telemergencycall", "telprompt", "callto", "facetime", "facetime-prompt", "facetime-audio",
        "facetime-audio-prompt", "imap", "pop", "addressbook", "contact", "phasset", "http+unix", "https+unix",
        "ws+unix", "wss+unix",
    ];

    /// <summary>Foundation gives up on hosts longer than this many UTF-8 bytes.</summary>
    private const int MaximumIdnaHostLength = 2048;

    private static readonly IdnMapping Idna = new();

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Components _parts;

    private FoundationUrl(string text, Components parts)
    {
        AbsoluteString = text;
        _parts = parts;
    }

    /// <summary>The URL as Foundation's <c>absoluteString</c> would report it.</summary>
    public string AbsoluteString { get; }

    /// <summary>The host as written in <see cref="AbsoluteString"/> (still encoded), or null without an authority.</summary>
    public string? EncodedHost => Slice(_parts.Host);

    /// <summary>The path as written in <see cref="AbsoluteString"/> (still encoded); empty when there is none.</summary>
    public string EncodedPath => AbsoluteString[_parts.Path];

    /// <summary>
    /// Parses <paramref name="text"/> as <c>URL(string:)</c> does, repairing invalid characters.
    /// </summary>
    /// <returns>Null where Foundation returns nil: an empty string, or one that cannot be repaired
    /// (a non-numeric port, a malformed scheme or IP literal, a host IDNA rejects).</returns>
    public static FoundationUrl? Parse(string text) => Parse(text, lenient: true);

    /// <summary>
    /// Parses <paramref name="text"/> as <c>URLComponents(string:)</c> does. It repairs like
    /// <see cref="Parse(string)"/> but splits by the letter of RFC 3986: a leading ":" starts a
    /// path rather than following an empty scheme, and "host:" has no port.
    /// </summary>
    public static FoundationUrl? ParseStrict(string text) => Parse(text, lenient: false);

    private static FoundationUrl? Parse(string text, bool lenient)
    {
        if (text.Length == 0 || Split(text, lenient) is not { } components) return null;

        var original = new FoundationUrl(text, components);
        var repaired = original.Repair();
        if (repaired is null) return null;
        if (ReferenceEquals(repaired, text)) return original;
        return Split(repaired, lenient) is { } repairedComponents ? new FoundationUrl(repaired, repairedComponents) : null;
    }

    /// <summary>
    /// Foundation's <c>addingPercentEncoding(withAllowedCharacters: .urlFragmentAllowed)</c>:
    /// escapes everything a URL fragment may not contain, including existing <c>%</c> signs.
    /// </summary>
    public static string EscapeForFragment(string text) => PercentEncode(text, FragmentAllowed);

    /// <summary>
    /// Percent-encodes every UTF-8 byte of <paramref name="text"/> except the RFC 3986 unreserved
    /// characters (A–Z a–z 0–9 - . _ ~), making it safe as a single query-string value.
    /// </summary>
    public static string EscapeQueryValue(string text) => PercentEncode(text, UnreservedAllowed);

    /// <summary>
    /// Percent-encodes a query item's name or value as <c>URLComponents.queryItems</c> does:
    /// what a query may contain stays literal, except the "&amp;" and "=" that delimit items.
    /// </summary>
    public static string EscapeQueryItem(string text) => PercentEncode(text, QueryItemAllowed);

    /// <summary>
    /// Reverses percent-encoding as <c>URLComponents</c> does for its decoded accessors.
    /// </summary>
    /// <returns>Null when an escape is malformed or the decoded bytes are not UTF-8.</returns>
    public static string? PercentDecode(string text)
    {
        if (!text.Contains('%')) return text;

        var source = Encoding.UTF8.GetBytes(text);
        var decoded = new byte[source.Length];
        var length = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != (byte)'%')
            {
                decoded[length++] = source[i];
                continue;
            }
            if (i + 2 >= source.Length || !char.IsAsciiHexDigit((char)source[i + 1]) || !char.IsAsciiHexDigit((char)source[i + 2]))
            {
                return null;
            }
            decoded[length++] = (byte)((HexValue(source[i + 1]) << 4) | HexValue(source[i + 2]));
            i += 2;
        }

        try
        {
            return StrictUtf8.GetString(decoded, 0, length);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        static int HexValue(byte digit) => digit <= (byte)'9' ? digit - '0' : (digit | 0x20) - 'a' + 10;
    }

    // ---- Splitting ----

    /// <summary>Where each component sits in the string. Delimiters are not part of any range.</summary>
    private struct Components
    {
        public Range? Scheme, User, Password, Host, Port, Query, Fragment;
        public Range Path;
        public bool IsIpLiteral;
    }

    /// <summary>
    /// Finds the components by their delimiters alone, validating only the two parts that cannot
    /// be repaired afterwards (scheme and port). Null when either is malformed.
    /// </summary>
    /// <param name="text">The string to split.</param>
    /// <param name="lenient">Accept an empty scheme (":x") and an empty port ("host:"), as
    /// <c>URL</c> does and <c>URLComponents</c> does not.</param>
    private static Components? Split(string text, bool lenient)
    {
        var components = new Components();
        var end = text.Length;
        var index = 0;

        // Scheme: everything before the first ":", unless an authority or path delimiter comes first.
        while (index < end)
        {
            var c = text[index];
            if (c == ':')
            {
                if (index > 0 || lenient)
                {
                    components.Scheme = ..index;
                    index++;
                }
                break;
            }
            if (c is '/' or '?' or '#' or '[' or ']' or '@')
            {
                index = 0;
                break;
            }
            index++;
        }
        if (components.Scheme is { } scheme)
        {
            var name = text.AsSpan(scheme);
            if (name.Length > 0 && (name[0] < 'A' || !IsValid(name, SchemeAllowed, percentEncodingAllowed: false))) return null;
            if (index == end)
            {
                components.Path = end..end;
                return components;
            }
        }
        else
        {
            index = 0;
        }

        // Authority: "//" up to the next "/", "?" or "#".
        if (index + 1 < end && text[index] == '/' && text[index + 1] == '/')
        {
            index += 2;
            var authorityStart = index;
            while (index < end && text[index] is not ('/' or '?' or '#')) index++;

            if (index == authorityStart)
            {
                components.Host = authorityStart..authorityStart;
            }
            else
            {
                if (!SplitAuthority(text, authorityStart, index, lenient, ref components)) return null;
                if (components.Port is { } port && !IsValidPort(text.AsSpan(port), components.Scheme is { } s ? text.AsSpan(s) : default))
                {
                    return null;
                }
            }
        }

        var pathStart = index;
        while (index < end && text[index] is not ('?' or '#')) index++;
        components.Path = pathStart..index;
        if (index == end) return components;

        if (text[index] == '?')
        {
            var hash = text.IndexOf('#', index + 1);
            if (hash < 0)
            {
                components.Query = (index + 1)..end;
            }
            else
            {
                components.Query = (index + 1)..hash;
                components.Fragment = (hash + 1)..end;
            }
        }
        else
        {
            components.Fragment = (index + 1)..end;
        }
        return components;
    }

    /// <summary>Splits <c>user:password@host:port</c>. False when text follows an IP literal's "]".</summary>
    private static bool SplitAuthority(string text, int start, int end, bool lenient, ref Components components)
    {
        var hostStart = start;
        var hostEnd = end;

        var at = text.LastIndexOf('@', end - 1, end - start);
        if (at >= 0)
        {
            var colon = text.IndexOf(':', start, at - start);
            if (colon >= 0)
            {
                components.User = start..colon;
                components.Password = (colon + 1)..at;
            }
            else
            {
                components.User = start..at;
            }
            hostStart = at + 1;
        }

        var closingBracket = hostStart < end && text[hostStart] == '[' ? text.IndexOf(']', hostStart, end - hostStart) : -1;
        if (closingBracket >= 0)
        {
            components.IsIpLiteral = true;
            hostEnd = closingBracket + 1;
            if (hostEnd != end)
            {
                if (text[hostEnd] != ':') return false;
                components.Port = (hostEnd + 1)..end;
            }
        }
        else if (text.IndexOf(':', hostStart, end - hostStart) is >= 0 and var portColon)
        {
            hostEnd = portColon;
            if (portColon + 1 < end || lenient) components.Port = (portColon + 1)..end;
        }

        components.Host = hostStart..hostEnd;
        return true;
    }

    // ---- Repairing ----

    /// <summary>
    /// Returns the string with every invalid component encoded: the very same instance when
    /// nothing needed repair, or null when a component is beyond repair.
    /// </summary>
    private string? Repair()
    {
        var user = Slice(_parts.User);
        var password = Slice(_parts.Password);
        var host = Slice(_parts.Host);
        var path = EncodedPath;
        var query = Slice(_parts.Query);
        var fragment = Slice(_parts.Fragment);

        var userValid = user is null || IsValid(user, UserInfoAllowed);
        var passwordValid = password is null || IsValid(password, UserInfoAllowed);
        var hostValid = host is null || IsValidHost(host, _parts.IsIpLiteral);
        var pathValid = IsValid(path, PathAllowed);
        var queryValid = query is null || IsValid(query, QueryAllowed);
        var fragmentValid = fragment is null || IsValid(fragment, FragmentAllowed);
        if (userValid && passwordValid && hostValid && pathValid && queryValid && fragmentValid) return AbsoluteString;

        var scheme = Slice(_parts.Scheme);
        var repaired = new StringBuilder(AbsoluteString.Length + 16);
        if (scheme is not null) repaired.Append(scheme).Append(':');
        if (host is not null) repaired.Append("//");

        if (user is not null)
        {
            repaired.Append(userValid ? user : PercentEncode(user, UserInfoAllowed));
            if (password is not null)
            {
                repaired.Append(':').Append(passwordValid ? password : PercentEncode(password, UserInfoAllowed));
            }
            repaired.Append('@');
        }

        if (host is not null)
        {
            var repairedHost = hostValid ? host : RepairHost(host, scheme);
            if (repairedHost is null) return null;
            repaired.Append(repairedHost);
        }

        // Foundation re-writes the port from its numeric value, so "0080" becomes "80" and a
        // port that is empty or too large for an integer disappears.
        if (long.TryParse(Slice(_parts.Port), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            repaired.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        }
        repaired.Append(pathValid ? path : PercentEncodePath(path));
        if (query is not null) repaired.Append('?').Append(queryValid ? query : PercentEncode(query, QueryAllowed));
        if (fragment is not null) repaired.Append('#').Append(fragmentValid ? fragment : PercentEncode(fragment, FragmentAllowed));
        return repaired.ToString();
    }

    private string? RepairHost(string host, string? scheme)
    {
        if (_parts.IsIpLiteral)
        {
            // Only the zone ID of an IP literal ("[fe80::1%en0]") can be encoded.
            var percent = host.IndexOf('%');
            var encoded = percent < 0 ? host : string.Concat(host.AsSpan(0, percent), PercentEncode(host[percent..^1], UnreservedAllowed), "]");
            return IsValidHost(encoded, isIpLiteral: true) ? encoded : null;
        }

        if (scheme is not null && Ascii.IsValid(scheme) && SchemesWithPercentEncodedHost.Contains(scheme.ToLowerInvariant()))
        {
            return PercentEncode(host, HostAllowed);
        }

        return ToIdna(host) is { } ascii && IsValid(ascii, HostAllowed) ? ascii : null;
    }

    /// <summary>
    /// IDNA (UTS #46, non-transitional) conversion of a whole host name; null when it is rejected.
    /// </summary>
    private static string? ToIdna(string host)
    {
        if (Encoding.UTF8.GetByteCount(host) > MaximumIdnaHostLength) return null;
        try
        {
            var ascii = Idna.GetAscii(host);
            return ascii.Length > 0 ? ascii : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Encodes a path. Without a scheme-like first segment in mind, Foundation escapes ":" before
    /// the first "/" so the result can never be mistaken for a scheme.
    /// </summary>
    private static string PercentEncodePath(string path)
    {
        var slash = path.IndexOf('/');
        return slash switch
        {
            < 0 => PercentEncode(path, PathFirstSegmentAllowed),
            0 => PercentEncode(path, PathAllowed),
            _ => PercentEncode(path[..slash], PathFirstSegmentAllowed) + PercentEncode(path[slash..], PathAllowed),
        };
    }

    // ---- Character classes ----

    private static UInt128 Mask(string characters)
    {
        UInt128 mask = 0;
        foreach (var c in characters) mask |= UInt128.One << c;
        return mask;
    }

    private static bool Allows(UInt128 mask, int codeUnit) => codeUnit < 128 && (mask & (UInt128.One << codeUnit)) != 0;

    /// <summary>
    /// True when every character is allowed; with <paramref name="percentEncodingAllowed"/>, a
    /// "%" is also accepted provided two hex digits follow it.
    /// </summary>
    private static bool IsValid(ReadOnlySpan<char> text, UInt128 allowed, bool percentEncodingAllowed = true)
    {
        var hexDigitsRequired = 0;
        foreach (var c in text)
        {
            if (percentEncodingAllowed && c == '%')
            {
                if (hexDigitsRequired != 0) return false;
                hexDigitsRequired = 2;
            }
            else if (!Allows(allowed, c))
            {
                return false;
            }
            else if (hexDigitsRequired > 0)
            {
                if (!char.IsAsciiHexDigit(c)) return false;
                hexDigitsRequired--;
            }
        }
        return hexDigitsRequired == 0;
    }

    private static bool IsValidHost(string host, bool isIpLiteral)
    {
        if (!isIpLiteral) return IsValid(host, HostAllowed);

        // "[" address [ "%25" zone-id ] "]"
        var inner = host.AsSpan(1, host.Length - 2);
        var percent = inner.IndexOf('%');
        if (percent < 0) return IsValid(inner, IpLiteralAllowed, percentEncodingAllowed: false);
        return inner[percent..].StartsWith("%25")
            && IsValid(inner[..percent], IpLiteralAllowed, percentEncodingAllowed: false)
            && IsValid(inner[(percent + 3)..], UnreservedAllowed);
    }

    private static bool IsValidPort(ReadOnlySpan<char> port, ReadOnlySpan<char> scheme) =>
        port.IndexOfAnyExceptInRange('0', '9') < 0 || scheme is "addressbook";

    private static string PercentEncode(string text, UInt128 allowed)
    {
        const string Hex = "0123456789ABCDEF";
        var encoded = new StringBuilder(text.Length + 8);
        // An unpaired surrogate becomes U+FFFD here, as it would entering a Swift string.
        foreach (var unit in Encoding.UTF8.GetBytes(text))
        {
            if (Allows(allowed, unit))
            {
                encoded.Append((char)unit);
            }
            else
            {
                encoded.Append('%').Append(Hex[unit >> 4]).Append(Hex[unit & 0xF]);
            }
        }
        return encoded.ToString();
    }

    private string? Slice(Range? range) => range is { } r ? AbsoluteString[r] : null;
}
