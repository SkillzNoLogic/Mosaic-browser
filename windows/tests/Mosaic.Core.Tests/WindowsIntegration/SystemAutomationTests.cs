using System.Text.Json.Nodes;
using Mosaic.Core.Tests.Browser;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.Core.Tests.WindowsIntegration;

[Collection(SystemStaticState.Name)]
public class SystemAutomationTests : IDisposable
{
    private sealed class FakeDefaultBrowser : IDefaultBrowserService
    {
        public InMemoryRegistry Registry { get; } = new();
        public int SettingsOpened { get; private set; }
        private DefaultBrowserRegistration For(string? name) => new(@"C:\Mosaic\Mosaic.exe", name ?? "Mosaic");
        public DefaultBrowserStatus Status(string? appName = null) => For(appName).Status(Registry);
        public DefaultBrowserStatus Register(string? appName = null) { For(appName).Register(Registry); return Status(appName); }
        public DefaultBrowserStatus Unregister(string? appName = null) { For(appName).Unregister(Registry); return Status(appName); }
        public bool OpenSettings(string? appName = null) { SettingsOpened++; return true; }
        public string? ShellDefault(string association) => DefaultBrowserRegistration.UserChoice(Registry, association);
    }

    private readonly FakeDefaultBrowser service = new();
    private readonly BrowserFixture browser = new();

    public SystemAutomationTests()
    {
        UserNotifications.Reset();
        TaskbarProgress.Reset();
    }

    public void Dispose()
    {
        browser.Dispose();
        UserNotifications.Reset();
        TaskbarProgress.Reset();
    }

    private async Task<JsonObject> Run(string command, string value = "", string? output = null)
    {
        var reply = await SystemAutomation.HandleAsync(service, browser.Model, command, value, output);
        Assert.NotNull(reply);
        return JsonNode.Parse(reply.Json)!.AsObject();
    }

    [Fact]
    public async Task UnknownCommandsAreNotHandled() =>
        Assert.Null(await SystemAutomation.HandleAsync(service, browser.Model, "navigate", "", null));

    [Fact]
    public async Task RegisterStatusUnregister()
    {
        Assert.False((await Run("defaultBrowserStatus", "MosaicCI"))["status"]!["registered"]!.GetValue<bool>());
        var registered = await Run("registerDefault", "MosaicCI");
        Assert.True(registered["status"]!["registered"]!.GetValue<bool>());
        Assert.True(registered["status"]!["registeredForThisCopy"]!.GetValue<bool>());
        Assert.True(registered["status"]!.AsObject().ContainsKey("shellDefaults"));
        var removed = await Run("unregisterDefault", "MosaicCI");
        Assert.False(removed["status"]!["registered"]!.GetValue<bool>());
        Assert.Equal(0, service.Registry.ValueCount);
    }

    [Fact]
    public async Task WithoutTheServiceDefaultBrowserCommandsFail()
    {
        var reply = await SystemAutomation.HandleAsync(null, browser.Model, "registerDefault", "", null);
        Assert.Contains("\"ok\":false", reply!.Json);
    }

    [Fact]
    public async Task NotifyPostsAndRecords()
    {
        var reply = await Run("notify", "Download finished|report.pdf");
        Assert.Equal("recorded", reply["result"]!["status"]!.GetValue<string>());
        var json = await Run("notify", "{\"area\":\"downloads\",\"title\":\"T\",\"body\":\"B\",\"tag\":\"x\",\"args\":{\"download\":\"1\"}}");
        Assert.True(json["ok"]!.GetValue<bool>());
        var recent = UserNotifications.State()["recent"]!.AsArray();
        Assert.Equal("downloads", recent[1]!["area"]!.GetValue<string>());
    }

    [Fact]
    public async Task NotifyActivateReachesSubscribers()
    {
        NotificationActivation? got = null;
        UserNotifications.Activated += a => got = a;
        await Run("notifyActivate", "area=downloads;download=42");
        Assert.Equal("42", got!.Arguments["download"]);
    }

    [Fact]
    public async Task TaskbarProgressCommand()
    {
        var reply = await Run("taskbarProgress", "normal:0.25");
        Assert.Equal("normal", reply["taskbar"]!["state"]!.GetValue<string>());
        Assert.Equal(0.25, reply["taskbar"]!["value"]!.GetValue<double>());
        Assert.False((await Run("taskbarProgress", "sideways"))["ok"]!.GetValue<bool>());
        await Run("taskbarProgress", "none");
        Assert.Equal(TaskbarProgressState.None, TaskbarProgress.Current.State);
    }

    [Fact]
    public async Task ActivateOpensTabsAndPayloadDoesNot()
    {
        var payload = await Run("activationPayload", "--single-argument https://example.com/x y");
        Assert.Equal("https://example.com/x%20y", payload["request"]!["targets"]![0]!.GetValue<string>());
        Assert.Single(browser.Model.Tabs);
        await Run("activate", "https://one.example/ https://two.example/");
        Assert.Equal(2, browser.Model.Tabs.Count);
        Assert.Equal("https://two.example/", browser.Model.SelectedTab!.Url);
    }

    [Fact]
    public async Task RepliesCanBeWrittenToAFile()
    {
        var path = browser.PathFor("status.json");
        await Run("defaultBrowserStatus", "", path);
        Assert.Contains("\"registered\"", File.ReadAllText(path));
    }

    [Fact]
    public void CoreStateHasEveryPart()
    {
        var state = SystemAutomation.CoreState();
        foreach (var key in new[] { "notifications", "taskbar", "appearance", "appearanceChanges", "lastActivation" })
            Assert.True(state.ContainsKey(key), key);
    }
}
