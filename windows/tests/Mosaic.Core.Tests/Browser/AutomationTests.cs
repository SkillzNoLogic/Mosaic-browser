using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Extensions;
using Mosaic.Core.Search;

namespace Mosaic.Core.Tests.Browser;

public class AutomationTests
{
    private sealed class Harness : IDisposable
    {
        public BrowserFixture Fixture { get; }
        public Automation Automation { get; }
        public List<string> Screenshots { get; } = [];

        public Harness(Action<BrowserFixture>? configure = null)
        {
            Fixture = new BrowserFixture(configure);
            Automation = new Automation(Fixture.Model, path =>
            {
                Screenshots.Add(path);
                return Task.CompletedTask;
            });
        }

        public BrowserModel Model => Fixture.Model;

        /// <summary>Sends one request the way a client would and parses the reply line.</summary>
        public async Task<JsonObject> SendAsync(string command, string? value = null, string? output = null)
        {
            var request = new JsonObject { ["do"] = command };
            if (value is not null) request["value"] = value;
            if (output is not null) request["out"] = output;
            var reply = await Automation.HandleAsync(request.ToJsonString());
            Assert.DoesNotContain('\n', reply.Json);
            return JsonNode.Parse(reply.Json)!.AsObject();
        }

        public async Task OkAsync(string command, string? value = null, string? output = null)
        {
            var reply = await SendAsync(command, value, output);
            Assert.True((bool)reply["ok"]!, reply.ToJsonString());
            Assert.Single(reply);
        }

        public async Task<string> ErrorAsync(string command, string? value = null)
        {
            var reply = await SendAsync(command, value);
            Assert.False((bool)reply["ok"]!);
            return (string)reply["error"]!;
        }

        public void Dispose() => Fixture.Dispose();
    }

    [Fact]
    public void AutomationTurnsFocusChangesOff() => TestLoop.Run(() =>
    {
        using var harness = new Harness();

        Assert.False(harness.Model.AllowsFocusChanges);
        Assert.Equal("--mosaic-automation", Automation.Flag);
        Assert.Equal("mosaic-automation", Automation.PipeName);
    });

    [Fact]
    public void TabCommandsDriveTheModel() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();
        var model = harness.Model;

        await harness.OkAsync("navigate", "example.com");
        Assert.Equal("https://example.com", model.Tabs[0].Url);

        await harness.OkAsync("newTab", "https://example.org/");
        Assert.Equal(2, model.Tabs.Count);
        Assert.Equal("https://example.org/", model.SelectedTab!.Url);

        await harness.OkAsync("newTab");
        Assert.True(model.SelectedTab!.IsStartPage);

        await harness.OkAsync("selectTab", "0");
        Assert.Same(model.Tabs[0], model.SelectedTab);

        await harness.OkAsync("closeTab");
        Assert.Equal(2, model.Tabs.Count);

        Assert.Equal("There is no tab 7.", await harness.ErrorAsync("selectTab", "7"));
        Assert.Equal("There is no tab -1.", await harness.ErrorAsync("selectTab", "-1"));
        Assert.Equal("There is no tab first.", await harness.ErrorAsync("selectTab", "first"));
    });

    [Fact]
    public void EngineCommandsSwitchEngines() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();
        var model = harness.Model;

        await harness.OkAsync("engine", "chromium");
        Assert.Equal(EngineKind.Chromium, model.Engine);
        await harness.OkAsync("toggleEngine");
        Assert.Equal(EngineKind.Gecko, model.Engine);
        await harness.OkAsync("engine", "gecko");

        Assert.Equal("Unknown engine \"servo\".", await harness.ErrorAsync("engine", "servo"));
    });

    [Fact]
    public void EngineCommandReportsWhyChromiumIsUnavailable() => TestLoop.Run(async () =>
    {
        using var harness = new Harness(f =>
        {
            f.Chromium.IsAvailable = false;
            f.Chromium.UnavailableReason = "Install the WebView2 Runtime.";
        });

        Assert.Equal("Install the WebView2 Runtime.", await harness.ErrorAsync("engine", "chromium"));
        Assert.Equal(EngineKind.Gecko, harness.Model.Engine);
    });

    [Fact]
    public void SearchEngineCommandPicksByName() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();

        await harness.OkAsync("searchEngine", "startpage");
        Assert.Equal(SearchEngine.Startpage, harness.Model.SearchEngine);

        Assert.Equal("Unknown search engine \"jeeves\".", await harness.ErrorAsync("searchEngine", "jeeves"));
        Assert.Equal(SearchEngine.Startpage, harness.Model.SearchEngine);
    });

    [Fact]
    public void HistoryCommandsReachTheEngine() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();
        var (_, browser) = harness.Fixture.OpenChromiumTab("https://example.com/");

        await harness.OkAsync("back");
        await harness.OkAsync("forward");
        await harness.OkAsync("reload");

        Assert.Equal(["back", "forward", "reload"], browser.Actions);
    });

    [Fact]
    public void ExtensionCommandsStartWorkAndReplyAtOnce() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();
        var manager = harness.Fixture.Extensions;
        var download = new TaskCompletionSource<HttpResponseMessage>();
        harness.Fixture.Http.Respond = _ => download.Task;

        await harness.OkAsync("installExtension", TestRules.DarkReader);

        // The reply did not wait for the download; `dump` shows it in progress.
        var busy = (await harness.SendAsync("dump"))["state"]!;
        Assert.True((bool)busy["extensionsBusy"]!);
        Assert.Empty(busy["extensions"]!.AsArray());

        download.SetResult(StubHttpHandler.Package("Dark Reader|4.9"));
        while (manager.IsBusy) await TestLoop.Settle();
        Assert.True(manager.IsInstalled(TestRules.DarkReader));

        await harness.OkAsync("removeExtension", TestRules.DarkReader);
        await TestLoop.Settle();
        Assert.Empty(manager.Extensions);
        Assert.Equal($"No extension {TestRules.DarkReader} is installed.", await harness.ErrorAsync("removeExtension", TestRules.DarkReader));
    });

    [Fact]
    public void InstallFileCommandInstallsFromDisk() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();
        var path = harness.Fixture.PathFor("local.crx");
        await File.WriteAllTextAsync(path, "Local|1.2");

        await harness.OkAsync("installFile", path);
        while (harness.Fixture.Extensions.IsBusy) await TestLoop.Settle();

        Assert.Equal("Local", Assert.Single(harness.Fixture.Extensions.Extensions).Name);
    });

    [Fact]
    public void ExtensionPageCommandOpensPopupOrOptions() => TestLoop.Run(async () =>
    {
        using var harness = new Harness(f => f.Library.Add("aaaa", "Alpha", hasPopup: true));

        await harness.OkAsync("extensionPage", "aaaa:popup");
        await TestLoop.Settle();
        Assert.Equal(["aaaa@crx.mosaic", "popup"], Assert.Single(harness.Fixture.Gecko.CallsTo("openExtensionPage")));

        Assert.Equal("No extension zzzz is installed.", await harness.ErrorAsync("extensionPage", "zzzz:options"));
        Assert.StartsWith("Expected", await harness.ErrorAsync("extensionPage", "aaaa"));
        Assert.StartsWith("Expected", await harness.ErrorAsync("extensionPage", "aaaa:sidebar"));
    });

    [Fact]
    public void ShowExtensionsCommandOpensAndClosesTheDialog() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();

        await harness.OkAsync("showExtensions");
        Assert.True(harness.Model.ShowExtensions);
        await harness.OkAsync("showExtensions", "false");
        Assert.False(harness.Model.ShowExtensions);
        await harness.OkAsync("showExtensions", "true");
        Assert.True(harness.Model.ShowExtensions);
    });

    [Fact]
    public void GeckoEvalWritesTheAgentsAnswer() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();
        harness.Fixture.OpenGeckoTab("https://example.com/");
        harness.Fixture.Gecko.Handlers["version"] = _ => new JsonObject { ["agent"] = 1, ["app"] = "Firefox" };
        var output = harness.Fixture.PathFor("version.json");

        await harness.OkAsync("geckoEval", "version", output);

        Assert.Equal("""{"agent":1,"app":"Firefox"}""", await File.ReadAllTextAsync(output));
        Assert.Empty(Assert.Single(harness.Fixture.Gecko.CallsTo("version")));
    });

    [Fact]
    public void GeckoEvalFailsCleanlyWhenGeckoIsNotRunning() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();

        Assert.Equal("Gecko is not running.", await harness.ErrorAsync("geckoEval", "version"));
    });

    [Fact]
    public void DumpDescribesTheWholeBrowser() => TestLoop.Run(async () =>
    {
        using var harness = new Harness(f => f.Library.Add("aaaa", "Alpha", "1.5", notes:
            [new(NoteSeverity.Warning, "sidePanel is unavailable."), new(NoteSeverity.Info, "Service worker converted.")]));
        var fixture = harness.Fixture;
        fixture.OpenGeckoTab("https://example.com/");
        fixture.Gecko.Report(Snapshots.Tab(1, "https://example.com/", "Example", canGoBack: true, selected: true));
        var (_, browser) = fixture.OpenChromiumTab("https://example.org/");
        browser.RaiseTitle("Example Org");
        harness.Model.IsGeckoAttached = true;
        harness.Model.Notice = "Search engine: Google";

        var reply = await harness.SendAsync("dump");
        Assert.True((bool)reply["ok"]!);
        var state = reply["state"]!.AsObject();

        // The key sets are the contract scripts are written against.
        Assert.Equal(
            ["addressText", "chromiumAvailable", "engine", "extensionError", "extensions", "extensionsBusy",
             "geckoAttached", "geckoProcessId", "geckoStatus", "notice", "searchEngine", "tabs"],
            state.Select(pair => pair.Key).Order());

        var tabs = state["tabs"]!.AsArray();
        Assert.Equal(3, tabs.Count);
        foreach (var tab in tabs)
        {
            Assert.Equal(
                ["canGoBack", "canGoForward", "engine", "failure", "geckoId", "hasChromiumView", "loading",
                 "selected", "title", "url"],
                tab!.AsObject().Select(pair => pair.Key).Order());
        }

        Assert.Equal("""{"engine":"gecko","url":"","title":"","loading":false,"canGoBack":false,"canGoForward":false,"geckoId":-1,"hasChromiumView":false,"failure":"","selected":false}""", tabs[0]!.ToJsonString());
        Assert.Equal("""{"engine":"gecko","url":"https://example.com/","title":"Example","loading":false,"canGoBack":true,"canGoForward":false,"geckoId":1,"hasChromiumView":false,"failure":"","selected":false}""", tabs[1]!.ToJsonString());
        Assert.Equal("""{"engine":"chromium","url":"https://example.org/","title":"Example Org","loading":true,"canGoBack":false,"canGoForward":false,"geckoId":-1,"hasChromiumView":true,"failure":"","selected":true}""", tabs[2]!.ToJsonString());

        Assert.Equal("chromium", (string)state["engine"]!);
        Assert.Equal("google", (string)state["searchEngine"]!);
        Assert.Equal("https://example.org/", (string)state["addressText"]!);
        Assert.True((bool)state["chromiumAvailable"]!);
        Assert.Equal("running", (string)state["geckoStatus"]!);
        Assert.Equal(4242, (int)state["geckoProcessId"]!);
        Assert.True((bool)state["geckoAttached"]!);
        Assert.Equal("Search engine: Google", (string)state["notice"]!);
        Assert.Equal("", (string)state["extensionError"]!);
        Assert.False((bool)state["extensionsBusy"]!);

        var extension = Assert.Single(state["extensions"]!.AsArray())!.AsObject();
        Assert.Equal(
            ["enabled", "geckoId", "id", "loaded", "name", "notes", "version"],
            extension.Select(pair => pair.Key).Order());
        Assert.Equal("aaaa", (string)extension["id"]!);
        Assert.Equal("aaaa@crx.mosaic", (string)extension["geckoId"]!);
        Assert.Equal("Alpha", (string)extension["name"]!);
        Assert.Equal("1.5", (string)extension["version"]!);
        Assert.True((bool)extension["enabled"]!);
        Assert.True((bool)extension["loaded"]!);
        Assert.Equal(
            ["warning: sidePanel is unavailable.", "info: Service worker converted."],
            extension["notes"]!.AsArray().Select(note => (string)note!));
    });

    [Fact]
    public void DumpOfAFreshBrowserUsesTheEmptyValues() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();

        var state = (await harness.SendAsync("dump"))["state"]!;

        Assert.Equal("stopped", (string)state["geckoStatus"]!);
        Assert.Equal(-1, (int)state["geckoProcessId"]!);
        Assert.False((bool)state["geckoAttached"]!);
        Assert.Equal("", (string)state["notice"]!);
        Assert.Equal("gecko", (string)state["engine"]!);
        Assert.True((bool)state["tabs"]![0]!["selected"]!);
    });

    [Fact]
    public void DumpAlsoWritesToAFileWhenGivenAPath() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();
        var path = harness.Fixture.PathFor("state.json");

        var reply = await harness.SendAsync("dump", path);

        var written = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.True(JsonNode.DeepEquals(reply["state"], written));
    });

    [Fact]
    public void DumpShowsAFailedTab() => TestLoop.Run(async () =>
    {
        using var harness = new Harness(f => f.Gecko.StartFailure = new InvalidOperationException("No Gecko runtime found."));
        harness.Model.Navigate("example.com");

        var state = (await harness.SendAsync("dump"))["state"]!;

        Assert.Equal("failed", (string)state["geckoStatus"]!);
        Assert.Equal("No Gecko runtime found.", (string)state["tabs"]![0]!["failure"]!);
    });

    [Fact]
    public void ScreenshotIsDelegatedToTheWindow() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();

        await harness.OkAsync("screenshot", @"C:\shots\mosaic.png");

        Assert.Equal([@"C:\shots\mosaic.png"], harness.Screenshots);
        Assert.Equal("screenshot needs the path to write to.", await harness.ErrorAsync("screenshot"));
    });

    [Fact]
    public void ScreenshotFailureIsReportedInTheReply() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var automation = new Automation(fixture.Model, _ => throw new IOException("The window is minimised."));

        var reply = await automation.HandleAsync("""{"do": "screenshot", "value": "shot.png"}""");

        Assert.Equal("""{"ok":false,"error":"The window is minimised."}""", reply.Json);
        Assert.False(reply.QuitRequested);
    });

    [Fact]
    public void QuitRepliesFirstAndAsksTheAppToExit() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();

        var reply = await harness.Automation.HandleAsync("""{"do":"quit"}""");

        Assert.Equal("""{"ok":true}""", reply.Json);
        Assert.True(reply.QuitRequested);
        Assert.False((await harness.Automation.HandleAsync("""{"do":"dump"}""")).QuitRequested);
    });

    [Theory]
    [InlineData("")]
    [InlineData("navigate example.com")]
    [InlineData("[]")]
    [InlineData("""{"value": "example.com"}""")]
    [InlineData("""{"do": 5}""")]
    public void MalformedRequestsGetAnErrorReply(string line) => TestLoop.Run(async () =>
    {
        using var harness = new Harness();

        var reply = JsonNode.Parse((await harness.Automation.HandleAsync(line)).Json)!;

        Assert.False((bool)reply["ok"]!);
        Assert.Equal("The request is not a JSON object with a \"do\" command.", (string)reply["error"]!);
    });

    [Fact]
    public void UnknownCommandIsNamedInTheError() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();

        Assert.Equal("Unknown command \"hitTest\".", await harness.ErrorAsync("hitTest"));
    });

    [Fact]
    public void ReplyIsAlwaysASingleLineOfValidJson() => TestLoop.Run(async () =>
    {
        using var harness = new Harness();
        harness.Model.Notice = "line one\nline two — “quoted”";

        var reply = await harness.Automation.HandleAsync("""{"do":"dump"}""");

        Assert.DoesNotContain('\n', reply.Json);
        Assert.DoesNotContain('\r', reply.Json);
        using var parsed = JsonDocument.Parse(reply.Json);
        Assert.Equal(
            "line one\nline two — “quoted”",
            parsed.RootElement.GetProperty("state").GetProperty("notice").GetString());
    });
}
