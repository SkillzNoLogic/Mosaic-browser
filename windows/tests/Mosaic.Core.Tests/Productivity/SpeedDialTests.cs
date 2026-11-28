using Mosaic.Core.Productivity;

namespace Mosaic.Core.Tests.Productivity;

public class SpeedDialTests
{
    [Fact]
    public void NormalisesAddresses()
    {
        Assert.Equal("https://example.com/", SpeedDial.NormalizedUrl("example.com"));
        Assert.Equal("http://example.com/path?q=1", SpeedDial.NormalizedUrl("  HTTP://Example.COM/path?q=1 "));
        Assert.Equal("https://localhost:8000/", SpeedDial.NormalizedUrl("localhost:8000"));
        Assert.Null(SpeedDial.NormalizedUrl("javascript:alert(1)"));
        Assert.Null(SpeedDial.NormalizedUrl("file:///C:/Windows/win.ini"));
        Assert.Null(SpeedDial.NormalizedUrl("not an address"));
        Assert.Null(SpeedDial.NormalizedUrl(""));
        Assert.Null(SpeedDial.NormalizedUrl("word"));
    }

    [Fact]
    public void IdentityIgnoresSchemeWwwAndTrailingSlash()
    {
        Assert.Equal(SpeedDial.Identity("https://www.example.com/"), SpeedDial.Identity("http://example.com"));
        Assert.Equal("example.com/a", SpeedDial.Identity("https://example.com/a/"));
        Assert.NotEqual(SpeedDial.Identity("https://example.com/?q=1"), SpeedDial.Identity("https://example.com/"));
    }

    [Fact]
    public void AddEditRemove()
    {
        var dial = new SpeedDial();
        var site = dial.Add(" Example ", "example.com");
        Assert.Equal("Example", site.Title);
        Assert.Equal(["https://example.com/"], dial.Pinned.Select(s => s.Url));
        Assert.Equal(SpeedDialError.Duplicate, Assert.Throws<SpeedDialException>(() => dial.Add("", "https://www.example.com")).Error);
        Assert.Equal(SpeedDialError.InvalidAddress, Assert.Throws<SpeedDialException>(() => dial.Add("", "nope")).Error);

        dial.Update(site.Id, "", "example.org/news");
        Assert.Equal("https://example.org/news", dial.Pinned[0].Url);
        Assert.Equal("example.org", dial.Pinned[0].DisplayTitle);
        Assert.Throws<SpeedDialException>(() => dial.Update(site.Id, "", "ftp://x.y"));

        dial.Remove(site.Id);
        Assert.Empty(dial.Pinned);
    }

    [Fact]
    public void UpdateRefusesDuplicatesOfOtherSites()
    {
        var dial = new SpeedDial();
        var a = dial.Add("A", "a.com");
        dial.Add("B", "b.com");
        Assert.Throws<SpeedDialException>(() => dial.Update(a.Id, "A", "b.com"));
        dial.Update(a.Id, "A2", "a.com");
        Assert.Equal("A2", dial.Pinned[0].Title);
    }

    [Fact]
    public void Reorders()
    {
        var dial = new SpeedDial();
        var a = dial.Add("A", "a.com");
        var b = dial.Add("B", "b.com");
        var c = dial.Add("C", "c.com");
        dial.Move(c.Id, 0);
        Assert.Equal(["C", "A", "B"], dial.Pinned.Select(s => s.Title));
        dial.Shift(c.Id, 1);
        Assert.Equal(["A", "C", "B"], dial.Pinned.Select(s => s.Title));
        dial.Shift(b.Id, 5);
        Assert.Equal(["A", "C", "B"], dial.Pinned.Select(s => s.Title));
        dial.Shift(a.Id, -1);
        Assert.Equal(["A", "C", "B"], dial.Pinned.Select(s => s.Title));
        dial.Add("D", "d.com", 1);
        Assert.Equal(["A", "D", "C", "B"], dial.Pinned.Select(s => s.Title));
    }

    [Fact]
    public void TopSitesSkipPinnedHiddenDuplicatesAndNonWeb()
    {
        var dial = new SpeedDial();
        dial.Add("Pinned", "pinned.com");
        dial.HideTopSite("https://hidden.com/");
        (string, string)[] candidates =
        [
            ("Pinned again", "https://www.pinned.com/"),
            ("Hidden", "http://hidden.com"),
            ("One", "https://one.com/"),
            ("One again", "https://one.com"),
            ("Local file", "file:///C:/x.html"),
            ("Two", "https://two.com/"),
            ("Three", "https://three.com/"),
        ];
        var top = dial.VisibleTopSites(candidates, 2);
        Assert.Equal(["One", "Two"], top.Select(s => s.Title));
        Assert.Equal(top.Select(s => s.Id), dial.VisibleTopSites(candidates, 2).Select(s => s.Id));

        dial.ShowsTopSites = false;
        Assert.Empty(dial.VisibleTopSites(candidates, 5));
        dial.ShowsTopSites = true;
        dial.RestoreHiddenTopSites();
        Assert.Contains("Hidden", dial.VisibleTopSites(candidates, 5).Select(s => s.Title));
    }

    [Fact]
    public void PinningAHiddenSiteUnhidesIt()
    {
        var dial = new SpeedDial();
        dial.HideTopSite("https://site.com");
        dial.Add("", "site.com");
        Assert.Empty(dial.HiddenTopSites);
    }

    [Fact]
    public void StableIdsMatchTheMacFormula()
    {
        // FNV-1a of "" is the seed itself: bytes are the seeds little-endian, printed in order.
        Assert.Equal(Guid.Parse("25232284e49cf2cbe49cf2cb25232284"), SpeedDial.StableId(""));
        Assert.Equal(SpeedDial.StableId("example.com"), SpeedDial.StableId("example.com"));
        Assert.NotEqual(SpeedDial.StableId("example.com"), SpeedDial.StableId("example.org"));
    }

    [Fact]
    public void JsonRoundTripsAndReadsMacFiles()
    {
        var dial = new SpeedDial();
        dial.Add("A", "a.com");
        dial.Add("B", "b.com");
        dial.HideTopSite("https://c.com");
        dial.ShowsIcons = false;
        var loaded = SpeedDial.FromJson(dial.ToJson())!;
        Assert.Equal(dial.Pinned, loaded.Pinned);
        Assert.Equal(dial.HiddenTopSites, loaded.HiddenTopSites);
        Assert.False(loaded.ShowsIcons);
        Assert.True(loaded.ShowsTopSites);
        Assert.Contains("\"id\": \"" + dial.Pinned[0].Id.ToString().ToUpperInvariant() + "\"", dial.ToJson());

        var mac = SpeedDial.FromJson("""{"pinned":[{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","title":"A","url":"https://a.com/"}]}""")!;
        Assert.Single(mac.Pinned);
        Assert.Equal(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"), mac.Pinned[0].Id);
        Assert.True(mac.ShowsTopSites && mac.ShowsIcons);
        Assert.Empty(mac.HiddenTopSites);
        Assert.Null(SpeedDial.FromJson("nope"));
    }
}
