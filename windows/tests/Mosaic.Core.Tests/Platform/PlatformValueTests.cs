using System.Text.Json.Nodes;
using Mosaic.Core.Extensions;
using Mosaic.Core.Platform;

namespace Mosaic.Core.Tests.Platform;

/// <summary>Ports of <c>Tests/MosaicCoreTests/Platform/EngineRulesTests.swift</c> (EngineRulesTests, LanguagePreferencesTests).</summary>
public class EngineRulesTests
{
    [Fact]
    public void NormalisesWhatPeopleType()
    {
        Assert.Equal("example.com", EngineRuleSet.NormalizedDomain("https://www.Example.com/path?q=1"));
        Assert.Equal("example.com", EngineRuleSet.NormalizedDomain("example.com:8080"));
        Assert.Equal("example.com", EngineRuleSet.NormalizedDomain("  *.Example.COM "));
        Assert.Equal("mail.example.com", EngineRuleSet.NormalizedDomain("mail.example.com/"));
        Assert.Equal("localhost", EngineRuleSet.NormalizedDomain("localhost"));
        Assert.Equal("127.0.0.1", EngineRuleSet.NormalizedDomain("127.0.0.1"));
        Assert.Equal("example.com", EngineRuleSet.NormalizedDomain("example.com."));
    }

    [Fact]
    public void RejectsNonDomains()
    {
        Assert.Null(EngineRuleSet.NormalizedDomain(""));
        Assert.Null(EngineRuleSet.NormalizedDomain("not a domain"));
        Assert.Null(EngineRuleSet.NormalizedDomain("-bad.com"));
        Assert.Null(EngineRuleSet.NormalizedDomain("a..b"));
        Assert.Null(EngineRuleSet.NormalizedDomain("exa_mple.com"));
    }

    [Fact]
    public void MatchesDomainAndSubdomains()
    {
        var set = new EngineRuleSet([new EngineRule("example.com", "chromium")]);
        Assert.Equal("chromium", set.EngineFor("https://example.com/"));
        Assert.Equal("chromium", set.EngineFor("https://www.example.com/a"));
        Assert.Equal("chromium", set.EngineFor("http://mail.example.com"));
        Assert.Equal("chromium", set.EngineFor("https://a.b.example.com:8443/x"));
        Assert.Null(set.EngineFor("https://notexample.com"));
        Assert.Null(set.EngineFor("https://example.com.evil.net"));
    }

    [Fact]
    public void ExactRulesIgnoreSubdomains()
    {
        var set = new EngineRuleSet([new EngineRule("example.com", "chromium", includesSubdomains: false)]);
        Assert.Equal("chromium", set.EngineFor("https://example.com"));
        Assert.Equal("chromium", set.EngineFor("https://www.example.com"));  // www. is the same site
        Assert.Null(set.EngineFor("https://mail.example.com"));
    }

    [Fact]
    public void MostSpecificRuleWinsAndExactBeatsSubdomainsForTheSameDomain()
    {
        var set = new EngineRuleSet([new EngineRule("google.com", "chromium"), new EngineRule("mail.google.com", "gecko")]);
        Assert.Equal("gecko", set.EngineFor("https://mail.google.com/inbox"));
        Assert.Equal("chromium", set.EngineFor("https://docs.google.com"));
    }

    [Fact]
    public void OnlyWebPages()
    {
        var set = new EngineRuleSet([new EngineRule("example.com", "chromium")]);
        Assert.Null(set.EngineFor("file:///example.com/index.html"));
        Assert.Null(set.EngineFor("mosaic://history"));
        Assert.Null(set.EngineFor("about:blank"));
        Assert.Null(set.EngineFor("not a url"));
        Assert.Equal("chromium", set.EngineFor("https://EXAMPLE.com"));
    }

    [Fact]
    public void OneRulePerDomain()
    {
        var set = new EngineRuleSet();
        Assert.True(set.Upsert(new EngineRule("https://www.example.com", "chromium")));
        Assert.True(set.Upsert(new EngineRule("example.com", "gecko")));
        Assert.Single(set.Rules);
        Assert.Equal("gecko", set.Rules[0].Engine);
        Assert.False(set.Upsert(new EngineRule("nope nope", "gecko")));

        // Editing an existing rule onto another rule's domain merges them.
        var other = new EngineRule("other.org", "chromium");
        set.Upsert(other);
        set.Upsert(other with { Domain = "example.com" });
        Assert.Single(set.Rules);
        Assert.Equal(other.Id, set.Rules[0].Id);

        set.Remove(other.Id);
        Assert.Empty(set.Rules);
    }

    [Fact]
    public void RoundTripsThroughTheMacOSJson()
    {
        var set = new EngineRuleSet([new EngineRule("b.com", "gecko"), new EngineRule("a.com", "chromium", includesSubdomains: false)]);
        var decoded = EngineRuleSet.FromJson(JsonNode.Parse(set.ToJson().ToJsonString()));
        Assert.Equal(set, decoded);
        Assert.Equal(["a.com", "b.com"], decoded.Rules.Select(rule => rule.Domain));

        // What Swift's JSONEncoder wrote on a Mac reads here.
        var fromMac = EngineRuleSet.FromJson(JsonNode.Parse("""
            {"rules":[{"id":"7C9E6679-7425-40DE-944B-E07FC1F90AE7","domain":"youtube.com","engine":"chromium","includesSubdomains":true}]}
            """));
        var rule = Assert.Single(fromMac.Rules);
        Assert.Equal(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"), rule.Id);
        Assert.Equal("chromium", fromMac.EngineFor("https://m.youtube.com/watch"));
        Assert.Equal("7C9E6679-7425-40DE-944B-E07FC1F90AE7", fromMac.ToJson()["rules"]![0]!["id"]!.GetValue<string>());
    }
}

public class LanguagePreferencesTests
{
    [Fact]
    public void NormalisesTags()
    {
        Assert.Equal("en-US", LanguagePreferences.Normalize("EN_us"));
        Assert.Equal("zh-Hant-TW", LanguagePreferences.Normalize("zh-hant-tw"));
        Assert.Equal("es-419", LanguagePreferences.Normalize("es-419"));
        Assert.Equal("fil", LanguagePreferences.Normalize("fil"));
        Assert.Null(LanguagePreferences.Normalize("e"));
        Assert.Null(LanguagePreferences.Normalize("english"));
        Assert.Null(LanguagePreferences.Normalize("en-"));
        Assert.Null(LanguagePreferences.Normalize("en,fr"));
    }

    [Fact]
    public void SystemDefaultsAddBaseLanguages()
    {
        Assert.Equal(["en-AU", "en", "fr-FR", "fr", "en-GB"], LanguagePreferences.FromSystem(["en-AU", "fr-FR", "en-GB"]).Tags);
        Assert.Equal(["en-US", "en"], LanguagePreferences.FromSystem([]).Tags);
    }

    [Fact]
    public void Editing()
    {
        var prefs = new LanguagePreferences(["en-GB", "en"]);
        Assert.True(prefs.Add("fr"));
        Assert.False(prefs.Add("FR"));  // already listed
        Assert.False(prefs.Add("nonsense!"));
        prefs.Move("fr", -2);
        Assert.Equal(["fr", "en-GB", "en"], prefs.Tags);
        prefs.Move("fr", -1);  // already first
        Assert.Equal("fr", prefs.Tags[0]);
        Assert.True(prefs.Remove("en"));
        Assert.True(prefs.Remove("fr"));
        Assert.False(prefs.Remove("en-GB"));  // the last one stays
        Assert.Equal(["en-GB"], prefs.Tags);
    }

    [Fact]
    public void FormatsForEngines()
    {
        var prefs = new LanguagePreferences(["de-CH", "de", "en"]);
        Assert.Equal("de-CH,de,en", prefs.PreferenceValue);
        Assert.Equal("de-CH,de;q=0.9,en;q=0.8", prefs.AcceptLanguageHeader);
        Assert.Equal("English (United Kingdom)", LanguagePreferences.DisplayName("en-GB"));
        Assert.False(string.IsNullOrEmpty(LanguagePreferences.DisplayName("qq-ZZ")));
        var chromium = ChromiumLanguageOptions.For(prefs);
        Assert.Equal("de-CH", chromium.Language);
        Assert.Equal("--accept-lang=de-CH,de,en", chromium.BrowserArgument);
    }

    [Fact]
    public void CommonListIsValid()
    {
        foreach (var tag in LanguagePreferences.Common) Assert.Equal(tag, LanguagePreferences.Normalize(tag));
    }
}

/// <summary>Ports of <c>PlatformVersionTests.swift</c>, with Windows' download addresses.</summary>
public class SoftwareVersionTests
{
    private static SoftwareVersion V(string text) => SoftwareVersion.Parse(text)!;

    [Fact]
    public void ParsesMozillaGitHubAndDotNetStyles()
    {
        Assert.Equal([153, 8, 0], V("153.8.0esr").Components);
        Assert.Null(V("153.8.0esr").PreRelease);
        Assert.Equal([0, 2, 0], V("v0.2.0").Components);
        Assert.Equal("b3", V("140.0b3").PreRelease);
        Assert.Equal("beta.1", V("1.2.3-beta.1").PreRelease);
        Assert.Equal("153.8.0", V("153.8.0esr").NumberString);
        // .NET's informational version carries the commit after "+".
        Assert.Equal(V("0.3.1"), V("0.3.1+5f2c9e0a"));
        Assert.Null(V("0.3.1+5f2c9e0a").PreRelease);
    }

    [Fact]
    public void RejectsNonVersions()
    {
        Assert.Null(SoftwareVersion.Parse(""));
        Assert.Null(SoftwareVersion.Parse("esr"));
        Assert.Null(SoftwareVersion.Parse("1..2"));
        Assert.Null(SoftwareVersion.Parse("latest"));
    }

    [Fact]
    public void ComparesNumerically()
    {
        Assert.True(V("153.10.0") > V("153.9.0"));
        Assert.True(V("154.0") > V("153.99.99"));
        Assert.Equal(V("153.8"), V("153.8.0"));
        Assert.Equal(V("153.8.0esr"), V("153.8.0"));
        Assert.Equal(V("153.8").GetHashCode(), V("153.8.0").GetHashCode());
        Assert.True(V("153.8.1") > V("153.8.0esr"));
        Assert.True(V("v0.10.0") > V("0.9.9"));
    }

    [Fact]
    public void PreReleasesSortBeforeTheRelease()
    {
        Assert.True(V("140.0b3") < V("140.0"));
        Assert.True(V("140.0b3") < V("140.0b10"));
        Assert.True(V("140.0b3") > V("139.0"));
    }

    [Fact]
    public void ReadsESRFromProductDetails()
    {
        Assert.Equal(V("153.8.0"), FirefoxProductDetails.EsrVersion("""{"FIREFOX_ESR": "153.8.0esr", "FIREFOX_ESR_NEXT": "", "LATEST_FIREFOX_VERSION": "161.0"}"""));
        Assert.Null(FirefoxProductDetails.EsrVersion("""{"FIREFOX_ESR": ""}"""));
        Assert.Null(FirefoxProductDetails.EsrVersion("not json"));
    }

    [Fact]
    public void DownloadsTheRightBuildAndOnlyTrustsMozillaOverHttps()
    {
        Assert.Equal("https://download.mozilla.org/?product=firefox-esr-latest-ssl&os=win64&lang=en-US",
            FirefoxProductDetails.EsrDownloadUrl(MachineArchitecture.X64).ToString());
        Assert.Contains("os=win64-aarch64", FirefoxProductDetails.EsrDownloadUrl(MachineArchitecture.Arm64).ToString());
        Assert.True(FirefoxProductDetails.IsTrustedDownloadUrl(FirefoxProductDetails.EsrDownloadUrl(MachineArchitecture.X64)));
        Assert.True(FirefoxProductDetails.IsTrustedDownloadUrl(new Uri(
            "https://download-installer.cdn.mozilla.net/pub/firefox/releases/153.8.0esr/win64/en-US/Firefox%20Setup%20153.8.0esr.exe")));
        Assert.False(FirefoxProductDetails.IsTrustedDownloadUrl(new Uri("http://download.mozilla.org/x.exe")));
        Assert.False(FirefoxProductDetails.IsTrustedDownloadUrl(new Uri("https://mozilla.org.evil.com/x.exe")));
        Assert.False(FirefoxProductDetails.IsTrustedDownloadUrl(new Uri("https://evilmozilla.org/x.exe")));
    }
}

public class ReleaseCheckTests
{
    private const string ReleaseJson = """
        {"tag_name": "v0.3.0", "name": "Mosaic 0.3", "html_url": "https://github.com/SkillzNoLogic/Mosaic-browser/releases/tag/v0.3.0",
         "published_at": "2026-11-20T10:00:00Z", "body": "Notes", "assets": [
          {"name":"Mosaic-0.3.0-macos-arm64.zip","size":2,"browser_download_url":"https://github.com/a/b/m.zip","url":"https://api.github.com/x/2"},
          {"name":"Mosaic-0.3.0-windows-x64.zip","size":3,"browser_download_url":"https://github.com/a/b/w.zip","url":"https://api.github.com/x/3"},
          {"name":"Mosaic-0.3.0-windows-x64.zip.sig","size":4,"browser_download_url":"https://github.com/a/b/w.zip.sig","url":"https://api.github.com/x/4"},
          {"name":"Mosaic-0.3.0-windows-arm64.zip","size":5,"browser_download_url":"https://github.com/a/b/a.zip","url":"https://api.github.com/x/5"}]}
        """;

    [Fact]
    public void NewerReleaseIsAvailable()
    {
        var outcome = Assert.IsType<ReleaseOutcome.Available>(ReleaseCheck.Outcome(200, ReleaseJson, SoftwareVersion.Parse("0.1.0")));
        Assert.Equal(SoftwareVersion.Parse("0.3.0"), outcome.Release.Version);
        Assert.Equal("Mosaic 0.3", outcome.Release.Name);
        Assert.EndsWith("v0.3.0", outcome.Release.PageUrl!.ToString());
        Assert.NotNull(outcome.Release.PublishedAt);
        Assert.Equal(4, outcome.Release.Assets.Count);
        Assert.Equal("Mosaic 0.3.0 is available.", outcome.Message);
    }

    [Fact]
    public void PicksThisArchitecturesZipAndSignature()
    {
        var release = ReleaseCheck.ParseRelease(ReleaseJson)!;
        var x64 = ReleaseCheck.UpdateAssets(release.Assets, release.Version, MachineArchitecture.X64);
        Assert.NotNull(x64);
        Assert.Equal(3, x64.Value.Zip.Size);
        Assert.Equal("https://github.com/a/b/w.zip.sig", x64.Value.Signature.BrowserUrl!.ToString());
        // The ARM64 zip has no signature in this release, so it is not installable.
        Assert.Null(ReleaseCheck.UpdateAssets(release.Assets, release.Version, MachineArchitecture.Arm64));
        Assert.Equal("Mosaic-0.3.0-windows-arm64.zip", UpdatePackage.ZipName(release.Version, MachineArchitecture.Arm64));
        Assert.Equal("Mosaic-0.3.0-windows-x64.zip.sig", UpdatePackage.SignatureName(release.Version, MachineArchitecture.X64));
    }

    [Fact]
    public void SameOrOlderReleaseIsUpToDate()
    {
        Assert.Equal(new ReleaseOutcome.UpToDate(SoftwareVersion.Parse("0.3.0")!), ReleaseCheck.Outcome(200, ReleaseJson, SoftwareVersion.Parse("0.3.0")));
        Assert.IsType<ReleaseOutcome.UpToDate>(ReleaseCheck.Outcome(200, ReleaseJson, SoftwareVersion.Parse("1.0")));
    }

    [Fact]
    public void OtherAnswers()
    {
        Assert.IsType<ReleaseOutcome.NotFound>(ReleaseCheck.Outcome(404, """{"message": "Not Found"}""", null));
        Assert.IsType<ReleaseOutcome.RateLimited>(ReleaseCheck.Outcome(403, "", null));
        Assert.Equal(new ReleaseOutcome.Failed("GitHub returned HTTP 500."), ReleaseCheck.Outcome(500, "", null));
        Assert.IsType<ReleaseOutcome.Failed>(ReleaseCheck.Outcome(200, "{}", null));
    }

    [Fact]
    public void FeedOverrideIsLimitedToHttpsOrThisPC()
    {
        Assert.Equal(ReleaseCheck.LatestReleaseUrl, ReleaseCheck.FeedUrl(_ => null));
        Assert.Equal("http://127.0.0.1:8765/latest", ReleaseCheck.FeedUrl(_ => "http://127.0.0.1:8765/latest").ToString());
        Assert.Equal("http://localhost:1/x", ReleaseCheck.FeedUrl(_ => "http://localhost:1/x").ToString());
        Assert.Equal(ReleaseCheck.LatestReleaseUrl, ReleaseCheck.FeedUrl(_ => "http://example.com/latest"));
        Assert.Equal(ReleaseCheck.LatestReleaseUrl, ReleaseCheck.FeedUrl(_ => "file:///C:/latest"));
        Assert.Equal("https://example.com/feed", ReleaseCheck.FeedUrl(_ => "https://example.com/feed").ToString());
    }

    [Fact]
    public void DailyScheduleOnlyWhenChosen()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(UpdateCheckFrequency.Manual.IsDue(null, now));
        Assert.True(UpdateCheckFrequency.Daily.IsDue(null, now));
        Assert.False(UpdateCheckFrequency.Daily.IsDue(now.AddHours(-1), now));
        Assert.True(UpdateCheckFrequency.Daily.IsDue(now.AddHours(-25), now));
        // A clock that went backwards must not suppress checks forever.
        Assert.True(UpdateCheckFrequency.Daily.IsDue(now.AddHours(1), now));
    }
}

public class ExtensionPermissionsTests
{
    [Fact]
    public void ReadsApisSitesAndContentScripts()
    {
        var permissions = ExtensionPermissions.FromManifest("""
            {"manifest_version": 3, "permissions": ["storage", "tabs", "storage", "https://api.example.com/*"],
             "host_permissions": ["*://*.example.org/*"],
             "content_scripts": [{"matches": ["https://example.org/*", "*://*.example.org/*"]}]}
            """);
        Assert.Equal(["storage", "tabs"], permissions.Apis);
        Assert.Equal(["https://api.example.com/*", "*://*.example.org/*", "https://example.org/*"], permissions.Sites);
        Assert.False(permissions.ReadsAllSites);
        Assert.Equal("Uses: storage, tabs", permissions.Summary[1]);
    }

    [Fact]
    public void RecognisesAllSitesAndNothing()
    {
        Assert.True(ExtensionPermissions.FromManifest("""{"permissions": ["<all_urls>"]}""").ReadsAllSites);
        Assert.True(ExtensionPermissions.FromManifest("""{"content_scripts": [{"matches": ["*://*/*"]}]}""").ReadsAllSites);
        Assert.Equal(["Can read and change all websites"], ExtensionPermissions.FromManifest("""{"host_permissions": ["https://*/*"]}""").Summary);
        Assert.Equal(["Asks for no special permissions"], ExtensionPermissions.FromManifest("{}").Summary);
        Assert.Same(ExtensionPermissions.None, ExtensionPermissions.FromManifest("not json"));
        Assert.Same(ExtensionPermissions.None, ExtensionPermissions.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }
}
