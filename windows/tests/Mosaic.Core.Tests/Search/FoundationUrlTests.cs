using Mosaic.Core.Search;

namespace Mosaic.Core.Tests.Search;

/// <summary>
/// Checks the port of Foundation's URL parsing directly. Expected values are what
/// <c>URL(string:)?.absoluteString</c> returns on macOS.
/// </summary>
public class FoundationUrlTests
{
    [Theory]
    [InlineData("https://example.com/a?b#c", "example.com", "/a")]
    [InlineData("https://user:pw@example.com:8080/a", "example.com", "/a")]
    [InlineData("http://[::1]:80/x", "[::1]", "/x")]
    [InlineData("//example.com/x", "example.com", "/x")]
    [InlineData("file:///tmp/x", "", "/tmp/x")]
    [InlineData("about:blank", null, "blank")]
    [InlineData("example.com/x", null, "example.com/x")]
    [InlineData("?q", null, "")]
    public void SplitsIntoComponents(string text, string? host, string path)
    {
        var url = FoundationUrl.Parse(text);
        Assert.NotNull(url);
        Assert.Equal(text, url.AbsoluteString);
        Assert.Equal(host, url.EncodedHost);
        Assert.Equal(path, url.EncodedPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1http://example.com")]      // a scheme starts with a letter
    [InlineData("ht tp://example.com")]
    [InlineData("http://example.com:8o/")]   // a port is digits
    [InlineData("http://[::1]x/")]           // nothing but a port may follow an IP literal
    [InlineData("http://[a\"b]/")]           // an IP literal cannot be repaired
    public void RejectsWhatCannotBeRepaired(string text) => Assert.Null(FoundationUrl.Parse(text));

    [Fact]
    public void TheLastAtSignEndsTheUserInformation()
    {
        var url = FoundationUrl.Parse("https://a@b@example.com/");
        Assert.Equal("https://a%40b@example.com/", url?.AbsoluteString);
        Assert.Equal("example.com", url?.EncodedHost);
    }

    [Fact]
    public void TheStrictParserDoesNotAcceptAnEmptySchemeOrPort()
    {
        Assert.Equal("example.com", FoundationUrl.Parse("://example.com/x")?.EncodedHost);
        Assert.Null(FoundationUrl.ParseStrict("://example.com/x")?.EncodedHost);

        // With a host that needs rebuilding, the strict parser drops the dangling colon.
        Assert.Equal("http://xn--9ca.com/", FoundationUrl.ParseStrict("http://é.com:/")?.AbsoluteString);
    }

    [Fact]
    public void SomeSchemesPercentEncodeTheHostInsteadOfUsingPunycode()
    {
        Assert.Equal("https://xn--9ca.com", FoundationUrl.Parse("https://é.com")?.AbsoluteString);
        Assert.Equal("imap://%C3%A9.com", FoundationUrl.Parse("imap://é.com")?.AbsoluteString);
    }

    [Fact]
    public void AColonInTheFirstPathSegmentIsEncodedSoItCannotPassForAScheme() =>
        Assert.Equal("a@b%20c%3Ad/e:f", FoundationUrl.Parse("a@b c:d/e:f")?.AbsoluteString);

    [Theory]
    [InlineData("a%20b", "a b")]
    [InlineData("%E6%97%A5", "日")]
    [InlineData("plain", "plain")]
    [InlineData("%", null)]
    [InlineData("%4", null)]
    [InlineData("%zz", null)]
    [InlineData("%FF", null)] // not UTF-8
    public void PercentDecodes(string encoded, string? decoded) =>
        Assert.Equal(decoded, FoundationUrl.PercentDecode(encoded));

    [Fact]
    public void EscapesForEachContext()
    {
        const string Everything = "aZ09-._~!$&'()*+,;=:@/?#[]%\" é";
        Assert.Equal("aZ09-._~%21%24%26%27%28%29%2A%2B%2C%3B%3D%3A%40%2F%3F%23%5B%5D%25%22%20%C3%A9", FoundationUrl.EscapeQueryValue(Everything));
        Assert.Equal("aZ09-._~!$%26'()*+,;%3D:@/?%23%5B%5D%25%22%20%C3%A9", FoundationUrl.EscapeQueryItem(Everything));
        Assert.Equal("aZ09-._~!$&'()*+,;=:@/?%23%5B%5D%25%22%20%C3%A9", FoundationUrl.EscapeForFragment(Everything));
    }
}
