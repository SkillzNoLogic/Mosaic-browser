using Mosaic.Core.Blocker;
using Mosaic.Core.Privacy;

namespace Mosaic.Core.Tests.Privacy;

/// <summary>Port of <c>Tests/MosaicCoreTests/PrivacyTests.swift</c> (tracking parameters, navigation policy, DNS, site data).</summary>
public sealed class TrackingParameterTests
{
    [Theory]
    [InlineData("https://example.com/a?fbclid=123", "https://example.com/a")]
    [InlineData("https://example.com/a?id=7&utm_source=news&utm_medium=email", "https://example.com/a?id=7")]
    [InlineData("https://example.com/?gclid=x&q=shoes#reviews", "https://example.com/?q=shoes#reviews")]
    [InlineData("https://example.com/?UTM_Campaign=Spring&page=2", "https://example.com/?page=2")]
    [InlineData("http://shop.example/?q=a%20b&msclkid=9&sort=asc", "http://shop.example/?q=a%20b&sort=asc")]
    [InlineData("https://example.com/?utm_custom_thing=1", "https://example.com/")]
    [InlineData("https://example.com/?a=1&&mc_eid=2", "https://example.com/?a=1&")]
    [InlineData("https://example.com/?%66bclid=1&b=2", "https://example.com/?b=2")]
    public void StripsTrackingParameters(string input, string expected) => Assert.Equal(expected, TrackingParameters.Stripped(input));

    [Fact]
    public void LeavesCleanAndNonWebAddressesAlone()
    {
        Assert.Null(TrackingParameters.Stripped("https://example.com/search?q=fbclid"));
        Assert.Null(TrackingParameters.Stripped("https://example.com/"));
        Assert.Null(TrackingParameters.Stripped("https://example.com/#utm_source=x"));
        Assert.Null(TrackingParameters.Stripped("https://example.com/#a?utm_source=x"));
        Assert.Null(TrackingParameters.Stripped("about:blank?fbclid=1"));
        Assert.Null(TrackingParameters.Stripped("mosaic://history?utm_source=1"));
    }

    [Fact]
    public void HonoursFirefoxsAllowList() =>
        Assert.Null(TrackingParameters.Stripped("https://www.googleadservices.com/pagead/aclk?gclid=1"));

    [Fact]
    public void GeckoListHasExactNamesOnly()
    {
        Assert.Contains("fbclid", TrackingParameters.AllNames);
        Assert.Contains("utm_source", TrackingParameters.AllNames);
        Assert.All(TrackingParameters.AllNames, name => Assert.True(!name.Contains('*') && name == name.ToLowerInvariant()));
        Assert.Equal(TrackingParameters.AllNames.Count, TrackingParameters.AllNames.Distinct().Count());
    }
}

public sealed class NavigationPolicyTests
{
    private static PrivacySettings Settings(bool httpsOnly = true, bool strip = true, params string[] exceptions) =>
        new() { HttpsOnly = httpsOnly, StripTrackingParameters = strip, HttpsExceptions = exceptions };

    [Fact]
    public void UpgradesPlainHttp()
    {
        Assert.Equal("https://example.com/a?b=1", NavigationPolicy.Rewrite("http://example.com/a?b=1", Settings()));
        Assert.Equal("https://example.com/", NavigationPolicy.Rewrite("http://example.com:80/", Settings()));
        Assert.Equal("https://example.com:8080/", NavigationPolicy.Rewrite("http://example.com:8080/", Settings()));
        Assert.Equal("https://Example.com", NavigationPolicy.Rewrite("HTTP://Example.com", Settings()));
        Assert.Equal("https://user@example.com/x", NavigationPolicy.Rewrite("http://user@example.com/x", Settings()));
        Assert.Null(NavigationPolicy.Rewrite("https://example.com/", Settings()));
    }

    [Fact]
    public void UpgradesAndStripsTogether()
    {
        Assert.Equal("https://example.com/?x=2", NavigationPolicy.Rewrite("http://example.com/?fbclid=1&x=2", Settings()));
        Assert.Equal("http://example.com/", NavigationPolicy.Rewrite("http://example.com/?fbclid=1", Settings(httpsOnly: false)));
        Assert.Equal("https://example.com/?fbclid=1", NavigationPolicy.Rewrite("http://example.com/?fbclid=1", Settings(strip: false)));
    }

    [Fact]
    public void RespectsExceptionsAndTheUsersChoice()
    {
        var policy = Settings(exceptions: "example.com");
        Assert.Null(NavigationPolicy.Rewrite("http://www.example.com/", policy));
        Assert.Null(NavigationPolicy.Rewrite("http://news.example.com/", policy));
        Assert.Equal("https://notexample.com/", NavigationPolicy.Rewrite("http://notexample.com/", policy));
        Assert.Null(NavigationPolicy.Rewrite("http://other.org/", Settings(), new HashSet<string> { "other.org" }));
        Assert.Null(NavigationPolicy.Rewrite("http://other.org/", Settings(), allowInsecure: true));
    }

    [Fact]
    public void LeavesLocalAddressesOnHttp()
    {
        foreach (var url in new[] { "http://localhost:3000/", "http://127.0.0.1/", "http://192.168.1.10/", "http://10.0.0.2/x",
                                    "http://172.20.1.1/", "http://printer.local/", "http://router/", "http://[::1]:8080/" })
        {
            Assert.True(NavigationPolicy.Rewrite(url, Settings()) is null, url);
        }
        Assert.Equal("https://172.32.0.1/", NavigationPolicy.Rewrite("http://172.32.0.1/", Settings()));
        Assert.Equal("https://8.8.8.8/", NavigationPolicy.Rewrite("http://8.8.8.8/", Settings()));
    }

    [Fact]
    public void ExceptionsAreNormalisedAndSorted()
    {
        var settings = new PrivacySettings()
            .WithHttpException(true, "https://WWW.Zeta.com/page")
            .WithHttpException(true, "alpha.org")
            .WithHttpException(true, "alpha.org");
        Assert.Equal(["alpha.org", "zeta.com"], settings.HttpsExceptions);
        Assert.Equal(["alpha.org"], settings.WithHttpException(false, "zeta.com").HttpsExceptions);
        Assert.Same(settings, settings.WithHttpException(true, "about:blank"));
    }

    [Fact]
    public void SettingsDecodeFromOlderVersionsAndRoundTrip()
    {
        var decoded = PrivacySettings.FromJson("""{"httpsOnly":true}""");
        Assert.True(decoded.HttpsOnly);
        Assert.True(decoded.StripTrackingParameters);
        Assert.True(decoded.BlockThirdPartyCookies);
        Assert.Equal(DnsMode.Off, decoded.DnsOverHttps.Mode);
        Assert.Equal(decoded, PrivacySettings.FromJson(decoded.ToJson()));
        Assert.Equal(new PrivacySettings(), PrivacySettings.FromJson("not json"));
        Assert.Equal(new PrivacySettings(), PrivacySettings.FromJson("""{"httpsExceptions":null,"dnsOverHTTPS":null}"""));
    }

    [Fact]
    public void JsonUsesMacOSKeys()
    {
        var json = new PrivacySettings
        {
            HttpsOnly = true,
            DnsOverHttps = new DnsOverHttpsSetting { Mode = DnsMode.Strict, ProviderId = "quad9" },
        }.WithHttpException(true, "a.org").ToJson();
        foreach (var key in new[] { "\"httpsOnly\": true", "\"httpsExceptions\"", "\"stripTrackingParameters\"", "\"blockThirdPartyCookies\"",
                                    "\"clearCookiesOnExit\"", "\"dnsOverHTTPS\"", "\"mode\": \"strict\"", "\"providerID\": \"quad9\"", "\"customTemplate\"" })
        {
            Assert.Contains(key, json);
        }
        // What macOS writes reads back here.
        var macOS = PrivacySettings.FromJson("""{"httpsOnly":false,"httpsExceptions":["b.org"],"stripTrackingParameters":false,"blockThirdPartyCookies":true,"clearCookiesOnExit":true,"dnsOverHTTPS":{"mode":"automatic","providerID":"custom","customTemplate":"https://x.example/q"}}""");
        Assert.False(macOS.StripTrackingParameters);
        Assert.True(macOS.ClearCookiesOnExit);
        Assert.Equal(["b.org"], macOS.HttpsExceptions);
        Assert.Equal("https://x.example/q", macOS.DnsOverHttps.Template);
    }

    [Theory]
    [InlineData("https://example.com/", ConnectionSecurity.Secure)]
    [InlineData("http://example.com/", ConnectionSecurity.Insecure)]
    [InlineData("http://localhost:8080/", ConnectionSecurity.Local)]
    [InlineData("http://192.168.0.4/", ConnectionSecurity.Local)]
    [InlineData("wss://example.com/socket", ConnectionSecurity.Secure)]
    [InlineData("", ConnectionSecurity.NotApplicable)]
    [InlineData("about:blank", ConnectionSecurity.NotApplicable)]
    [InlineData("mosaic://settings", ConnectionSecurity.NotApplicable)]
    [InlineData("file:///C:/x.html", ConnectionSecurity.NotApplicable)]
    public void ConnectionSecurityOfPages(string url, ConnectionSecurity expected) => Assert.Equal(expected, ConnectionSecurities.Of(url));

    [Fact]
    public void ConnectionSecurityIdsMatchMacOS() =>
        Assert.Equal(["notApplicable", "secure", "insecure", "local"], Enum.GetValues<ConnectionSecurity>().Select(value => value.Id()));
}

public sealed class DnsOverHttpsTests
{
    [Fact]
    public void OffMeansOffInBothEngines()
    {
        var setting = new DnsOverHttpsSetting();
        Assert.Null(setting.Template);
        Assert.Equal(5, setting.GeckoPreferences["network.trr.mode"]);
        Assert.Equal("off", setting.ChromiumPreferences["dns_over_https.mode"]);
        Assert.Null(setting.WebView2Feature);
    }

    [Fact]
    public void ProviderAndStrictness()
    {
        var automatic = new DnsOverHttpsSetting { Mode = DnsMode.Automatic, ProviderId = "quad9" };
        Assert.Equal("https://dns.quad9.net/dns-query", automatic.Template);
        Assert.Equal(2, automatic.GeckoPreferences["network.trr.mode"]);
        Assert.Equal("automatic", automatic.ChromiumPreferences["dns_over_https.mode"]);
        Assert.Equal("DnsOverHttps:Fallback/true/Templates/https%3A%2F%2Fdns.quad9.net%2Fdns-query", automatic.WebView2Feature);

        var strict = new DnsOverHttpsSetting { Mode = DnsMode.Strict, ProviderId = "cloudflare" };
        Assert.Equal(3, strict.GeckoPreferences["network.trr.mode"]);
        Assert.Equal("https://cloudflare-dns.com/dns-query", strict.GeckoPreferences["network.trr.uri"]);
        Assert.Equal("secure", strict.ChromiumPreferences["dns_over_https.mode"]);
        Assert.StartsWith("DnsOverHttps:Fallback/false/", strict.WebView2Feature);
    }

    [Fact]
    public void CustomTemplatesAreValidated()
    {
        Assert.True(DnsOverHttps.IsValidTemplate("https://dns.example/dns-query{?dns}"));
        Assert.True(DnsOverHttps.IsValidTemplate(" https://dns.example/q "));
        Assert.False(DnsOverHttps.IsValidTemplate("http://dns.example/dns-query"));
        Assert.False(DnsOverHttps.IsValidTemplate("dns.example"));
        Assert.False(DnsOverHttps.IsValidTemplate(""));
        Assert.Null(new DnsOverHttpsSetting { Mode = DnsMode.Strict, ProviderId = "custom", CustomTemplate = "ftp://x" }.Template);
        Assert.Equal("https://x.example/q", new DnsOverHttpsSetting { Mode = DnsMode.Strict, ProviderId = "custom", CustomTemplate = "https://x.example/q" }.Template);
    }

    [Fact]
    public void BrowserArgumentsKeepOneFeatureList()
    {
        Assert.Equal("--enable-features=A,B", BrowserArguments.Merge("", ["A", "B"]));
        Assert.Equal("--flag --enable-features=X,Y,DnsOverHttps:Fallback/true",
            BrowserArguments.Merge("--enable-features=X --flag --enable-features=Y,DnsOverHttps:Old/1", ["DnsOverHttps:Fallback/true"]));
        Assert.Equal("--flag", BrowserArguments.Merge("--flag", []));
    }
}

public sealed class SiteDataTests
{
    private static readonly PublicSuffixList Psl = new("""
        // comment
        com
        uk
        co.uk
        *.ck
        !www.ck
        github.io
        """);

    [Theory]
    [InlineData("www.example.com", "example.com")]
    [InlineData(".example.com", "example.com")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("user.github.io", "user.github.io")]
    [InlineData("foo.bar.ck", "foo.bar.ck")]
    [InlineData("www.ck", "www.ck")]
    [InlineData("a.www.ck", "www.ck")]
    [InlineData("localhost", "localhost")]
    [InlineData("192.168.1.1", "192.168.1.1")]
    [InlineData("example.unknowntld", "example.unknowntld")]
    public void GroupsHostsIntoSites(string host, string site) => Assert.Equal(site, Psl.Site(host));

    [Fact]
    public void MergesBothEnginesPerSite()
    {
        var entries = SiteDataSummary.Merge(
        [
            new(SiteDataEngine.Chromium, ".example.com", Cookies: 1),
            new(SiteDataEngine.Chromium, "www.example.com", Cookies: 1),
            new(SiteDataEngine.Gecko, "shop.example.com", Cookies: 2, StorageBytes: 4096),
            new(SiteDataEngine.Gecko, "other.co.uk", Cookies: 0, StorageBytes: 10),
            new(SiteDataEngine.Chromium, "", Cookies: 1),
        ], Psl);
        Assert.Equal(["example.com", "other.co.uk"], entries.Select(entry => entry.Site));
        var example = entries[0];
        Assert.Equal(2, example.ChromiumCookies);
        Assert.Equal(2, example.GeckoCookies);
        Assert.Equal(4096, example.GeckoStorageBytes);
        Assert.Equal(4, example.CookieCount);
        Assert.True(example.Hosts.SetEquals([".example.com", "www.example.com", "shop.example.com"]));
        Assert.Contains("https://shop.example.com", example.ChromiumOrigins);
        Assert.Contains("http://example.com", example.ChromiumOrigins);
        Assert.DoesNotContain(example.ChromiumOrigins, origin => origin.Contains("//.", StringComparison.Ordinal));
        Assert.Equal("Chromium: 2 cookies · Mozilla: 2 cookies, 4 KB", example.Description);
    }

    [Theory]
    [InlineData(1, "1 byte")]
    [InlineData(999, "999 bytes")]
    [InlineData(4096, "4 KB")]
    [InlineData(1_250_000, "1.3 MB")]
    [InlineData(250_000, "250 KB")]
    [InlineData(12_000_000, "12 MB")]
    public void ByteCounts(long bytes, string expected) =>
        Assert.Equal(expected, SiteDataEntry.ByteCount(bytes).Replace(',', '.'));
}

public sealed class BlockLogTests
{
    [Fact]
    public void RecordsPerSiteWithCounts()
    {
        var log = new BlockLog(maxPages: 2, maxEntries: 2);
        log.Record("https://ads.example/a.js", "https://a.com/", "script");
        log.Record("https://ads.example/a.js", "https://a.com/", "script");
        log.Record("https://ads.example/b.gif", "https://a.com/", "image");
        log.Record("https://ads.example/c.gif", "https://a.com/", "image"); // over the cap
        Assert.Equal([2, 1], log.EntriesFor("https://news.a.com/story?id=1").Select(entry => entry.Count));
        Assert.Equal("https://a.com/", log.EntriesFor("https://a.com/")[0].DocumentUrl);
        Assert.Empty(log.EntriesFor("https://nota.com/"));

        log.Record("x", "https://b.com/", "script");
        log.Record("y", "https://c.com/", "script"); // evicts a.com
        Assert.Empty(log.EntriesFor("https://a.com/"));
        log.Clear("https://www.b.com/page");
        Assert.Empty(log.EntriesFor("https://b.com/"));
        Assert.Single(log.EntriesFor("https://c.com/x"));
    }

    [Fact]
    public void InjectionScriptEmbedsCssSafely()
    {
        Assert.Null(CosmeticInjection.Script(""));
        var script = CosmeticInjection.Script(".ad[title=\"x\"]\n{display:none!important;}</script>");
        Assert.NotNull(script);
        Assert.Contains("adoptedStyleSheets", script);
        Assert.DoesNotContain("</script>", script);
        Assert.Contains(@".ad[title=\u0022x\u0022]\n{display:none!important;}\u003C/script\u003E", script);
    }
}
