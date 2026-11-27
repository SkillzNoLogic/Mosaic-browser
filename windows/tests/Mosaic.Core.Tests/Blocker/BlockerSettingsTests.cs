using Mosaic.Core.Blocker;

namespace Mosaic.Core.Tests.Blocker;

/// <summary>Port of <c>BlockerSettingsTests</c> in <c>Tests/MosaicCoreTests/BlockerTests.swift</c>.</summary>
public class BlockerSettingsTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("WWW.Example.COM", "example.com")]
    [InlineData("https://www.example.com/path?q=1", "example.com")]
    [InlineData("news.example.co.uk/article", "news.example.co.uk")]
    [InlineData("  example.org  ", "example.org")]
    [InlineData("localhost:3000", "localhost")]
    public void NormalizesSites(string input, string expected) => Assert.Equal(expected, BlockerSettings.Normalize(input));

    [Fact]
    public void RejectsThingsThatAreNotSites()
    {
        Assert.Null(BlockerSettings.Normalize(""));
        Assert.Null(BlockerSettings.Normalize("about:blank"));
        Assert.Null(BlockerSettings.Normalize("two words"));
    }

    [Fact]
    public void OnlyWebPagesHaveASite()
    {
        Assert.Equal("example.com", BlockerSettings.SiteOfPage("https://www.example.com/a"));
        Assert.Equal("localhost", BlockerSettings.SiteOfPage("http://localhost:3000/"));
        Assert.Null(BlockerSettings.SiteOfPage("about:blank"));
        Assert.Null(BlockerSettings.SiteOfPage("file:///tmp/x.html"));
        Assert.Null(BlockerSettings.SiteOfPage("moz-extension://abc/popup.html"));
        Assert.Null(BlockerSettings.SiteOfPage(""));
    }

    [Fact]
    public void AllowlistCoversSubdomainsButNotLookalikes()
    {
        var settings = new BlockerSettings(allowlist: ["example.com"]);
        Assert.True(settings.IsAllowlisted("example.com"));
        Assert.True(settings.IsAllowlisted("news.example.com"));
        Assert.True(settings.IsAllowlisted("a.b.example.com"));
        Assert.False(settings.IsAllowlisted("notexample.com"));
        Assert.False(settings.IsAllowlisted("example.com.evil.net"));
    }

    [Fact]
    public void FiltersOnlyWhenEnabledAndNotExempt()
    {
        var settings = new BlockerSettings();
        Assert.True(settings.Filters("https://example.com/"));
        Assert.False(settings.Filters("about:blank"));

        settings = settings.WithAllowlisted(true, "https://www.example.com/page");
        Assert.Equal(["example.com"], settings.Allowlist);
        Assert.False(settings.Filters("https://example.com/"));
        Assert.True(settings.Filters("https://other.org/"));

        settings = settings.WithAllowlisted(false, "example.com");
        Assert.True(settings.Filters("https://example.com/"));

        settings = settings.WithEnabled(false);
        Assert.False(settings.Filters("https://other.org/"));
    }

    [Fact]
    public void BlockingIsOnByDefault()
    {
        Assert.True(new BlockerSettings().IsEnabled);
        Assert.Empty(new BlockerSettings().Allowlist);
    }

    // ---- Windows: the settings are immutable, so a change is a new instance. ----

    [Fact]
    public void ChangesLeaveTheOriginalUntouched()
    {
        var original = new BlockerSettings(allowlist: ["https://www.example.com/", "not a site", ""]);
        Assert.Equal(["example.com"], original.Allowlist);

        var exempted = original.WithAllowlisted(true, "other.org");
        var disabled = original.WithEnabled(false);

        Assert.Equal(["example.com"], original.Allowlist);
        Assert.True(original.IsEnabled);
        Assert.Equal(["example.com", "other.org"], exempted.Allowlist.Order());
        Assert.False(disabled.IsEnabled);
        Assert.Equal(["example.com"], disabled.Allowlist);
        // No change, no new instance; and a "site" without a host changes nothing.
        Assert.Same(original, original.WithEnabled(true));
        Assert.Same(original, original.WithAllowlisted(true, "about:blank"));
    }
}
