using Mosaic.Core.Tests.Browser;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.Core.Tests.WindowsIntegration;

[Collection(SystemStaticState.Name)]
public class ActivationRequestTests
{
    private const string Cwd = @"C:\Users\me\Documents";
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Users\me\Documents\page.html",
        @"C:\Users\me\Documents\My Pages\a b#1.html",
        @"C:\Users\me\report.pdf",
        @"\\server\share\doc.svg",
    };

    private static ActivationRequest Parse(params string[] args) =>
        ActivationRequest.Parse(args, null, Cwd, Files.Contains);

    [Fact]
    public void NoArgumentsIsAPlainLaunch()
    {
        var request = Parse();
        Assert.True(request.IsPlainLaunch);
        Assert.Empty(request.Targets);
    }

    [Fact]
    public void UrlsAreOpenedInOrder()
    {
        var request = Parse("https://example.com/a", "http://example.org");
        Assert.Equal(["https://example.com/a", "http://example.org/"], request.Targets);
    }

    [Fact]
    public void AppSwitchesAreIgnored()
    {
        var request = Parse("--mosaic-automation", "--gecko-owned", "https://example.com/");
        Assert.Equal(["https://example.com/"], request.Targets);
        Assert.Empty(request.Rejected);
    }

    [Fact]
    public void FilesBecomeFileUrisResolvedAgainstTheWorkingDirectory()
    {
        var request = Parse("page.html", @"..\report.pdf", @"My Pages\a b#1.html", @"\\server\share\doc.svg");
        Assert.Equal(
        [
            "file:///C:/Users/me/Documents/page.html",
            "file:///C:/Users/me/report.pdf",
            "file:///C:/Users/me/Documents/My%20Pages/a%20b%231.html",
            "file://server/share/doc.svg",
        ], request.Targets);
    }

    [Fact]
    public void AbsolutePathsToMissingFilesAreRejectedNotSearched()
    {
        var request = Parse(@"C:\nope\missing.html");
        Assert.Empty(request.Targets);
        Assert.Equal([@"C:\nope\missing.html"], request.Rejected);
    }

    [Fact]
    public void AddressTextIsKeptForTheAddressResolver()
    {
        Assert.Equal(["example.com"], Parse("example.com").Targets);
        Assert.Equal(["localhost:3000"], Parse("localhost:3000").Targets);
        Assert.Equal(["example.com:8080/x"], Parse("example.com:8080/x").Targets);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ms-settings:privacy")]
    public void UnsafeOrForeignSchemesAreRejected(string url)
    {
        var request = Parse(url);
        Assert.Empty(request.Targets);
        Assert.Single(request.Rejected);
    }

    [Theory]
    [InlineData("mosaic://settings")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/x.html")]
    public void MosaicAboutAndFileUrlsAreAllowed(string url) => Assert.Single(Parse(url).Targets);

    [Fact]
    public void SingleArgumentTakesTheRestOfTheRawCommandLineVerbatim()
    {
        // What the registration's open command produces for a URL with spaces and a fake switch.
        var raw = "\"C:\\Program Files\\Mosaic\\Mosaic.exe\" --single-argument https://example.com/a b --mosaic-automation";
        var args = new[] { "--single-argument", "https://example.com/a", "b", "--mosaic-automation" };
        var request = ActivationRequest.Parse(args, raw, Cwd, Files.Contains);
        Assert.Equal(["https://example.com/a%20b%20--mosaic-automation"], request.Targets);
    }

    [Fact]
    public void SingleArgumentWithAFile()
    {
        var raw = "Mosaic.exe --single-argument C:\\Users\\me\\Documents\\My Pages\\a b#1.html";
        var request = ActivationRequest.Parse(["--single-argument", @"C:\Users\me\Documents\My", @"Pages\a", "b#1.html"], raw, Cwd, Files.Contains);
        Assert.Equal(["file:///C:/Users/me/Documents/My%20Pages/a%20b%231.html"], request.Targets);
    }

    [Fact]
    public void SingleArgumentWithoutRawCommandLineJoinsTheRest()
    {
        var request = ActivationRequest.Parse(["--single-argument", "https://example.com/"], null, Cwd, Files.Contains);
        Assert.Equal(["https://example.com/"], request.Targets);
    }

    [Fact]
    public void JumpListSwitches()
    {
        Assert.True(Parse("--new-tab").NewTab);
        var priv = Parse("--new-private-tab");
        Assert.True(priv.Private);
        Assert.False(priv.IsPlainLaunch);
        Assert.True(Parse("--new-window").NewWindow);
    }

    [Fact]
    public void NotificationActivationIsRecognisedAndNotOpened()
    {
        var request = Parse("----AppNotificationActivated:", "area=downloads;download=1");
        Assert.True(request.FromNotification);
        Assert.Empty(request.Targets);  // the SDK's own arguments are not pages
        Assert.True(request.IsPlainLaunch);
    }

    [Fact]
    public void PayloadRoundTrips()
    {
        var request = Parse("--new-private-tab", "https://example.com/", "page.html");
        var back = ActivationRequest.FromPayload(request.ToPayload())!;
        Assert.Equal(request.Targets, back.Targets);
        Assert.True(back.Private);
        Assert.False(back.NewTab);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":2,\"targets\":[]}")]
    [InlineData("[1,2]")]
    public void BadPayloadsAreRefused(string payload) => Assert.Null(ActivationRequest.FromPayload(payload));

    [Fact]
    public void PayloadCannotSmuggleUnsafeSchemes()
    {
        var back = ActivationRequest.FromPayload("{\"v\":1,\"targets\":[\"javascript:alert(1)\",\"https://ok.example/\",\"words to search\"]}")!;
        Assert.Equal(["https://ok.example/", "words to search"], back.Targets);
    }

    [Fact]
    public void ApplyReusesTheFreshTabThenOpensNewOnes()
    {
        using var browser = new BrowserFixture();
        Parse("https://one.example/", "https://two.example/").ApplyTo(browser.Model);
        Assert.Equal(["https://one.example/", "https://two.example/"], browser.Model.Tabs.Select(t => t.Url));
        Assert.Equal("https://two.example/", browser.Model.SelectedTab!.Url);
    }

    [Fact]
    public void ApplyResolvesAddressText()
    {
        using var browser = new BrowserFixture();
        browser.Model.Load("https://already.example/", browser.Model.SelectedTab!);
        Parse("example.com").ApplyTo(browser.Model);
        Assert.Equal(2, browser.Model.Tabs.Count);
        Assert.StartsWith("https://example.com", browser.Model.SelectedTab!.Url);
    }

    [Fact]
    public void NewTabOpensAnEmptyTab()
    {
        using var browser = new BrowserFixture();
        Parse("--new-tab").ApplyTo(browser.Model);
        Assert.Equal(2, browser.Model.Tabs.Count);
    }

    [Fact]
    public void PrivateWithoutPrivacySupportNeverOpensANormalTab()
    {
        using var browser = new BrowserFixture();
        ActivationRequest.PrivateTabOpener = null;
        Parse("--new-private-tab", "https://secret.example/").ApplyTo(browser.Model);
        Assert.Single(browser.Model.Tabs);
        Assert.Equal("", browser.Model.SelectedTab!.Url);
        Assert.NotNull(browser.Model.Notice);
    }

    [Fact]
    public void PrivateUsesThePrivacyAreasOpener()
    {
        using var browser = new BrowserFixture();
        var opened = new List<string>();
        ActivationRequest.PrivateTabOpener = (_, target) => { opened.Add(target); return true; };
        try
        {
            Parse("--new-private-tab").ApplyTo(browser.Model);
            Parse("--new-private-tab", "https://secret.example/").ApplyTo(browser.Model);
        }
        finally
        {
            ActivationRequest.PrivateTabOpener = null;
        }
        Assert.Equal(["", "https://secret.example/"], opened);
    }
}
