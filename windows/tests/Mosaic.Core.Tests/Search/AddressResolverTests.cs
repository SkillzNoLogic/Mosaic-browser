using Mosaic.Core.Search;

namespace Mosaic.Core.Tests.Search;

/// <summary>
/// Port of <c>AddressResolverTests</c> in <c>Tests/MosaicCoreTests/SearchTests.swift</c>, followed
/// by further cases whose expected values were produced by running the Swift implementation.
/// </summary>
public class AddressResolverTests
{
    private static string? Resolve(string input) => AddressResolver.Resolve(input, SearchEngine.DuckDuckGo);

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("  example.com  ", "https://example.com")]
    [InlineData("example.com/path?q=1#top", "https://example.com/path?q=1#top")]
    [InlineData("sub.domain.co.uk", "https://sub.domain.co.uk")]
    [InlineData("https://example.com/a b", "https://duckduckgo.com/?q=https%3A%2F%2Fexample.com%2Fa%20b")]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("localhost", "http://localhost")]
    [InlineData("localhost:3000/app", "http://localhost:3000/app")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("192.168.1.1", "http://192.168.1.1")]
    [InlineData("example.com:8443", "https://example.com:8443")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("file:///tmp/x.html", "file:///tmp/x.html")]
    [InlineData("moz-extension://abc/popup.html", "moz-extension://abc/popup.html")]
    public void TreatsAsAddress(string input, string expected) => Assert.Equal(expected, Resolve(input));

    [Theory]
    [InlineData("swift ui browser")]
    [InlineData("what is 3.14")]
    [InlineData("3.14")]
    [InlineData("v1.2")]
    [InlineData("hello")]
    [InlineData("node.js tutorial")]
    [InlineData("user@example.com")]
    [InlineData("foo: bar")]
    [InlineData("example.com:notaport")]
    [InlineData("999.1.1.1")]
    public void TreatsAsSearch(string input)
    {
        Assert.StartsWith("https://duckduckgo.com/?q=", Resolve(input));
        Assert.False(AddressResolver.IsAddress(input));
    }

    [Fact]
    public void EmptyInputResolvesToNothing()
    {
        Assert.Null(Resolve(""));
        Assert.Null(Resolve("   \n"));
    }

    // ---- Beyond the Swift tests: values below are what the Swift implementation returns ----

    [Theory]
    // Typed text is kept as typed: no lower-casing, no default-port or dot-segment removal.
    [InlineData("EXAMPLE.Com/Path", "https://EXAMPLE.Com/Path")]
    [InlineData("HTTP://EXAMPLE.COM/A", "HTTP://EXAMPLE.COM/A")]
    [InlineData("ABOUT:blank", "ABOUT:blank")]
    [InlineData("FILE:///tmp/x", "FILE:///tmp/x")]
    [InlineData("file:///C:/Users/a/x.html", "file:///C:/Users/a/x.html")]
    [InlineData("www.example.com:80/a?b#c", "https://www.example.com:80/a?b#c")]
    [InlineData("example.com:0080", "https://example.com:0080")]
    [InlineData("example.com:65535", "https://example.com:65535")]
    [InlineData("example.com:00000000000000000080", "https://example.com:00000000000000000080")]
    [InlineData("http://example.com/a/../b/./c", "http://example.com/a/../b/./c")]
    [InlineData("http://exa%6dple.com/", "http://exa%6dple.com/")]
    [InlineData("www.example.com/a%2Fb%2fc", "https://www.example.com/a%2Fb%2fc")]
    [InlineData("example.com/~user/-._!$&'()*+,;=:@", "https://example.com/~user/-._!$&'()*+,;=:@")]
    [InlineData("example.com?x=1", "https://example.com?x=1")]
    [InlineData("example.com#y", "https://example.com#y")]
    [InlineData("example.com/a?b?c", "https://example.com/a?b?c")]
    [InlineData("http://user:pw@host.com/", "http://user:pw@host.com/")]
    [InlineData("http://[::1]:80/", "http://[::1]:80/")]
    [InlineData("http://", "http://")]
    [InlineData("http:example", "http:example")]
    // Other schemes that pass through.
    [InlineData("mailto:a@b.c", "mailto:a@b.c")]
    [InlineData("view-source:https://example.com", "view-source:https://example.com")]
    [InlineData("chrome://settings", "chrome://settings")]
    [InlineData("chrome-extension://abc/x.html", "chrome-extension://abc/x.html")]
    [InlineData("blob:https://x.com/uuid", "blob:https://x.com/uuid")]
    [InlineData("ftp://ftp.example.com/f", "ftp://ftp.example.com/f")]
    [InlineData("data:text/plain;base64,aGk=", "data:text/plain;base64,aGk=")]
    [InlineData("data:,", "data:,")]
    // Local names.
    [InlineData("foo.localhost", "http://foo.localhost")]
    [InlineData("FOO.LOCALHOST:80/x", "http://FOO.LOCALHOST:80/x")]
    [InlineData("01.02.03.004", "http://01.02.03.004")]
    // Domain-shaped names.
    [InlineData("a_b.com", "https://a_b.com")]
    [InlineData("xn--bcher-kva.xn--p1ai", "https://xn--bcher-kva.xn--p1ai")]
    public void KeepsValidAddressesExactlyAsTyped(string input, string expected)
    {
        Assert.Equal(expected, Resolve(input));
        Assert.True(AddressResolver.IsAddress(input));
    }

    [Theory]
    // Characters a URL cannot contain are percent-encoded, component by component.
    [InlineData("example.com/a|b", "https://example.com/a%7Cb")]
    [InlineData("example.com/a\"b", "https://example.com/a%22b")]
    [InlineData("example.com/a\\b", "https://example.com/a%5Cb")]
    [InlineData("example.com/a^b{c}`d", "https://example.com/a%5Eb%7Bc%7D%60d")]
    [InlineData("example.com/[x]", "https://example.com/%5Bx%5D")]
    [InlineData("example.com/é", "https://example.com/%C3%A9")]
    [InlineData("x.co/😀", "https://x.co/%F0%9F%98%80")]
    [InlineData("example.com/a#b#c", "https://example.com/a#b%23c")]
    [InlineData("example.com/?a=<>\"{}|\\^`", "https://example.com/?a=%3C%3E%22%7B%7D%7C%5C%5E%60")]
    [InlineData("example.com/#a=<>\"{}|\\^`[]%", "https://example.com/#a=%3C%3E%22%7B%7D%7C%5C%5E%60%5B%5D%25")]
    // A component that needs encoding is encoded whole, existing escapes included.
    [InlineData("example.com/%zz", "https://example.com/%25zz")]
    [InlineData("example.com/%41%4", "https://example.com/%2541%254")]
    // Non-hierarchical addresses may contain spaces.
    [InlineData("about:hello world", "about:hello%20world")]
    [InlineData("about:blank#a b", "about:blank#a%20b")]
    [InlineData("data:text/html,<h1>hi there</h1>", "data:text/html,%3Ch1%3Ehi%20there%3C/h1%3E")]
    [InlineData("data:text/html,a:b c", "data:text/html,a:b%20c")]
    [InlineData("mailto:a b:c", "mailto:a%20b%3Ac")]
    // An IPv6 zone identifier.
    [InlineData("http://[fe80::1%en0]/", "http://[fe80::1%25en0]/")]
    // A host that cannot be converted is percent-encoded with the rest of the address.
    [InlineData("https://ex[ample.com", "https://ex%5Bample.com")]
    [InlineData("https://bücher..de/", "https://b%C3%BCcher..de/")]
    public void RepairsAddressesTheWayFoundationDoes(string input, string expected)
    {
        Assert.Equal(expected, Resolve(input));
        Assert.True(AddressResolver.IsAddress(input));
    }

    [Theory]
    [InlineData("bücher.de", "https://xn--bcher-kva.de")]
    [InlineData("Bücher.DE/Straße", "https://xn--bcher-kva.de/Stra%C3%9Fe")]
    [InlineData("例え.テスト", "https://xn--r8jz45g.xn--zckzah")]
    [InlineData("例え.jp", "https://xn--r8jz45g.jp")]
    [InlineData("مثال.مصر", "https://xn--mgbh0fb.xn--wgbh1c")]
    // Vowel signs and tone marks are combining marks, yet part of the letters they follow.
    [InlineData("उदाहरण.भारत", "https://xn--p1b6ci4b4b3a.xn--h2brj9c")]
    [InlineData("ไทย.ไทย", "https://xn--o3cw4h.xn--o3cw4h")]
    [InlineData("e\u0301a.com", "https://xn--a-9fa.com")]
    [InlineData("テスト.localhost:00080/", "http://xn--zckzah.localhost:80/")]
    // Once an address is rebuilt its port is rewritten from its value; an empty or absurd one goes.
    [InlineData("http://é.com:0080/", "http://xn--9ca.com:80/")]
    [InlineData("http://é.com:/x", "http://xn--9ca.com/x")]
    [InlineData("http://é.com:99999999999999999999/x", "http://xn--9ca.com/x")]
    public void InternationalHostNamesBecomePunycode(string input, string expected)
    {
        Assert.Equal(expected, Resolve(input));
        Assert.True(AddressResolver.IsAddress(input));
    }

    [Theory]
    [InlineData("\u200Bexample.com\u200B")] // zero-width spaces, as pasted from a web page
    [InlineData("\u3000example.com\u00A0")]
    [InlineData("\texample.com\r\n")]
    public void IgnoresSurroundingWhitespaceOfAnyKind(string input) =>
        Assert.Equal("https://example.com", Resolve(input));

    [Theory]
    [InlineData("javascript:alert(1)", "javascript%3Aalert%281%29")]
    [InlineData("about:", "about%3A")]
    [InlineData("https:", "https%3A")]
    [InlineData("view-source:", "view-source%3A")]
    [InlineData("[::1]", "%5B%3A%3A1%5D")]
    [InlineData("example.com:65536", "example.com%3A65536")]
    [InlineData("localhost:", "localhost%3A")]
    [InlineData("localhost:80:90", "localhost%3A80%3A90")]
    [InlineData("localhost.", "localhost.")]
    [InlineData("-a.com", "-a.com")]
    [InlineData("a-.com", "a-.com")]
    [InlineData("a.b1", "a.b1")]
    [InlineData("a.xn--", "a.xn--")]
    [InlineData("x.c", "x.c")]
    [InlineData("a..com", "a..com")]
    [InlineData(".com", ".com")]
    [InlineData("example.com.", "example.com.")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3.4.5", "1.2.3.4.5")]
    [InlineData("256.1.1.1", "256.1.1.1")]
    [InlineData("example。com", "example%E3%80%82com")]
    [InlineData("😀.com", "%F0%9F%98%80.com")]
    [InlineData("a.e\u0301", "a.e%CC%81")] // one letter is not a top-level domain, however it is encoded
    [InlineData("a.é", "a.%C3%A9")]
    [InlineData("user:pw@host.com", "user%3Apw%40host.com")]
    [InlineData("file:///tmp/a b.html", "file%3A%2F%2F%2Ftmp%2Fa%20b.html")]
    [InlineData("chrome://settings/a b", "chrome%3A%2F%2Fsettings%2Fa%20b")]
    [InlineData("https://example.com/\ttab", "https%3A%2F%2Fexample.com%2F%09tab")]
    [InlineData("example.com/a b", "example.com%2Fa%20b")]
    [InlineData("http://example.com:abc/", "http%3A%2F%2Fexample.com%3Aabc%2F")]
    [InlineData("http://[::1]x/", "http%3A%2F%2F%5B%3A%3A1%5Dx%2F")]
    // Only ASCII case is folded when recognising schemes and "localhost".
    [InlineData("httpſ://example.com", "http%C5%BF%3A%2F%2Fexample.com")]
    [InlineData("localhoſt", "localho%C5%BFt")]
    public void SearchesForEverythingElse(string input, string encodedQuery)
    {
        Assert.Equal($"https://duckduckgo.com/?q={encodedQuery}", Resolve(input));
        Assert.False(AddressResolver.IsAddress(input));
    }

    [Theory]
    [InlineData("١.١.١.١")] // Arabic-Indic digits: numbers, but not an IPv4 address
    [InlineData("½.1.1.1")]
    public void NumbersOfOtherScriptsAreSearchedRatherThanTrapping(string input)
    {
        // The macOS implementation force-unwraps Int(part) here and crashes.
        Assert.StartsWith("https://duckduckgo.com/?q=", Resolve(input));
        Assert.False(AddressResolver.IsAddress(input));
    }

    [Fact]
    public void UsesTheGivenSearchEngine() =>
        Assert.Equal("https://kagi.com/search?q=two%20words", AddressResolver.Resolve("two words", SearchEngine.Kagi));

    [Fact]
    public void WhitespaceOnlyInputIsNotAnAddress()
    {
        Assert.False(AddressResolver.IsAddress(""));
        Assert.False(AddressResolver.IsAddress(" \t"));
    }
}
