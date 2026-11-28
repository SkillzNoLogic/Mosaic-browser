using System.Diagnostics;
using Mosaic.Core.Downloads;
using Mosaic.Core.Gecko;
using Mosaic.Core.Tests.Gecko;

namespace Mosaic.Core.Tests.Downloads;

/// <summary>
/// The download manager against a real Gecko (the Firefox named by <c>MOSAIC_GECKO_EXE</c>, run
/// headless on a throwaway profile, as the Gecko integration tests do) and the shared agent's
/// downloads methods, fetching from a local throttled server: start, progress, pause, resume
/// with a byte range, completion, the Mark of the Web, and a download a page started itself.
/// </summary>
public sealed class GeckoDownloadIntegrationTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();
    private readonly ThrottledServer server = new();
    private GeckoEngine? engine;

    static GeckoDownloadIntegrationTests() => Environment.SetEnvironmentVariable("MOZ_APP_NO_DOCK", "1");

    public void Dispose()
    {
        engine?.StopAndWait(TimeSpan.FromSeconds(5));
        server.Dispose();
        directory.Dispose();
    }

    private static string AgentDirectory
    {
        get
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, "Resources", "gecko");
                if (File.Exists(Path.Combine(candidate, "agent.sys.mjs"))) return candidate;
            }
            throw new DirectoryNotFoundException("Resources/gecko/agent.sys.mjs was not found above the test folder.");
        }
    }

    private (DownloadManager Manager, string Folder) Create()
    {
        engine = new GeckoEngine(new GeckoEngineOptions
        {
            ProfileDirectory = directory.File("profile"),
            AgentDirectory = AgentDirectory,
            Runtime = GeckoRuntime.Locate()!,
            ExtraArguments = ["--headless"],
            PollInterval = TimeSpan.FromMilliseconds(100),
        });
        var folder = directory.File("Downloads");
        var settings = new DownloadSettings(null) { FolderPath = folder };
        var manager = new DownloadManager(engine, directory.File("history.json"), settings);
        return (manager, folder);
    }

    private static async Task Until(Func<bool> condition, string what, DownloadManager manager, double seconds = 60)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed.TotalSeconds < seconds,
                $"Timed out waiting for {what}: " + DownloadsAutomation.State(manager)["items"]!.ToJsonString());
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a thread of its own standing in for the UI thread (the
    /// manager and the engine are single-threaded), without TestLoop's stall timeout: Gecko can
    /// take a while to start on a CI runner.
    /// </summary>
    private static async Task OnUiThread(Func<Task> body)
    {
        using var ui = new SingleThreadContext();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(async _ =>
        {
            try
            {
                await body();
                done.SetResult();
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        }, null);
        await done.Task;
    }

    [GeckoFact]
    public Task DownloadsPausesResumesAndAdopts() => OnUiThread(async () =>
    {
        var small = ThrottledServer.Pattern(100_000);
        var large = ThrottledServer.Pattern(1_500_000);
        server.Serve("files/small.txt", small, "text/plain");
        server.Serve("files/large.bin", large, bytesPerSecond: 150_000);
        server.Serve("page/attachment", ThrottledServer.Pattern(50_000), disposition: "attachment; filename=\"report.zip\"");

        var (manager, folder) = Create();
        await manager.LoadAsync();
        await engine!.StartAsync();
        await Until(() => manager.GeckoWatching, "the agent to watch downloads", manager, 30);

        // A download Mosaic starts in Gecko.
        manager.StartInGecko(server.Url("files/small.txt"));
        await Until(() => manager.Items.FirstOrDefault()?.State == DownloadState.Completed, "small.txt to complete", manager);
        var smallRecord = manager.Items[0];
        Assert.Equal(Path.Combine(folder, "small.txt"), smallRecord.DestinationPath);
        Assert.Equal(small, File.ReadAllBytes(smallRecord.DestinationPath!));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(3, MarkOfTheWeb.ZoneOf(MarkOfTheWeb.Read(smallRecord.DestinationPath!)));
        }

        // A slow one: pause half way, resume, and it continues with a byte range.
        manager.StartInGecko(server.Url("files/large.bin"));
        await Until(() => manager.Items[0].FileName == "large.bin" && manager.Items[0].ReceivedBytes > 100_000,
            "large.bin to make progress", manager);
        var id = manager.Items[0].Id;
        manager.Pause(id);
        await Until(() => manager.Record(id)!.State == DownloadState.Paused, "large.bin to pause", manager);
        var pausedAt = manager.Record(id)!.ReceivedBytes;
        Assert.True(pausedAt < large.Length);
        manager.Resume(id);
        await Until(() => manager.Record(id)!.State == DownloadState.Completed, "large.bin to complete", manager, 90);
        Assert.Equal(large, File.ReadAllBytes(manager.Record(id)!.DestinationPath!));
        Assert.Contains(server.Requests, request => request.StartsWith("GET /files/large.bin range=bytes=", StringComparison.Ordinal));

        // A download a page starts (Content-Disposition: attachment) is adopted and named by the server.
        await engine.CallAsync("openTab", server.Url("page/attachment"), true);
        await Until(() => manager.Items.Any(r => r.FileName == "report.zip" && r.State == DownloadState.Completed),
            "the page's download to be adopted and complete", manager);
        var adopted = manager.Items.First(r => r.FileName == "report.zip");
        Assert.Equal(Path.Combine(folder, "report.zip"), adopted.DestinationPath);
        Assert.Equal(DownloadEngine.Gecko, adopted.Engine);

        manager.Flush();
        var history = DownloadHistoryStore.Read(manager.HistoryPath);
        Assert.Equal(3, history.Count);
    });
}
