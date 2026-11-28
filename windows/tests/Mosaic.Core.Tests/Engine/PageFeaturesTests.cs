using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Engine;
using Mosaic.Core.Permissions;
using Mosaic.Core.Search;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Engine;

internal sealed class FakeChromiumPage : IChromiumPage
{
    public List<string> Actions { get; } = [];
    public double Zoom { get; private set; } = 1;
    public bool Muted { get; private set; }
    public FindStatus? NextFindResult { get; set; } = new FindStatus(1, 3);
    public string PictureInPicture { get; set; } = "entered";
    public PageCapture? Capture { get; set; } = new([0x89, 0x50, 0x4E, 0x47], 800, 600, false);

    public Task<FindStatus?> FindAsync(string text, bool matchCase, bool again, bool backwards)
    {
        Actions.Add($"find {text} case={matchCase} again={again} back={backwards}");
        return Task.FromResult(NextFindResult);
    }

    public void StopFind() => Actions.Add("stopFind");
    public void SetZoom(double factor) => Zoom = factor;
    public void SetMuted(bool muted) => Muted = muted;
    public void Print() => Actions.Add("print");

    public Task<bool> PrintToPdfAsync(string path)
    {
        File.WriteAllText(path, "%PDF-1.7");
        Actions.Add("pdf");
        return Task.FromResult(true);
    }

    public void OpenDevTools() => Actions.Add("devtools");
    public Task<PageCapture?> CaptureAsync(bool fullPage)
    {
        Actions.Add(fullPage ? "capture full" : "capture");
        return Task.FromResult(Capture);
    }

    public Task<string> TogglePictureInPictureAsync() => Task.FromResult(PictureInPicture);
    public void ExitFullScreen() => Actions.Add("exitFullScreen");
    public Task<JsonNode?> EvaluateAsync(string script, bool userGesture) => Task.FromResult<JsonNode?>(JsonValue.Create(script.Length));
    public void Click(double x, double y, int button) => Actions.Add($"click {x},{y},{button}");
}

internal sealed class FakeHost : IPageFeaturesHost
{
    public string? SavePath { get; set; }
    public List<string> Events { get; } = [];
    public string ScreenshotsFolder { get; init; } = "";

    public Task<string?> AskSavePathAsync(string suggestedName, string extension)
    {
        Events.Add($"ask {suggestedName}.{extension}");
        return Task.FromResult(SavePath);
    }

    public Task<bool> CopyImageAsync(byte[] png)
    {
        Events.Add($"copy image {png.Length}");
        return Task.FromResult(true);
    }

    public void CopyText(string text) => Events.Add($"copy {text}");
    public void Beep() => Events.Add("beep");
    public void SetPageFullScreen(bool fullScreen) => Events.Add($"fullscreen {fullScreen}");
    public void OpenSettings(string uri) => Events.Add($"settings {uri}");
}

internal sealed class RecordingResponder : IPermissionResponder, IDialogResponder
{
    public List<string> Answers { get; } = [];
    public void Respond(PermissionResponse response) => Answers.Add(response.ToString());
    public void Finish(bool accepted, string? text) => Answers.Add($"{accepted}:{text}");
}

public sealed class PageFeaturesTests : IDisposable
{
    private readonly BrowserFixture fixture = new();
    private readonly TemporaryFolder folder = new();
    private readonly Dictionary<BrowserTab, FakeChromiumPage> chromiumPages = [];
    private readonly FakeHost host;
    private WindowsCapabilityAccess cameraAccess = WindowsCapabilityAccess.Allowed;

    public PageFeaturesTests()
    {
        host = new FakeHost { ScreenshotsFolder = folder.File("Screenshots") };
        Model.ConfigurePages(new PageFeaturesOptions
        {
            DataDirectory = folder.Path,
            WindowsAccess = capability => capability == WindowsCapability.Camera ? cameraAccess : WindowsCapabilityAccess.Allowed,
        });
        Pages.Host = host;
        Pages.ChromiumPages = tab =>
        {
            if (!chromiumPages.TryGetValue(tab, out var page)) chromiumPages[tab] = page = new FakeChromiumPage();
            return page;
        };
    }

    public void Dispose()
    {
        fixture.Dispose();
        folder.Dispose();
    }

    private BrowserModel Model => fixture.Model;
    private PageFeatures Pages => Model.Pages;

    private FakeChromiumPage PageOf(BrowserTab tab) => (FakeChromiumPage)Pages.ChromiumPages!(tab)!;

    // ---- Find ----

    [Fact]
    public void FindsInChromiumAndMovesOnWithTheSameText()
    {
        var (tab, _) = fixture.OpenChromiumTab("https://example.com/");
        Pages.ShowFindBar();
        Assert.True(Pages.ShowsFindBar);
        Pages.FindText = "mosaic";
        Assert.Equal("1 of 3", Pages.FindLabel);
        Pages.FindNext();
        Pages.FindNext(backwards: true);
        Pages.FindMatchCase = true;
        Assert.Equal(
            ["find mosaic case=False again=False back=False", "find mosaic case=False again=True back=False",
             "find mosaic case=False again=True back=True", "find mosaic case=True again=False back=False"],
            PageOf(tab).Actions);

        Pages.CloseFindBar();
        Assert.False(Pages.ShowsFindBar);
        Assert.Equal("stopFind", PageOf(tab).Actions[^1]);
    }

    [Fact]
    public void FindsInGeckoThroughTheAgent()
    {
        fixture.Gecko.Handlers["find"] = _ => JsonNode.Parse("""{"found":true,"current":2,"total":7,"limit":1000}""");
        var tab = fixture.OpenGeckoTab("https://example.com/");
        Pages.ShowFindBar();
        Pages.FindText = "web";
        Assert.Equal("2 of 7", Pages.FindLabel);
        var call = fixture.Gecko.CallsTo("find").Single();
        Assert.Equal(tab.GeckoId, call[0]);
        Assert.Equal("web", call[1]);
        Assert.False(((JsonObject)call[2]!)["again"]!.GetValue<bool>());

        // Switching tabs hides the bar and ends the search there.
        Model.NewTab("https://other.example/", EngineKind.Gecko);
        Assert.False(Pages.IsFindBarVisible);
        Assert.Single(fixture.Gecko.CallsTo("stopFind"));
    }

    [Fact]
    public void FindShortcutsPressedInsideGeckoOpenTheFindBar()
    {
        fixture.OpenGeckoTab("https://example.com/");
        _ = Pages;
        fixture.Gecko.ReportEvents([Snapshots.Tab(1, "https://example.com/", selected: true)], "find");
        Assert.True(Pages.ShowsFindBar);
    }

    [Fact]
    public void TheStartPageCannotBeSearched()
    {
        Pages.ShowFindBar();
        Assert.False(Pages.IsFindBarVisible);
        Assert.Equal(["beep"], host.Events);
    }

    // ---- Zoom ----

    [Fact]
    public async Task ZoomIsRememberedPerSiteAndSharedByBothEngines()
    {
        var (chromium, _) = fixture.OpenChromiumTab("https://www.example.com/a");
        Pages.ZoomIn();
        Assert.Equal(1.1, Pages.ZoomOf(chromium));
        Assert.Equal(1.1, PageOf(chromium).Zoom);
        Assert.Equal("Zoom 110 %", Model.Notice);
        Assert.Equal("110 %", Pages.ZoomLabel);

        // A Gecko tab on the same site opens at the remembered zoom.
        var gecko = fixture.OpenGeckoTab("https://example.com/b");
        Assert.Equal(1.1, Pages.ZoomOf(gecko));
        Assert.Contains(fixture.Gecko.CallsTo("setZoom"), call => (int)call[0]! == gecko.GeckoId && (double)call[1]! == 1.1);

        // Zooming there moves the Chromium tab too.
        Pages.ZoomIn();
        Assert.Equal(1.25, PageOf(chromium).Zoom);
        Pages.ResetZoom();
        Assert.Equal(1, PageOf(chromium).Zoom);
        Assert.Empty(Pages.ZoomStore.Sites);

        Pages.SetZoom(2);
        await Pages.ZoomStore.FlushAsync();
        Assert.Equal(2, new SiteZoomStore(Pages.ZoomStore.FilePath).Factor("https://example.com/"));
    }

    [Fact]
    public void AZoomChangedInsideGeckoIsRemembered() => TestLoop.Run(async () =>
    {
        _ = Pages;
        var tab = fixture.OpenGeckoTab("https://example.com/");
        var id = tab.GeckoId!.Value;
        fixture.Gecko.Handlers["pageActivity"] = _ => JsonNode.Parse($$"""[{"id": {{id}}, "zoom": 1.5, "muted": false}]""");
        // Not while the page is loading: Gecko is still applying its own zoom then.
        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Pages.ZoomOf(tab));
        fixture.Gecko.Report(Snapshots.Tab(id, "https://example.com/", selected: true));
        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1.5, Pages.ZoomOf(tab));
        Assert.Equal(1.5, Pages.ZoomStore.Factor("https://example.com/"));
        fixture.Gecko.Stop();
    });

    // ---- Mute ----

    [Fact]
    public void MuteFollowsThePageToTheOtherEngine()
    {
        var (tab, _) = fixture.OpenChromiumTab("https://example.com/");
        Model.ApplyMute(tab, true);
        Assert.True(PageOf(tab).Muted);
        Model.SetEngine(EngineKind.Gecko);
        Assert.Contains(fixture.Gecko.CallsTo("setMuted"), call => (int)call[0]! == tab.GeckoId && (bool)call[1]!);

        // Gecko reports it unmuted (its own tab menu): Mosaic puts it back.
        fixture.Gecko.Calls.Clear();
        Pages.ApplyGeckoActivity(JsonNode.Parse($$"""[{"id": {{tab.GeckoId}}, "zoom": 1, "muted": false}]""")!.AsArray());
        Assert.Single(fixture.Gecko.CallsTo("setMuted"));
    }

    [Fact]
    public void AMuteMosaicDidNotSetIsLeftAlone()
    {
        var tab = fixture.OpenGeckoTab("https://example.com/");
        _ = Pages;
        fixture.Gecko.Calls.Clear();
        Pages.ApplyGeckoActivity(JsonNode.Parse($$"""[{"id": {{tab.GeckoId}}, "zoom": 1, "muted": true}]""")!.AsArray());
        Assert.Empty(fixture.Gecko.CallsTo("setMuted"));
    }

    [Fact]
    public void ChromiumAudioIsReported()
    {
        var (tab, _) = fixture.OpenChromiumTab("https://example.com/");
        var changes = 0;
        Pages.AudioChanged += _ => changes++;
        Pages.ChromiumAudioChanged(tab, true);
        Pages.ChromiumAudioChanged(tab, true);
        Assert.True(Pages.IsPlayingAudio(tab));
        Assert.Equal(1, changes);
    }

    // ---- Permissions ----

    [Fact]
    public void ChromiumRequestsArePromptedMergedAndRemembered()
    {
        var (tab, _) = fixture.OpenChromiumTab("https://meet.example/room");
        var camera = new RecordingResponder();
        var microphone = new RecordingResponder();
        Pages.ChromiumPermissionRequested(tab, new PermissionRequestInfo("https://meet.example", PermissionKind.Camera), camera);
        Pages.ChromiumPermissionRequested(tab, new PermissionRequestInfo("https://meet.example", PermissionKind.Microphone), microphone);
        var prompt = Assert.Single(Pages.PermissionPrompts);
        Assert.Same(prompt, Pages.CurrentPrompt);
        Assert.True(Pages.HasOverlay);
        Assert.Equal("meet.example wants to use your camera and microphone", prompt.Sentence);
        Assert.Empty(prompt.Notes);

        Pages.Answer(prompt, SitePermissionDecision.Allow);
        Assert.Equal(["Allow"], camera.Answers);
        Assert.Equal(["Allow"], microphone.Answers);
        Assert.Null(Pages.CurrentPrompt);
        Assert.Equal(SitePermissionDecision.Allow, Model.SitePermissions.Decision(SitePermission.Microphone, "https://meet.example"));

        // Asked again: answered without a prompt.
        var again = new RecordingResponder();
        Pages.ChromiumPermissionRequested(tab, new PermissionRequestInfo("https://meet.example", PermissionKind.Camera), again);
        Assert.Equal(["Allow"], again.Answers);
        Assert.Empty(Pages.PermissionPrompts);
    }

    [Fact]
    public void APromptSaysWhenWindowsBlocksTheDevice()
    {
        cameraAccess = WindowsCapabilityAccess.DeniedForDesktopApps;
        var (tab, _) = fixture.OpenChromiumTab("https://meet.example/");
        Pages.ChromiumPermissionRequested(tab, new PermissionRequestInfo("https://meet.example", PermissionKind.Camera), new RecordingResponder());
        var prompt = Pages.CurrentPrompt!;
        Assert.Equal([WindowsCapability.Camera], prompt.BlockedByWindows);
        Assert.Contains("Let desktop apps access your camera", Assert.Single(prompt.Notes));
        Pages.OpenWindowsSettings(prompt);
        Assert.Contains("settings ms-settings:privacy-webcam", host.Events);
        Pages.Answer(prompt, SitePermissionDecision.Allow);
        Assert.Contains("Windows is blocking the camera", Model.Notice);
    }

    [Fact]
    public void PromptsBelongToThePageThatAsked()
    {
        var (tab, browser) = fixture.OpenChromiumTab("https://a.example/");
        var responder = new RecordingResponder();
        Pages.ChromiumPermissionRequested(tab, new PermissionRequestInfo("https://a.example", PermissionKind.Location), responder);
        // Another tab in front: the prompt waits, unseen.
        var other = Model.NewTab();
        Assert.Null(Pages.CurrentPrompt);
        Model.Select(tab);
        Assert.NotNull(Pages.CurrentPrompt);
        // Leaving the site dismisses it.
        browser.RaiseUrl("https://b.example/");
        Assert.Equal(["Dismiss"], responder.Answers);
        Assert.Empty(Pages.PermissionPrompts);
        Model.Close(other);
    }

    [Fact]
    public void GeckoRequestsArriveWithTheirDetailsAndAreAnsweredThroughTheAgent()
    {
        var tab = fixture.OpenGeckoTab("https://maps.example/");
        _ = Pages;
        Assert.Single(fixture.Gecko.CallsTo("configurePageFeatures"));
        Assert.True(((JsonObject)fixture.Gecko.CallsTo("configurePageFeatures")[0][0]!)["keepEvents"]!.GetValue<bool>());

        fixture.Gecko.Handlers["takePageEvents"] = _ => JsonNode.Parse($$"""
            [{"type":"permissionRequest","tab":{{tab.GeckoId}},"detail":"{\"id\":7,\"origin\":\"https://maps.example\",\"kinds\":[\"location\"],\"other\":[]}"}]
            """);
        fixture.Gecko.ReportEvents([Snapshots.Tab(tab.GeckoId!.Value, "https://maps.example/", selected: true)], "permissionRequest");
        var prompt = Assert.Single(Pages.PermissionPrompts);
        Assert.Equal("maps.example wants to know your location", prompt.Sentence);
        Assert.Equal(EngineKind.Gecko, prompt.Engine);

        Pages.Answer(prompt, SitePermissionDecision.Block);
        var mirrored = fixture.Gecko.CallsTo("setSitePermissions").Last();
        Assert.Equal("block", ((JsonArray)mirrored[0]!)[0]!["decision"]!.GetValue<string>());
        Assert.Equal([7, false], fixture.Gecko.CallsTo("answerPermission").Single());

        // A cancelled request takes its prompt away.
        fixture.Gecko.Handlers["takePageEvents"] = _ => JsonNode.Parse($$"""
            [{"type":"permissionRequest","tab":{{tab.GeckoId}},"detail":"{\"id\":8,\"origin\":\"https://maps.example\",\"kinds\":[\"notifications\"],\"other\":[]}"},
             {"type":"permissionCancel","tab":null,"detail":"{\"id\":8}"}]
            """);
        fixture.Gecko.ReportEvents([Snapshots.Tab(tab.GeckoId!.Value, "https://maps.example/", selected: true)], "permissionRequest", "permissionCancel");
        Assert.Empty(Pages.PermissionPrompts);
    }

    [Fact]
    public void DecisionsMadeInSettingsReachBothEngines()
    {
        fixture.OpenGeckoTab("https://example.com/");
        var mirrored = new List<string>();
        Pages.ChromiumPermissionMirror = (permission, decision, site) => mirrored.Add($"{site} {permission} {decision}");
        Model.SitePermissions.Set(SitePermissionDecision.Allow, SitePermission.Notifications, "https://news.example");
        Assert.Equal(["https://news.example Notifications Allow"], mirrored);
        var sent = fixture.Gecko.CallsTo("setSitePermissions").Last();
        Assert.Equal("notifications", ((JsonArray)sent[0]!)[0]!["permission"]!.GetValue<string>());
    }

    // ---- Pop-ups ----

    [Fact]
    public void ChromiumPopupsWithoutAGestureAreBlockedWithANotice()
    {
        var (tab, browser) = fixture.OpenChromiumTab("https://ads.example/");
        Assert.Equal(PopupVerdict.Open, Pages.ChromiumPopupRequested(tab, "https://ads.example/clicked", userInitiated: true));
        Assert.Equal(PopupVerdict.Block, Pages.ChromiumPopupRequested(tab, "https://ads.example/popup", userInitiated: false));
        // The browser still reports the window; the model does not open a tab for it.
        var before = Model.Tabs.Count;
        browser.RaiseNewTab("https://ads.example/popup");
        Assert.Equal(before, Model.Tabs.Count);
        var notice = Assert.Single(Pages.PopupNotices);
        Assert.Same(notice, Pages.CurrentPopupNotice);
        Assert.Equal("Pop-up blocked", notice.Title);

        Pages.OpenPopups(notice, alwaysAllow: true);
        Assert.Equal(before + 1, Model.Tabs.Count);
        Assert.Equal("https://ads.example/popup", Model.Tabs[^1].Url);
        Assert.Equal(PopupVerdict.Open, Pages.ChromiumPopupRequested(tab, "https://ads.example/next", userInitiated: false));
    }

    [Fact]
    public void GeckoPopupNoticesOpenThroughTheAgent()
    {
        var tab = fixture.OpenGeckoTab("https://ads.example/");
        _ = Pages;
        fixture.Gecko.Handlers["takePageEvents"] = _ => JsonNode.Parse($$"""
            [{"type":"popupBlocked","tab":{{tab.GeckoId}},"detail":"{\"count\":2,\"urls\":[\"https://ads.example/1\",\"https://ads.example/2\"]}"}]
            """);
        fixture.Gecko.ReportEvents([Snapshots.Tab(tab.GeckoId!.Value, "https://ads.example/", selected: true)], "popupBlocked");
        var notice = Assert.Single(Pages.PopupNotices);
        Assert.Equal("2 pop-ups blocked", notice.Title);
        Pages.OpenPopups(notice, alwaysAllow: false);
        Assert.Equal([tab.GeckoId], fixture.Gecko.CallsTo("openBlockedPopups").Single());
    }

    // ---- Dialogs ----

    [Fact]
    public void ChromiumDialogsAreShownAndFinished()
    {
        var (tab, _) = fixture.OpenChromiumTab("https://forms.example/");
        var responder = new RecordingResponder();
        Pages.ChromiumDialogOpening(tab, PageDialogKind.Prompt, "Your name?", "Ada", "https://forms.example/page", responder);
        var dialog = Pages.CurrentDialog!;
        Assert.Equal("forms.example says", dialog.Heading);
        Assert.True(dialog.HasTextField);
        Pages.Finish(dialog, accepted: true, text: "Grace");
        Assert.Equal(["True:Grace"], responder.Answers);
        Assert.Null(Pages.CurrentDialog);

        // Closing the tab cancels what is still open.
        var leave = new RecordingResponder();
        Pages.ChromiumDialogOpening(tab, PageDialogKind.BeforeUnload, "", "", "https://forms.example/", leave);
        Model.Close(tab);
        Assert.Equal(["False:"], leave.Answers);
    }

    // ---- Print, PDF, devtools, PiP, screenshots, full screen ----

    [Fact]
    public void PrintAndDevToolsGoToTheEngineOfThePage()
    {
        var (chromium, _) = fixture.OpenChromiumTab("https://example.com/");
        Assert.True(Pages.Print());
        Assert.True(Pages.ToggleDevTools());
        Assert.Equal(["print", "devtools"], PageOf(chromium).Actions);

        var gecko = fixture.OpenGeckoTab("https://example.org/");
        Pages.Print();
        Pages.ToggleDevTools();
        Assert.Equal([gecko.GeckoId], fixture.Gecko.CallsTo("print").Single());
        Assert.Equal([gecko.GeckoId], fixture.Gecko.CallsTo("toggleDevTools").Single());
    }

    [Fact]
    public void ExportsPdfInBothEngines() => TestLoop.Run(async () =>
    {
        fixture.OpenChromiumTab("https://example.com/");
        host.SavePath = folder.File("page.pdf");
        Assert.Equal(host.SavePath, await Pages.ExportPdfAsync());
        Assert.Equal("ask example.com.pdf", host.Events[0]);
        Assert.Equal("Saved “page.pdf”", Model.Notice);

        var gecko = fixture.OpenGeckoTab("https://example.org/");
        var target = folder.File("gecko.pdf");
        fixture.Gecko.Handlers["saveAsPDF"] = arguments =>
        {
            File.WriteAllText((string)arguments[1]!, "%PDF");
            return JsonValue.Create(true);
        };
        Assert.Equal(target, await Pages.ExportPdfAsync(target));
        Assert.Equal([gecko.GeckoId, target], fixture.Gecko.CallsTo("saveAsPDF").Single());
    });

    [Fact]
    public void ScreenshotsGoToPicturesScreenshotsByDefault() => TestLoop.Run(async () =>
    {
        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");
        browser.RaiseTitle("A: page/with*bad?chars");
        var shot = await Pages.TakeScreenshotAsync(fullPage: true);
        Assert.NotNull(shot);
        var path = shot.Value.SavedTo!;
        Assert.StartsWith(folder.File("Screenshots"), path);
        Assert.StartsWith("A- page-with-bad-chars (full page) ", Path.GetFileName(path));
        Assert.True(File.Exists(path));
        Assert.Equal("capture full", PageOf(tab).Actions.Single());

        Assert.NotNull(await Pages.TakeScreenshotAsync(fullPage: false, ScreenshotTarget.Clipboard));
        Assert.Contains("copy image 4", host.Events);
    });

    [Fact]
    public void GeckoScreenshotsAreReadFromTheFileTheAgentWrites() => TestLoop.Run(async () =>
    {
        var tab = fixture.OpenGeckoTab("https://example.org/");
        fixture.Gecko.Handlers["screenshot"] = arguments =>
        {
            File.WriteAllBytes((string)arguments[1]!, [1, 2, 3]);
            return JsonNode.Parse("""{"width":1200,"height":16384,"cropped":true}""");
        };
        var target = folder.File("shot.png");
        var shot = await Pages.TakeScreenshotAsync(fullPage: true, ScreenshotTarget.File, target);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(target));
        Assert.True(shot!.Value.Capture.Cropped);
        Assert.Contains("cut off", Model.Notice);
        var call = fixture.Gecko.CallsTo("screenshot").Single();
        Assert.Equal(tab.GeckoId, call[0]);
        Assert.False(File.Exists((string)call[1]!)); // the agent's file is removed
    });

    [Fact]
    public void PictureInPictureSaysWhenThereIsNothingToShow() => TestLoop.Run(async () =>
    {
        var (tab, _) = fixture.OpenChromiumTab("https://example.com/");
        PageOf(tab).PictureInPicture = "none";
        await Pages.TogglePictureInPictureAsync();
        Assert.Equal("This page has no video to show in Picture in Picture.", Model.Notice);
    });

    [Fact]
    public void PageFullScreenFillsTheWindowUntilThePageLeavesOrTheTabChanges()
    {
        var (tab, _) = fixture.OpenChromiumTab("https://video.example/");
        Pages.ChromiumFullScreenChanged(tab, true);
        Assert.True(Pages.IsPageFullScreen);
        Assert.Equal(["fullscreen True"], host.Events);
        Model.NewTab();
        Assert.False(Pages.IsPageFullScreen);
        Assert.Equal("fullscreen False", host.Events[^1]);
        Assert.Contains("exitFullScreen", PageOf(tab).Actions);

        var gecko = fixture.OpenGeckoTab("https://video.example/");
        fixture.Gecko.Handlers["takePageEvents"] = _ => JsonNode.Parse($$"""
            [{"type":"pageFullscreen","tab":{{gecko.GeckoId}},"detail":"{\"value\":true}"}]
            """);
        fixture.Gecko.ReportEvents([Snapshots.Tab(gecko.GeckoId!.Value, "https://video.example/", selected: true)], "pageFullscreen");
        Assert.Equal(gecko.Id, Pages.FullScreenTabId);
        Pages.EndFullScreen();
        Assert.Single(fixture.Gecko.CallsTo("exitFullscreen"));
    }

    // ---- Context menu ----

    [Fact]
    public void TheContextMenuAddsMosaicItemsAndDropsNewWindows()
    {
        var native = new List<NativeMenuItem>
        {
            new("openLinkInNewWindow", "Open link in new window", 1),
            new("copyLinkLocation", "Copy link", 2),
            new("", "", 0, NativeMenuItemKind.Separator),
            new("createQrCode", "Create QR code", 3),
            new("inspectElement", "Inspect", 4),
        };
        var link = new PageContextTarget(PageContextKind.Page, "https://a.example/", LinkUrl: "https://b.example/");
        var menu = PageContextMenu.Build(link, native, EngineKind.Chromium, otherEngineAvailable: true, SearchEngine.DuckDuckGo);
        Assert.Equal(["Open Link in New Tab", "Open Link in Mozilla Tab", "-", "Copy link", "-", "Inspect"],
            menu.Select(item => item.IsSeparator ? "-" : item.Label));

        var page = new PageContextTarget(PageContextKind.Page, "https://a.example/");
        var pageMenu = PageContextMenu.Build(page, [new("back", "Back", 9), new("inspectElement", "Inspect", 4)], EngineKind.Chromium, true, SearchEngine.Google);
        Assert.Equal(["Back", "-", "Find in Page…", "Take Screenshot", "Save Page as PDF…", "-", "Inspect"],
            pageMenu.Select(item => item.IsSeparator ? "-" : item.Label));

        var selection = new PageContextTarget(PageContextKind.SelectedText, "https://a.example/", SelectionText: "  the quick\nbrown fox jumps over the lazy dog ");
        var selectionMenu = PageContextMenu.Build(selection, [new("copy", "Copy", 5)], EngineKind.Chromium, true, SearchEngine.Google);
        Assert.Equal(["Copy", "Search Google for “the quick brown fox jum…”"], selectionMenu.Select(item => item.Label));
    }

    [Fact]
    public void ContextMenuCommandsOpenTabs()
    {
        var (tab, _) = fixture.OpenChromiumTab("https://a.example/");
        var target = new PageContextTarget(PageContextKind.Page, tab.Url, LinkUrl: "https://b.example/");
        Pages.RunMenuCommand(tab, PageMenuCommand.OpenLinkInNewTab, target);
        Assert.Equal("https://b.example/", Model.Tabs[^1].Url);
        Assert.Same(tab, Model.SelectedTab); // opened in the background
        Pages.RunMenuCommand(tab, PageMenuCommand.OpenLinkInOtherEngine, target);
        Assert.Equal(EngineKind.Gecko, Model.SelectedTab!.Engine);
    }

    // ---- Automation ----

    [Fact]
    public void AutomationDrivesThePageFeatures() => TestLoop.Run(async () =>
    {
        // Through the handler the app registers (registering here would change every test's dump).
        async Task<JsonNode> Send(string command, string value = "") =>
            JsonNode.Parse((await EngineAutomation.HandleAsync(Model, command, value, null))!.Json)!;

        var (tab, _) = fixture.OpenChromiumTab("https://example.com/");
        Assert.Null(await EngineAutomation.HandleAsync(Model, "navigate", "x", null));
        Assert.True((await Send("page.find", "hello"))["ok"]!.GetValue<bool>());
        Assert.True((await Send("page.zoomIn"))["ok"]!.GetValue<bool>());
        Assert.True((await Send("page.mute", "true"))["ok"]!.GetValue<bool>());
        Assert.True((await Send("page.setPermission", "https://x.example|camera|block"))["ok"]!.GetValue<bool>());
        Assert.True((await Send("page.pdf", folder.File("auto.pdf")))["result"]!["ok"]!.GetValue<bool>());
        var shot = await Send("page.screenshot", "full|" + folder.File("auto.png"));
        Assert.Equal(800, shot["result"]!["size"]!["width"]!.GetValue<int>());
        Assert.True(File.Exists(folder.File("auto.png")));
        Assert.False((await Send("page.permission", "allow"))["ok"]!.GetValue<bool>());
        Assert.Contains("Unknown command", (await Send("page.nonsense"))["error"]!.GetValue<string>());

        var page = Pages.AutomationState();
        Assert.True(page["findBarVisible"]!.GetValue<bool>());
        Assert.Equal("1 of 3", page["findStatus"]!["label"]!.GetValue<string>());
        Assert.Equal(1.1, page["tabs"]![Model.Tabs.IndexOf(tab)]!["zoom"]!.GetValue<double>());
        Assert.True(page["tabs"]![Model.Tabs.IndexOf(tab)]!["muted"]!.GetValue<bool>());
        Assert.Equal("https://x.example camera block", page["sitePermissions"]![0]!.GetValue<string>());
        Assert.Equal("example.com 1.1", page["siteZoom"]![0]!.GetValue<string>());
        Assert.Equal("Allowed", page["windowsPrivacy"]!["camera"]!.GetValue<string>());
    });
}
