using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Tests.Downloads;

/// <summary>
/// Runs only on Windows, against the app built next to the tests, when asked to: on CI for the
/// downloads branch (<c>GITHUB_REF_NAME=win2/downloads</c>) or with <c>MOSAIC_DOWNLOADS_E2E=1</c>.
/// <c>MOSAIC_APP_EXE</c> names the executable when it is not under <c>src\Mosaic.App\bin</c>.
/// </summary>
public sealed class DownloadsEndToEndFactAttribute : FactAttribute
{
    /// <summary>Off until the WinUI app runs (the winui area's first milestone in win-next).</summary>
    private const bool RunOnBranchCI = false;

    public DownloadsEndToEndFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Drives the Windows app.";
        else if (Environment.GetEnvironmentVariable("MOSAIC_DOWNLOADS_E2E") != "1" &&
                 !(RunOnBranchCI && Environment.GetEnvironmentVariable("GITHUB_REF_NAME") == "win2/downloads"))
        {
            Skip = "Set MOSAIC_DOWNLOADS_E2E=1 to drive the app's downloads end to end.";
        }
        else if (DownloadsEndToEndTests.FindApp() is null) Skip = "Mosaic.exe was not found (build the app first).";
    }
}

/// <summary>
/// Mosaic's downloads end to end on Windows: the real app (WinUI, WebView2, Gecko) through the
/// automation pipe, downloading from a local throttled server, checked through <c>dump</c>.
/// </summary>
public sealed class DownloadsEndToEndTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("mosaic-dl-e2e-").FullName;
    private readonly ThrottledServer server = new();
    private Process? app;

    public static string? FindApp()
    {
        if (Environment.GetEnvironmentVariable("MOSAIC_APP_EXE") is { Length: > 0 } named && File.Exists(named)) return named;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var bin = Path.Combine(folder.FullName, "src", "Mosaic.App", "bin");
            if (!Directory.Exists(bin)) continue;
            return Directory.EnumerateFiles(bin, "Mosaic.exe", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Contains("Release", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            if (app is { HasExited: false })
            {
                try { Send("quit").Wait(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
                if (!app.WaitForExit(10_000)) app.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            app?.Dispose();
            server.Dispose();
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<JsonObject> Send(string command, string? value = null)
    {
        using var pipe = new NamedPipeClientStream(".", Automation.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        var request = new JsonObject { ["do"] = command };
        if (value is not null) request["value"] = value;
        var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)) ?? "{}";
        return JsonNode.Parse(line)!.AsObject();
    }

    private async Task Ok(string command, string? value = null)
    {
        var reply = await Send(command, value);
        Assert.True(reply["ok"]?.GetValue<bool>() == true, $"{command} {value}: {reply.ToJsonString()}");
    }

    private async Task<JsonObject> Downloads() => (await Send("dump"))["state"]!["downloads"]!.AsObject();

    private static IEnumerable<JsonObject> Items(JsonObject downloads) => downloads["items"]!.AsArray().OfType<JsonObject>();

    private async Task<JsonObject> Until(Func<JsonObject, bool> condition, string what, double seconds = 60)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var downloads = await Downloads();
            if (condition(downloads)) return downloads;
            Assert.True(clock.Elapsed.TotalSeconds < seconds, $"Timed out waiting for {what}: {downloads.ToJsonString()}");
            await Task.Delay(250);
        }
    }

    private static JsonObject? Item(JsonObject downloads, string fileName) =>
        Items(downloads).FirstOrDefault(item => item["fileName"]?.GetValue<string>() == fileName);

    private static string State(JsonObject downloads, string fileName) => Item(downloads, fileName)?["state"]?.GetValue<string>() ?? "";

    private async Task StartAppAsync()
    {
        var exe = FindApp()!;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.ArgumentList.Add(Automation.Flag);
        start.Environment["MOSAIC_DATA_DIR"] = Path.Combine(root, "data");
        app = Process.Start(start)!;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if ((await Send("dump"))["ok"]?.GetValue<bool>() == true) return;
            }
            catch (Exception error) when (error is TimeoutException or IOException)
            {
            }
            Assert.False(app.HasExited, $"Mosaic exited with code {(app.HasExited ? app.ExitCode : 0)}");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "Mosaic's automation pipe never answered.");
            await Task.Delay(500);
        }
    }

    [DownloadsEndToEndFact]
    public async Task DownloadsInBothEnginesWithMarkOfTheWebWarningsAndQuestions()
    {
        server.Serve("files/report.pdf", ThrottledServer.Pattern(200_000), "application/pdf", "attachment; filename=\"report.pdf\"");
        server.Serve("files/big.bin", ThrottledServer.Pattern(3_000_000), disposition: "attachment; filename=\"big.bin\"", bytesPerSecond: 300_000);
        server.Serve("files/setup.exe", ThrottledServer.Pattern(10_000), "application/x-msdownload", "attachment; filename=\"setup.exe\"");
        server.Serve("files/gecko.zip", ThrottledServer.Pattern(120_000), "application/zip");
        server.Serve("files/helper.txt", ThrottledServer.Pattern(5_000), "text/plain", "attachment; filename=\"helper.txt\"");
        server.Serve("files/chosen.txt", ThrottledServer.Pattern(5_000), "text/plain", "attachment; filename=\"chosen.txt\"");

        await StartAppAsync();
        var folder = Path.Combine(root, "Downloads");
        await Ok("downloadsFolder", folder);
        var state = await Downloads();
        Assert.Equal(folder, state["folder"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(state["knownDownloadsFolder"]?.GetValue<string>()));

        // Chromium: a page link that is an attachment.
        await Ok("engine", "chromium");
        await Ok("navigate", server.Url("files/report.pdf"));
        state = await Until(d => State(d, "report.pdf") == "completed", "report.pdf (Chromium) to complete");
        var report = Item(state, "report.pdf")!;
        Assert.Equal("chromium", report["engine"]!.GetValue<string>());
        Assert.Equal(Path.Combine(folder, "report.pdf"), report["path"]!.GetValue<string>());
        Assert.Equal(3, report["zoneId"]!.GetValue<int>());
        Assert.Contains("HostUrl=" + server.Url("files/report.pdf"), report["markOfTheWeb"]!.GetValue<string>());
        Assert.Equal(200_000, new FileInfo(Path.Combine(folder, "report.pdf")).Length);

        // Duplicate: the same address again asks first.
        await Ok("navigate", server.Url("files/report.pdf"));
        state = await Until(d => d["pendingPrompt"] is JsonObject prompt && prompt["kind"]?.GetValue<string>() == "duplicate", "the duplicate question");
        await Ok("downloadPromptAnswer", "2");
        state = await Until(d => d["pendingPrompt"] is null, "the question to close");
        Assert.Single(Items(state), item => item["fileName"]!.GetValue<string>().StartsWith("report", StringComparison.Ordinal));

        // A slow file: progress, pause, resume, finish.
        await Ok("navigate", server.Url("files/big.bin"));
        state = await Until(d => Item(d, "big.bin")?["receivedBytes"]?.GetValue<long>() > 100_000, "big.bin to make progress");
        Assert.True(state["activeCount"]!.GetValue<int>() >= 1);
        await Ok("downloadPause", "big.bin");
        await Until(d => State(d, "big.bin") == "paused", "big.bin to pause");
        await Ok("downloadResume", "big.bin");
        await Until(d => State(d, "big.bin") == "completed", "big.bin to complete", 90);
        Assert.Equal(3_000_000, new FileInfo(Path.Combine(folder, "big.bin")).Length);

        // A program: kept, but flagged until acknowledged.
        await Ok("navigate", server.Url("files/setup.exe"));
        state = await Until(d => State(d, "setup.exe") == "completed", "setup.exe to complete");
        var setup = Item(state, "setup.exe")!;
        Assert.Equal("Application", setup["risk"]!.GetValue<string>());
        Assert.Equal("This file is a program.", setup["warning"]!.GetValue<string>());
        await Ok("downloadKeep", "setup.exe");
        Assert.Equal("", Item(await Downloads(), "setup.exe")!["warning"]!.GetValue<string>());

        // "download <url>": Chromium's hidden helper fetches it.
        await Ok("download", server.Url("files/helper.txt"));
        state = await Until(d => State(d, "helper.txt") == "completed", "helper.txt to complete");
        Assert.Equal("chromium", Item(state, "helper.txt")!["engine"]!.GetValue<string>());

        // Ask where to save, answered by script.
        await Ok("downloadsSetting", "askWhereToSave=on");
        await Ok("navigate", server.Url("files/chosen.txt"));
        await Until(d => d["pendingPrompt"] is JsonObject prompt && prompt["kind"]?.GetValue<string>() == "save", "the save question");
        var chosen = Path.Combine(root, "Elsewhere", "picked.txt");
        await Ok("downloadPromptSave", chosen);
        await Until(d => State(d, "picked.txt") == "completed", "picked.txt to complete");
        Assert.True(File.Exists(chosen));
        await Ok("downloadsSetting", "askWhereToSave=off");

        // Gecko: a download Mosaic starts in the Mozilla engine.
        await Ok("downloadGecko", server.Url("files/gecko.zip"));
        state = await Until(d => State(d, "gecko.zip") == "completed", "gecko.zip (Gecko) to complete", 120);
        var zip = Item(state, "gecko.zip")!;
        Assert.Equal("gecko", zip["engine"]!.GetValue<string>());
        Assert.Equal(3, zip["zoneId"]!.GetValue<int>());
        Assert.True(state["geckoWatching"]!.GetValue<bool>());

        // Search, the flyout, the page, and the views rendered to PNGs.
        await Ok("downloadsSearch", "report");
        Assert.Equal(1, (await Downloads())["filteredCount"]!.GetValue<int>());
        await Ok("downloadsSearch", "");
        await Ok("downloadsPopover", "true");
        Assert.True((await Downloads())["popoverShown"]!.GetValue<bool>());
        await Ok("downloadsPopover", "false");
        var snapshots = Path.Combine(root, "shots", "downloads");
        await Ok("downloadsSnapshot", snapshots);
        foreach (var suffix in new[] { "popover", "page", "settings", "button" })
        {
            Assert.True(new FileInfo($"{snapshots}-{suffix}.png").Length > 1000, $"{suffix} snapshot");
        }

        // The history file is written in the macOS format.
        var historyFile = state["historyFile"]!.GetValue<string>();
        var history = JsonNode.Parse(File.ReadAllText(historyFile))!.AsObject();
        Assert.Equal(1, history["version"]!.GetValue<int>());
        Assert.True(history["downloads"]!.AsArray().Count >= 6);

        // Clearing keeps the files.
        await Ok("downloadsClear");
        Assert.Empty(Items(await Downloads()));
        Assert.True(File.Exists(Path.Combine(folder, "report.pdf")));
    }
}
