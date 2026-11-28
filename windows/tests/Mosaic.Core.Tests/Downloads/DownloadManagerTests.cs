using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Downloads;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Downloads;

internal sealed class FakeChromiumDownload(string url, string resultPath, long total = 100) : IChromiumDownload
{
    public string Url { get; } = url;
    public string ResultFilePath { get; set; } = resultPath;
    public string? MimeType { get; set; }
    public long TotalBytes { get; set; } = total;
    public long ReceivedBytes { get; set; }
    public ChromiumDownloadState State { get; set; }
    public string? InterruptReason { get; set; }
    public bool CanResume { get; set; }
    public List<string> Actions { get; } = [];

    public void Pause() { Actions.Add("pause"); State = ChromiumDownloadState.Paused; CanResume = true; }
    public void Resume() { Actions.Add("resume"); State = ChromiumDownloadState.InProgress; }
    public void Cancel() { Actions.Add("cancel"); State = ChromiumDownloadState.Cancelled; }
}

internal sealed class FakeFdm : IFreeDownloadManager
{
    public bool IsInstalled { get; set; } = true;
    public string? Version => "6.36";
    public List<FdmDownloadRequest> Sent { get; } = [];
    public Action<FdmOutcome>? Answer { get; private set; }
    public void Refresh() { }

    public Task<FdmRoute> SendAsync(FdmDownloadRequest request, bool bringToFront, Action<FdmOutcome> onOutcome)
    {
        Sent.Add(request);
        Answer = onOutcome;
        return Task.FromResult(FdmRoute.Integration);
    }
}

internal sealed class FakeChromiumService : IChromiumDownloadService
{
    public List<string> Started { get; } = [];
    public bool CanStart { get; set; } = true;
    public string? UserAgent => "Chromium-UA";
    public bool StartDownload(string url) { Started.Add(url); return CanStart; }
    public Task<string?> CookiesAsync(string url) => Task.FromResult<string?>("sid=1");
}

/// <summary>A download manager on fakes, saving into a throwaway folder.</summary>
internal sealed class DownloadsFixture : IDisposable
{
    public DownloadsFixture()
    {
        Directory.CreateDirectory(Folder);
        Settings = new DownloadSettings(null) { FolderPath = Folder };
        Manager = new DownloadManager(Gecko, Path.Combine(Root, "history.json"), Settings, Time)
        {
            Shell = Shell, Fdm = Fdm, Chromium = Chromium,
            ShowNotice = notice => Notices.Add(notice),
            OpenPage = () => PageOpens++,
            CurrentEngine = () => EngineKind.Chromium,
        };
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"mosaic-dm-{Guid.NewGuid():N}");
    public string Folder => Path.Combine(Root, "Downloads");
    public FakeGeckoEngine Gecko { get; } = new();
    public ManualTimeProvider Time { get; } = new();
    public RecordingDownloadShell Shell { get; } = new();
    public FakeFdm Fdm { get; } = new() { IsInstalled = false };
    public FakeChromiumService Chromium { get; } = new();
    public DownloadSettings Settings { get; }
    public DownloadManager Manager { get; }
    public List<string> Notices { get; } = [];
    public int PageOpens { get; private set; }

    public DownloadRecord Only => Assert.Single(Manager.Items);

    /// <summary>A Chromium download through the whole start: returns the destination Mosaic chose.</summary>
    public async Task<(FakeChromiumDownload Download, string? Path)> StartChromiumAsync(
        string url = "https://example.com/files/report.pdf", string? suggested = null, string? page = "https://example.com/")
    {
        var download = new FakeChromiumDownload(url, "");
        var path = await Manager.ChromiumStartingAsync(download, suggested, page);
        if (path is not null) download.ResultFilePath = path;
        return (download, path);
    }

    public void Complete(FakeChromiumDownload download, string content = "data")
    {
        File.WriteAllText(download.ResultFilePath, content);
        download.ReceivedBytes = download.TotalBytes;
        download.State = ChromiumDownloadState.Completed;
        Manager.ChromiumUpdated(download);
    }

    public static JsonObject GeckoSnapshot(int version, params JsonObject[] downloads) => new()
    {
        ["version"] = version,
        ["downloads"] = new JsonArray(downloads),
        ["commands"] = new JsonArray(),
    };

    public static JsonObject GeckoItem(int id, string url, string path, string state = "inProgress", long current = 10, long total = 100) => new()
    {
        ["id"] = id, ["url"] = url, ["referrer"] = "https://example.com/", ["path"] = path, ["contentType"] = "application/zip",
        ["totalBytes"] = total, ["currentBytes"] = current, ["speed"] = 1000, ["state"] = state, ["error"] = "",
        ["hasPartialData"] = false, ["restored"] = false,
    };

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

public class DownloadManagerChromiumTests
{
    [Fact]
    public void SavesIntoTheDownloadFolderAndFinishes() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        await f.Manager.LoadAsync();
        var (download, path) = await f.StartChromiumAsync();
        Assert.Equal(Path.Combine(f.Folder, "report.pdf"), path);
        Assert.Equal(DownloadState.InProgress, f.Only.State);
        Assert.Equal("https://example.com/", f.Only.Referrer);

        download.ReceivedBytes = 50;
        f.Manager.ChromiumUpdated(download);
        Assert.Equal(50, f.Only.ReceivedBytes);
        Assert.Equal(0.5, f.Manager.OverallProgress);
        Assert.Equal((1, 0.5), f.Shell.LastProgress);

        f.Complete(download);
        var record = f.Only;
        Assert.Equal(DownloadState.Completed, record.State);
        Assert.Equal(4, record.TotalBytes); // the real size on disk
        Assert.True(f.Manager.HasUnseenCompletion);
        Assert.Equal(["Downloaded “report.pdf”"], f.Notices);
        Assert.Equal((0, (double?)null), f.Shell.LastProgress);
        Assert.Contains(OperatingSystem.IsWindows() ? "Written" : "Unsupported", f.Manager.LastMarkOfTheWeb);
        f.Manager.Flush();
        Assert.Contains("report.pdf", File.ReadAllText(f.Manager.HistoryPath));
    });

    [Fact]
    public void NotifiesWhenMosaicIsInTheBackground() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Shell.IsAppActive = false;
        var (download, _) = await f.StartChromiumAsync(url: "https://x.org/setup.exe");
        f.Complete(download);
        Assert.Empty(f.Notices);
        Assert.Equal("Download finished: “setup.exe”. Check the warning before opening it.", f.Manager.LastNotification);
        Assert.Equal("Download finished: “setup.exe”. Check the warning before opening it.", f.Shell.LastNotification);
    });

    [Fact]
    public void NeverGivesTwoDownloadsTheSameName() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        File.WriteAllText(Path.Combine(f.Folder, "report.pdf"), "old");
        var (_, first) = await f.StartChromiumAsync();
        var (_, second) = await f.StartChromiumAsync();
        Assert.Equal(Path.Combine(f.Folder, "report (1).pdf"), first);
        Assert.Equal(Path.Combine(f.Folder, "report (2).pdf"), second);
    });

    [Fact]
    public void SanitisesTheSuggestedNameButKeepsItForTheSafetyCheck() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        var (download, path) = await f.StartChromiumAsync(suggested: "invoice‮fdp.exe");
        Assert.Equal(Path.Combine(f.Folder, "invoicefdp.exe"), path);
        f.Complete(download);
        Assert.True(f.Only.NeedsSafetyConfirmation);
        Assert.Contains("reverse", f.Only.Assessment.Summary);
        // Opening asks first.
        var open = f.Manager.OpenAsync(f.Only.Id);
        await TestLoop.Settle();
        Assert.Equal("safety", f.Manager.Prompter.Pending?.Kind);
        f.Manager.Prompter.Answer(1);
        await open;
        Assert.Null(f.Manager.LastOpenRequest);
        f.Manager.Keep(f.Only.Id);
        await f.Manager.OpenAsync(f.Only.Id);
        Assert.Equal(path, f.Manager.LastOpenRequest);
        Assert.DoesNotContain(f.Shell.Requests, r => r.StartsWith("open:")); // scripted: nothing opened on screen
    });

    [Fact]
    public void SortsIntoFoldersWhenRulesAreOn() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Settings.Rules = new DownloadOrganizationRules(true);
        var (_, path) = await f.StartChromiumAsync(url: "https://x.org/photo.png");
        Assert.Equal(Path.Combine(f.Folder, "Images", "photo.png"), path);
        Assert.True(Directory.Exists(Path.Combine(f.Folder, "Images")));
    });

    [Fact]
    public void PausesResumesAndCancels() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        var (download, _) = await f.StartChromiumAsync();
        var id = f.Only.Id;
        f.Manager.Pause(id);
        f.Manager.ChromiumUpdated(download);
        Assert.Equal(DownloadState.Paused, f.Only.State);
        Assert.True(f.Only.CanResume);
        f.Manager.Resume(id);
        f.Manager.ChromiumUpdated(download);
        Assert.Equal(DownloadState.InProgress, f.Only.State);
        f.Manager.Cancel(id);
        f.Manager.ChromiumUpdated(download);
        Assert.Equal(DownloadState.Cancelled, f.Only.State);
        Assert.Equal(["pause", "resume", "cancel"], download.Actions);
    });

    [Fact]
    public void RetriesAnInterruptedDownloadInPlace() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        var (download, path) = await f.StartChromiumAsync();
        download.State = ChromiumDownloadState.Interrupted;
        download.InterruptReason = "Network error";
        download.CanResume = true;
        f.Manager.ChromiumUpdated(download);
        Assert.Equal(DownloadState.Failed, f.Only.State);
        Assert.Equal("Network error", f.Only.ErrorMessage);
        f.Manager.Retry(f.Only.Id);
        Assert.Equal(["resume"], download.Actions);

        // Once the operation is gone (e.g. after a restart), a retry starts it again in Chromium
        // and reuses the entry and its file name.
        download.State = ChromiumDownloadState.Cancelled;
        f.Manager.ChromiumUpdated(download);
        f.Manager.Retry(f.Only.Id);
        Assert.Equal(["https://example.com/files/report.pdf"], f.Chromium.Started);
        var (_, again) = await f.StartChromiumAsync();
        Assert.Equal(path, again);
        Assert.Single(f.Manager.Items);
    });

    [Fact]
    public void RecognisesARetryThatWasRedirected() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        var (download, path) = await f.StartChromiumAsync(url: "https://x.org/get/latest");
        download.State = ChromiumDownloadState.Cancelled;
        f.Manager.ChromiumUpdated(download);
        f.Manager.Retry(f.Only.Id);
        // The helper asked for /get/latest; the server redirected to the file itself.
        var redirected = new FakeChromiumDownload("https://cdn.x.org/app-2.0.zip", "");
        var again = await f.Manager.ChromiumStartingAsync(redirected, null, null, requestedUrl: "https://x.org/get/latest");
        Assert.Equal(path, again);
        Assert.Single(f.Manager.Items);
        Assert.Equal(DownloadState.InProgress, f.Only.State);
    });

    [Fact]
    public void ChromiumStartFailureMovesToGecko() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Gecko.Handlers["startDownload"] = _ => JsonValue.Create(3);
        f.Manager.StartInChromium("https://x.org/page.html");
        Assert.Empty(f.Manager.Items); // Chromium has not answered yet
        f.Manager.ChromiumStartFailed("https://x.org/page.html");
        await TestLoop.Settle();
        Assert.Equal(DownloadEngine.Gecko, f.Only.Engine);
        Assert.Single(f.Gecko.CallsTo("startDownload"));
        f.Manager.ChromiumStartFailed("https://x.org/page.html"); // only once
        Assert.Single(f.Manager.Items);
    });

    [Fact]
    public void FallsBackToGeckoWhenChromiumCannotStart() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Chromium.CanStart = false;
        f.Gecko.Handlers["startDownload"] = _ => JsonValue.Create(7);
        f.Manager.StartInChromium("https://x.org/a.zip");
        await TestLoop.Settle();
        Assert.Equal(DownloadEngine.Gecko, f.Only.Engine);
        Assert.Equal(Path.Combine(f.Folder, "a.zip"), f.Gecko.CallsTo("startDownload").Single()[1]);
    });

    [Fact]
    public void AsksAboutDuplicates() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        var (first, _) = await f.StartChromiumAsync();
        f.Complete(first);
        var second = f.StartChromiumAsync();
        await TestLoop.Settle();
        Assert.Equal("duplicate", f.Manager.Prompter.Pending?.Kind);
        Assert.Equal(["Open Existing File", "Download Again", "Cancel"], f.Manager.Prompter.Pending!.Buttons);
        f.Manager.Prompter.Answer(2);
        Assert.Null((await second).Path);
        Assert.Single(f.Manager.Items);

        var third = f.StartChromiumAsync();
        await TestLoop.Settle();
        f.Manager.Prompter.Answer(1);
        Assert.Equal(Path.Combine(f.Folder, "report (1).pdf"), (await third).Path);
    });

    [Fact]
    public void AsksWhereToSave() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Settings.AskWhereToSave = true;
        var start = f.StartChromiumAsync();
        await TestLoop.Settle();
        Assert.Equal("save", f.Manager.Prompter.Pending?.Kind);
        Assert.Equal("report.pdf", f.Manager.Prompter.Pending?.SuggestedName);
        var chosen = Path.Combine(f.Root, "Elsewhere", "mine.pdf");
        f.Manager.Prompter.Save(chosen);
        Assert.Equal(chosen, (await start).Path);
        Assert.Equal("mine.pdf", f.Only.FileName);

        var cancelled = f.StartChromiumAsync(url: "https://x.org/other.pdf");
        await TestLoop.Settle();
        f.Manager.Prompter.Answer(1);
        Assert.Null((await cancelled).Path);
        Assert.Single(f.Manager.Items);
    });

    [Fact]
    public void HandsOffToFreeDownloadManager() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Fdm.IsInstalled = true;
        f.Settings.FdmMode = FdmMode.Always;
        f.Settings.FdmPassCookies = true;
        var (_, path) = await f.StartChromiumAsync(url: "https://x.org/big.iso");
        Assert.Null(path);
        await TestLoop.Settle();
        var sent = Assert.Single(f.Fdm.Sent);
        Assert.Equal(new FdmDownloadRequest("https://x.org/big.iso", null, "https://example.com/", "Chromium-UA", "sid=1"), sent);
        Assert.Equal(DownloadState.HandedOff, f.Only.State);
        Assert.Equal(DownloadEngine.Fdm, f.Only.Engine);
        Assert.Equal("integration", f.Manager.LastFdmRoute);
        f.Fdm.Answer!(new FdmOutcome.CancelledByUser());
        Assert.Equal(DownloadState.Cancelled, f.Only.State);
        Assert.Equal("Cancelled in Free Download Manager", f.Only.ErrorMessage);
        // "Download in Mosaic" goes back to the engine it came from.
        f.Manager.Retry(f.Only.Id);
        Assert.Equal(["https://x.org/big.iso"], f.Chromium.Started);
    });

    [Fact]
    public void AsksBeforeHandingOffInAskMode() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Fdm.IsInstalled = true;
        f.Settings.FdmMode = FdmMode.Ask;
        var start = f.StartChromiumAsync(url: "https://x.org/big.iso");
        await TestLoop.Settle();
        Assert.Equal("fdm", f.Manager.Prompter.Pending?.Kind);
        f.Manager.Prompter.Answer(1);
        Assert.Equal(Path.Combine(f.Folder, "big.iso"), (await start).Path);
        Assert.Empty(f.Fdm.Sent);
    });

    [Fact]
    public void RemovesTrashesAndClears() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        var (a, pathA) = await f.StartChromiumAsync(url: "https://x.org/a.txt");
        f.Complete(a);
        var (b, _) = await f.StartChromiumAsync(url: "https://x.org/b.txt");
        f.Complete(b);
        await f.StartChromiumAsync(url: "https://x.org/c.txt");
        Assert.Equal(3, f.Manager.Items.Count);

        f.Manager.Trash(f.Manager.Items.Single(r => r.FileName == "a.txt").Id);
        Assert.False(File.Exists(pathA));
        Assert.Contains("recycle:" + pathA, f.Shell.Requests);
        f.Manager.ClearFinished();
        Assert.Equal(["c.txt"], f.Manager.Items.Select(r => r.FileName));
    });

    [Fact]
    public void ClearsHistorySinceATime() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        var (old, _) = await f.StartChromiumAsync(url: "https://x.org/old.txt");
        f.Complete(old);
        await f.Time.AdvanceAsync(TimeSpan.FromHours(2));
        var cutoff = f.Time.GetUtcNow();
        var (recent, _) = await f.StartChromiumAsync(url: "https://x.org/new.txt");
        f.Complete(recent);
        f.Manager.ClearHistory(cutoff);
        Assert.Equal(["old.txt"], f.Manager.Items.Select(r => r.FileName));
        f.Manager.ClearHistory(null);
        Assert.Empty(f.Manager.Items);
    });

    [Fact]
    public void SearchesTheList() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        await f.StartChromiumAsync(url: "https://x.org/Quarterly.pdf");
        await f.StartChromiumAsync(url: "https://y.org/photo.png");
        f.Manager.SearchText = "x.org";
        Assert.Equal(["Quarterly.pdf"], f.Manager.FilteredItems.Select(r => r.FileName));
    });

    [Fact]
    public void LoadingMergesWithDownloadsStartedMeanwhile() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        var store = new DownloadHistoryStore(f.Manager.HistoryPath);
        store.Upsert(new DownloadRecord { Url = "https://x/old.zip", FileName = "old.zip", State = DownloadState.Completed, StartedAt = DateTimeOffset.UnixEpoch });
        store.Save();
        await f.StartChromiumAsync(url: "https://x.org/new.zip");
        await f.Manager.LoadAsync();
        Assert.Equal(["new.zip", "old.zip"], f.Manager.Items.Select(r => r.FileName));
    });
}

public class DownloadManagerGeckoTests
{
    [Fact]
    public void ConfiguresGeckoWhenItStarts() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Gecko.Handlers["configureDownloads"] = _ => new JsonObject { ["watching"] = true };
        await f.Gecko.StartAsync();
        await TestLoop.Settle();
        var options = Assert.IsType<JsonObject>(f.Gecko.CallsTo("configureDownloads").Single()[0]);
        Assert.Equal(f.Folder, options["directory"]!.GetValue<string>());
        Assert.False(options["fdmMenu"]!.GetValue<bool>());
        Assert.True(f.Manager.GeckoWatching);
        Assert.NotEmpty(f.Gecko.CallsTo("downloadsState"));
    });

    [Fact]
    public void AdoptsADownloadAGeckoPageStarted() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        await f.Gecko.StartAsync();
        var path = Path.Combine(f.Folder, "a.zip");
        f.Manager.ReconcileGecko(DownloadsFixture.GeckoSnapshot(1, DownloadsFixture.GeckoItem(3, "https://x.org/a.zip", path)));
        await TestLoop.Settle();
        var record = f.Only;
        Assert.Equal(DownloadEngine.Gecko, record.Engine);
        Assert.Equal(10, record.ReceivedBytes);
        Assert.Equal(1000, record.Speed);
        Assert.Equal("https://example.com/", record.Referrer);

        f.Manager.Pause(record.Id);
        Assert.Equal([3, "pause"], f.Gecko.CallsTo("downloadAction").Last());

        File.WriteAllText(path, "zip");
        f.Manager.ReconcileGecko(DownloadsFixture.GeckoSnapshot(2, DownloadsFixture.GeckoItem(3, "https://x.org/a.zip", path, "completed", 100)));
        Assert.Equal(DownloadState.Completed, f.Only.State);
        Assert.Equal(3, f.Only.TotalBytes);
        // Mosaic keeps the history; Gecko's own list is cleared.
        Assert.Equal([3, "remove"], f.Gecko.CallsTo("downloadAction").Last());
        Assert.Equal(["Downloaded “a.zip”"], f.Notices);
    });

    [Fact]
    public void MovesCompletedGeckoFilesIntoTheirFolder() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Settings.Rules = new DownloadOrganizationRules(true);
        await f.Gecko.StartAsync();
        var path = Path.Combine(f.Folder, "song.mp3");
        File.WriteAllText(path, "mp3");
        f.Manager.ReconcileGecko(DownloadsFixture.GeckoSnapshot(1, DownloadsFixture.GeckoItem(4, "https://x.org/song.mp3", path, "completed", 3, 3)));
        await TestLoop.Settle();
        Assert.Equal(Path.Combine(f.Folder, "Music", "song.mp3"), f.Only.DestinationPath);
        Assert.True(File.Exists(Path.Combine(f.Folder, "Music", "song.mp3")));
    });

    [Fact]
    public void StartsDownloadsInGecko() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Gecko.Handlers["startDownload"] = _ => JsonValue.Create(9);
        f.Manager.StartInGecko("https://x.org/files/b.bin");
        await TestLoop.Settle();
        Assert.Equal(["https://x.org/files/b.bin", Path.Combine(f.Folder, "b.bin")], f.Gecko.CallsTo("startDownload").Single());
        var record = f.Only;
        // The snapshot that reports it does not create a second entry.
        f.Manager.ReconcileGecko(DownloadsFixture.GeckoSnapshot(1, DownloadsFixture.GeckoItem(9, "https://x.org/files/b.bin", record.DestinationPath!, "failed")));
        Assert.Equal(DownloadState.Failed, f.Only.State);
        f.Manager.Retry(record.Id);
        Assert.Equal([9, "retry"], f.Gecko.CallsTo("downloadAction").Last());
    });

    [Fact]
    public void RunsCommandsFromInsideGecko() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Fdm.IsInstalled = true;
        await f.Gecko.StartAsync();
        var snapshot = DownloadsFixture.GeckoSnapshot(1);
        snapshot["commands"] = new JsonArray(
            new JsonObject { ["type"] = "showDownloads" },
            new JsonObject { ["type"] = "sendLinkToFDM", ["url"] = "https://x.org/v.mp4", ["referrer"] = "https://x.org/" });
        f.Manager.ReconcileGecko(snapshot);
        await TestLoop.Settle();
        Assert.Equal(1, f.PageOpens);
        Assert.Equal("https://x.org/v.mp4", Assert.Single(f.Fdm.Sent).Url);
        Assert.Equal(DownloadState.HandedOff, f.Only.State);
    });

    [Fact]
    public void FailsRunningDownloadsWhenGeckoStops() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        await f.Gecko.StartAsync();
        f.Manager.ReconcileGecko(DownloadsFixture.GeckoSnapshot(1, DownloadsFixture.GeckoItem(3, "https://x.org/a.zip", Path.Combine(f.Folder, "a.zip"))));
        await TestLoop.Settle();
        f.Gecko.Crash();
        Assert.Equal(DownloadState.Failed, f.Only.State);
        Assert.Equal("The Mozilla engine stopped", f.Only.ErrorMessage);
    });

    [Fact]
    public void CancellingAQuestionDropsTheGeckoDownload() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Settings.AskWhereToSave = true;
        await f.Gecko.StartAsync();
        f.Manager.ReconcileGecko(DownloadsFixture.GeckoSnapshot(1, DownloadsFixture.GeckoItem(5, "https://x.org/a.zip", Path.Combine(f.Folder, "a.zip"))));
        await TestLoop.Settle();
        Assert.Equal("save", f.Manager.Prompter.Pending?.Kind);
        f.Manager.Prompter.Answer(1);
        await TestLoop.Settle();
        Assert.Empty(f.Manager.Items);
        Assert.Equal([5, "remove"], f.Gecko.CallsTo("downloadAction").Last());
    });
}

public class DownloadsAutomationTests
{
    [Fact]
    public void ChangesSettingsAndReportsState() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        Assert.Equal("""{"ok":true}""", DownloadsAutomation.Handle(f.Manager, "downloadsSetting", "fdmMode=ask")!.Json);
        DownloadsAutomation.Handle(f.Manager, "downloadsSetting", "organize=on");
        Assert.Contains("Unknown", DownloadsAutomation.Handle(f.Manager, "downloadsSetting", "bogus=1")!.Json);
        Assert.Null(DownloadsAutomation.Handle(f.Manager, "navigate", "x"));
        var (download, _) = await f.StartChromiumAsync(url: "https://x.org/tool.msi");
        f.Complete(download);
        DownloadsAutomation.Handle(f.Manager, "downloadsSearch", "tool");

        var state = DownloadsAutomation.State(f.Manager);
        Assert.Equal("ask", state["fdmMode"]!.GetValue<string>());
        Assert.True(state["organize"]!.GetValue<bool>());
        Assert.Equal(1, state["filteredCount"]!.GetValue<int>());
        var item = state["items"]![0]!.AsObject();
        Assert.Equal("completed", item["state"]!.GetValue<string>());
        Assert.Equal("chromium", item["engine"]!.GetValue<string>());
        Assert.Equal("Installer", item["risk"]!.GetValue<string>());
        Assert.Equal("This file is an installer package.", item["warning"]!.GetValue<string>());
        Assert.True(item["fileExists"]!.GetValue<bool>());

        Assert.Contains("No download", DownloadsAutomation.Handle(f.Manager, "downloadKeep", "nope.exe")!.Json);
        DownloadsAutomation.Handle(f.Manager, "downloadKeep", "tool.msi");
        Assert.True(f.Only.WarningAcknowledged);
        DownloadsAutomation.Handle(f.Manager, "downloadReveal", "0");
        Assert.StartsWith("reveal:", f.Manager.LastOpenRequest);
        DownloadsAutomation.Handle(f.Manager, "downloadsPopover", "true");
        Assert.True(f.Manager.IsPopoverShown);
        Assert.False(f.Manager.HasUnseenCompletion);
    });

    [Fact]
    public void AnswersQuestions() => TestLoop.Run(async () =>
    {
        using var f = new DownloadsFixture();
        f.Settings.AskWhereToSave = true;
        Assert.Contains("No download question", DownloadsAutomation.Handle(f.Manager, "downloadPromptAnswer", "0")!.Json);
        var start = f.StartChromiumAsync();
        await TestLoop.Settle();
        var pending = DownloadsAutomation.State(f.Manager)["pendingPrompt"]!.AsObject();
        Assert.Equal("save", pending["kind"]!.GetValue<string>());
        var target = Path.Combine(f.Root, "picked.pdf");
        DownloadsAutomation.Handle(f.Manager, "downloadPromptSave", target);
        Assert.Equal(target, (await start).Path);
    });
}
