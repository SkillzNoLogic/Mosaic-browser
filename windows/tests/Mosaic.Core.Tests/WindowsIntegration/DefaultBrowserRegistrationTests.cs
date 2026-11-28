using Mosaic.Core.WindowsIntegration;

namespace Mosaic.Core.Tests.WindowsIntegration;

public class DefaultBrowserRegistrationTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Programs\Mosaic\Mosaic.exe";
    private readonly DefaultBrowserRegistration registration = new(Exe);
    private readonly InMemoryRegistry registry = new();

    [Fact]
    public void WritesTheClientCapabilitiesAndProgIds()
    {
        registration.Register(registry);
        const string client = @"Software\Clients\StartMenuInternet\Mosaic";
        Assert.Equal("Mosaic", registry.GetString(client, null));
        Assert.Equal("Mosaic", registry.GetString(client + @"\Capabilities", "ApplicationName"));
        Assert.Equal(Exe + ",0", registry.GetString(client + @"\Capabilities", "ApplicationIcon"));
        Assert.Equal("MosaicURL", registry.GetString(client + @"\Capabilities\URLAssociations", "http"));
        Assert.Equal("MosaicURL", registry.GetString(client + @"\Capabilities\URLAssociations", "https"));
        Assert.Null(registry.GetString(client + @"\Capabilities\URLAssociations", "ftp"));
        foreach (var extension in new[] { ".htm", ".html", ".pdf", ".svg", ".webp" })
            Assert.Equal("MosaicHTML", registry.GetString(client + @"\Capabilities\FileAssociations", extension));
        Assert.Equal("Mosaic", registry.GetString(client + @"\Capabilities\StartMenu", "StartMenuInternet"));
        Assert.Equal(client + @"\Capabilities", registry.GetString(@"Software\RegisteredApplications", "Mosaic"));
    }

    [Fact]
    public void OpenCommandPassesTheUrlAsASingleArgument()
    {
        registration.Register(registry);
        var expected = $"\"{Exe}\" --single-argument %1";
        Assert.Equal(expected, registry.GetString(@"Software\Classes\MosaicURL\shell\open\command", null));
        Assert.Equal(expected, registry.GetString(@"Software\Classes\MosaicHTML\shell\open\command", null));
        Assert.Equal("", registry.GetString(@"Software\Classes\MosaicURL", "URL Protocol"));
        Assert.Null(registry.GetString(@"Software\Classes\MosaicHTML", "URL Protocol"));
    }

    [Fact]
    public void AppearsUnderOpenWith()
    {
        registration.Register(registry);
        Assert.Equal("", registry.GetString(@"Software\Classes\.html\OpenWithProgids", "MosaicHTML"));
        Assert.Equal("", registry.GetString(@"Software\Classes\.pdf\OpenWithProgids", "MosaicHTML"));
    }

    [Fact]
    public void UnregisterRemovesExactlyWhatWasWritten()
    {
        registry.SetString(@"Software\RegisteredApplications", "Firefox", "x");
        registry.SetString(@"Software\Classes\.html\OpenWithProgids", "FirefoxHTML", "");
        var before = registry.ValueCount;
        registration.Register(registry);
        registration.Unregister(registry);
        Assert.Equal(before, registry.ValueCount);
        Assert.False(registry.KeyExists(@"Software\Clients\StartMenuInternet\Mosaic"));
        Assert.False(registry.KeyExists(@"Software\Classes\MosaicURL"));
        Assert.Equal("x", registry.GetString(@"Software\RegisteredApplications", "Firefox"));
        Assert.Equal("", registry.GetString(@"Software\Classes\.html\OpenWithProgids", "FirefoxHTML"));
    }

    [Fact]
    public void StatusBeforeAndAfter()
    {
        Assert.False(registration.Status(registry).Registered);
        registration.Register(registry);
        var status = registration.Status(registry);
        Assert.True(status.Registered);
        Assert.True(status.RegisteredForThisCopy);
        Assert.False(status.IsDefault);
        Assert.Empty(status.DefaultFor);
    }

    [Fact]
    public void StatusSeesAnotherCopysRegistration()
    {
        new DefaultBrowserRegistration(@"D:\Old\Mosaic.exe").Register(registry);
        var status = registration.Status(registry);
        Assert.True(status.Registered);
        Assert.False(status.RegisteredForThisCopy);
        registration.Register(registry);  // repairs it
        Assert.True(registration.Status(registry).RegisteredForThisCopy);
    }

    [Fact]
    public void IsDefaultReadsTheUsersChoice()
    {
        registration.Register(registry);
        registry.SetString(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice", "ProgId", "MosaicURL");
        Assert.False(registration.Status(registry).IsDefault);  // https still elsewhere
        registry.SetString(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice", "ProgId", "MosaicURL");
        registry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.html\UserChoice", "ProgId", "MosaicHTML");
        var status = registration.Status(registry);
        Assert.True(status.IsDefault);
        Assert.Equal([".html"], status.DefaultFor);
        Assert.Equal("MosaicURL", status.Choices["http"]);
    }

    [Fact]
    public void UserChoiceLatestWins()
    {
        const string key = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http";
        registry.SetString(key + @"\UserChoice", "ProgId", "MSEdgeHTM");
        registry.SetString(key + @"\UserChoiceLatest", "ProgId", "MosaicURL");
        Assert.Equal("MosaicURL", DefaultBrowserRegistration.UserChoice(registry, "http"));
    }

    [Fact]
    public void SettingsUriNamesTheRegistration()
    {
        Assert.Equal("ms-settings:defaultapps?registeredAppUser=Mosaic", registration.SettingsUri);
        Assert.Equal("ms-settings:defaultapps?registeredAppUser=Mosaic%20CI", new DefaultBrowserRegistration(Exe, "Mosaic CI").SettingsUri);
    }

    [Fact]
    public void ThrowawayNamesKeepTheirOwnKeys()
    {
        var ci = new DefaultBrowserRegistration(Exe, "MosaicCI");
        ci.Register(registry);
        Assert.Equal("MosaicCIURL", registry.GetString(@"Software\Clients\StartMenuInternet\MosaicCI\Capabilities\URLAssociations", "https"));
        Assert.False(registration.Status(registry).Registered);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"a\b")]
    [InlineData("a\"b")]
    public void RejectsBadNames(string name) =>
        Assert.Throws<ArgumentException>(() => new DefaultBrowserRegistration(Exe, name));

    [Fact]
    public void StatusJsonHasTheFieldsScriptsUse()
    {
        registration.Register(registry);
        var json = registration.Status(registry).ToJson();
        foreach (var key in new[] { "appName", "registered", "registeredForThisCopy", "openCommand", "isDefault", "defaultFor", "choices" })
            Assert.True(json.ContainsKey(key), key);
    }
}
