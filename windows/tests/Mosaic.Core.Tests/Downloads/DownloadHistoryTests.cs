using System.Text.Json.Nodes;
using Mosaic.Core.Downloads;

namespace Mosaic.Core.Tests.Downloads;

public sealed class DownloadHistoryStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mosaic-dl-tests-{Guid.NewGuid():N}");

    private string File => Path.Combine(directory, "history.json");

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private static DownloadRecord Record(
        string name, string url, DownloadState state = DownloadState.Completed, DateTimeOffset? at = null, string? path = null) => new()
        {
            Url = url, FileName = name, DestinationPath = path ?? $"/tmp/{name}", Engine = DownloadEngine.Chromium,
            TotalBytes = 10, ReceivedBytes = state == DownloadState.Completed ? 10 : 3, State = state,
            StartedAt = at ?? DateTimeOffset.UtcNow,
        };

    [Fact]
    public void PersistsAndReloads()
    {
        var store = new DownloadHistoryStore(File);
        store.Upsert(Record("a.pdf", "https://example.com/a.pdf", at: DateTimeOffset.FromUnixTimeSeconds(100)) with { Speed = 999 });
        store.Upsert(Record("b.zip", "https://example.org/b.zip", at: DateTimeOffset.FromUnixTimeSeconds(200)));
        store.Save();

        var reloaded = new DownloadHistoryStore(File);
        reloaded.Load();
        Assert.Equal(["b.zip", "a.pdf"], reloaded.Records.Select(r => r.FileName));
        Assert.Equal(0, reloaded.Records[1].Speed); // live value: not persisted
        Assert.Equal("https://example.com/a.pdf", reloaded.Records[1].Url);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(100), reloaded.Records[1].StartedAt);
    }

    [Fact]
    public void WritesTheMacOSFormat()
    {
        var store = new DownloadHistoryStore(File);
        var id = Guid.Parse("e621e1f8-c36c-495a-93fc-0c247a3e6e5f");
        store.Upsert(new DownloadRecord
        {
            Id = id, Url = "https://x/a.zip", FileName = "a.zip", Engine = DownloadEngine.Gecko,
            State = DownloadState.Completed, StartedAt = new DateTimeOffset(2026, 5, 1, 10, 30, 15, 123, TimeSpan.FromHours(2)),
        });
        var json = JsonNode.Parse(store.Serialize())!.AsObject();
        Assert.Equal(1, json["version"]!.GetValue<int>());
        var entry = json["downloads"]![0]!.AsObject();
        Assert.Equal("E621E1F8-C36C-495A-93FC-0C247A3E6E5F", entry["id"]!.GetValue<string>());
        Assert.Equal("2026-05-01T08:30:15Z", entry["startedAt"]!.GetValue<string>());
        Assert.Equal("gecko", entry["engine"]!.GetValue<string>());
        Assert.Equal("completed", entry["state"]!.GetValue<string>());
        Assert.False(entry.ContainsKey("referrer")); // nil values are left out, as Swift does
        Assert.False(entry.ContainsKey("speed"));
        // Keys sorted, as the macOS encoder writes them.
        var keys = entry.Select(pair => pair.Key).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    [Fact]
    public void ReadsAFileWrittenOnMacOS()
    {
        Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(File, """
            {"downloads":[{"destinationPath":"\/Users\/a\/Downloads\/x.dmg","endedAt":"2026-03-02T09:00:05Z","engine":"chromium",
            "fileName":"x.dmg","id":"0F8FAD5B-D9CB-469F-A165-70867728950E","originalURL":"https:\/\/x.org\/get","receivedBytes":100,
            "startedAt":"2026-03-02T09:00:00Z","state":"completed","totalBytes":100,"url":"https:\/\/cdn.x.org\/x.dmg","warningAcknowledged":true}],
            "version":1}
            """);
        var store = new DownloadHistoryStore(File);
        store.Load();
        var record = Assert.Single(store.Records);
        Assert.Equal("x.dmg", record.FileName);
        Assert.Equal("https://x.org/get", record.OriginalUrl);
        Assert.Equal(DownloadEngine.Chromium, record.Engine);
        Assert.True(record.WarningAcknowledged);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 9, 0, 5, TimeSpan.Zero), record.EndedAt);
    }

    [Fact]
    public void MarksRunningDownloadsInterruptedOnLoad()
    {
        var store = new DownloadHistoryStore(File);
        store.Upsert(Record("big.iso", "https://x/big.iso", DownloadState.InProgress));
        store.Upsert(Record("p.bin", "https://x/p.bin", DownloadState.Paused));
        store.Save();
        var reloaded = new DownloadHistoryStore(File);
        reloaded.Load();
        Assert.All(reloaded.Records, r => Assert.Equal(DownloadState.Failed, r.State));
        Assert.All(reloaded.Records, r => Assert.Equal(DownloadHistoryStore.InterruptedMessage, r.ErrorMessage));
    }

    [Fact]
    public void SurvivesACorruptFile()
    {
        Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(File, "not json");
        var store = new DownloadHistoryStore(File);
        store.Load();
        Assert.Empty(store.Records);
        Assert.True(System.IO.File.Exists(File + ".corrupt"));
    }

    [Fact]
    public void UpsertReplacesInPlace()
    {
        var store = new DownloadHistoryStore(File);
        var a = Record("a", "https://x/a", DownloadState.InProgress);
        store.Upsert(a);
        store.Upsert(Record("b", "https://x/b"));
        store.Upsert(a with { State = DownloadState.Completed });
        Assert.Equal(2, store.Records.Count);
        Assert.Equal(DownloadState.Completed, store.Record(a.Id)!.State);
        Assert.Equal(a.Id, store.Records[^1].Id);
    }

    [Fact]
    public void SearchesNameUrlAndHost()
    {
        var store = new DownloadHistoryStore(File);
        store.Upsert(Record("Quarterly Report.pdf", "https://files.example.com/q3.pdf"));
        store.Upsert(Record("café.zip", "https://cdn.other.org/cafe.zip"));
        Assert.Equal(["Quarterly Report.pdf"], store.Search("report").Select(r => r.FileName));
        Assert.Single(store.Search("EXAMPLE.com"));
        Assert.Equal(["café.zip"], store.Search("cafe").Select(r => r.FileName));
        Assert.Single(store.Search("quarterly pdf"));
        Assert.Empty(store.Search("quarterly zip"));
        Assert.Equal(2, store.Search("  ").Count);
    }

    [Fact]
    public void ClearKeepsActiveDownloads()
    {
        var store = new DownloadHistoryStore(File);
        store.Upsert(Record("done", "https://x/1"));
        store.Upsert(Record("failed", "https://x/2", DownloadState.Failed));
        store.Upsert(Record("running", "https://x/3", DownloadState.InProgress));
        store.ClearFinished();
        Assert.Equal(["running"], store.Records.Select(r => r.FileName));
    }

    [Fact]
    public void LimitDropsOldestFinished()
    {
        var store = new DownloadHistoryStore(File, limit: 2);
        store.Upsert(Record("old-running", "https://x/0", DownloadState.InProgress, DateTimeOffset.FromUnixTimeSeconds(1)));
        store.Upsert(Record("mid", "https://x/1", at: DateTimeOffset.FromUnixTimeSeconds(2)));
        store.Upsert(Record("new", "https://x/2", at: DateTimeOffset.FromUnixTimeSeconds(3)));
        Assert.Equal(["new", "old-running"], store.Records.Select(r => r.FileName));
    }

    [Fact]
    public void FindsDuplicatesOnlyWhenTheFileStillExists()
    {
        var store = new DownloadHistoryStore(File);
        var existing = Record("a.zip", "https://x.org/a.zip#top", path: "/files/a.zip");
        store.Upsert(existing);
        store.Upsert(Record("b.zip", "https://x.org/b.zip", DownloadState.Failed));
        Assert.Equal(existing.Id, store.CompletedDownload("https://x.org/a.zip", fileExists: p => p == "/files/a.zip")?.Id);
        Assert.Null(store.CompletedDownload("https://x.org/a.zip", fileExists: _ => false));
        Assert.Null(store.CompletedDownload("https://x.org/b.zip", fileExists: _ => true));
        Assert.Null(store.CompletedDownload("https://x.org/a.zip", existing.Id, _ => true));
    }
}

public sealed class DownloadSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mosaic-dl-settings-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void DefaultsMatchMacOS()
    {
        var settings = new DownloadSettings(null);
        Assert.Equal(DownloadSettings.DefaultFolder, settings.FolderPath);
        Assert.False(settings.AskWhereToSave);
        Assert.False(settings.OpenSafeFiles);
        Assert.True(settings.NotifyWhenFinished);
        Assert.False(settings.Rules.IsEnabled);
        Assert.Equal(FdmMode.Off, settings.FdmMode);
        Assert.True(settings.FdmContextMenu);
        Assert.False(settings.FdmPassCookies);
    }

    [Fact]
    public void SavesAndReloads()
    {
        var file = Path.Combine(directory, "settings.json");
        var settings = new DownloadSettings(file)
        {
            FolderPath = Path.Combine(directory, "Saved"),
            AskWhereToSave = true,
            Rules = new DownloadOrganizationRules(true).WithFolder(DownloadFileCategory.Images, "Pics"),
            FdmMode = FdmMode.Ask,
            FdmMinimumMB = 50,
            FdmPassCookies = true,
            FdmContextMenu = false,
        };
        settings.Flush();
        var reloaded = new DownloadSettings(file);
        Assert.Equal(Path.Combine(directory, "Saved"), reloaded.FolderPath);
        Assert.True(reloaded.AskWhereToSave);
        Assert.Equal("Pics", reloaded.Rules.Subfolder("a.png"));
        Assert.Equal(FdmMode.Ask, reloaded.FdmMode);
        Assert.Equal(new FdmPolicy(FdmMode.Ask, 50_000_000), reloaded.FdmPolicy);
        Assert.True(reloaded.FdmPassCookies);
        Assert.False(reloaded.FdmContextMenu);
    }

    [Fact]
    public void AnnouncesWhatTheEnginesNeed()
    {
        var settings = new DownloadSettings(null);
        var changes = 0;
        settings.EngineSettingsChanged += () => changes++;
        settings.AskWhereToSave = true;
        Assert.Equal(0, changes);
        settings.FolderPath = "/elsewhere";
        settings.FdmContextMenu = false;
        settings.FdmMode = FdmMode.Always;
        Assert.Equal(3, changes);
    }
}
