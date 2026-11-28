using System.IO.Pipes;
using System.Text.Json.Nodes;
using Mosaic.Core.Downloads;

namespace Mosaic.Core.Tests.Downloads;

public class FreeDownloadManagerProtocolTests
{
    [Fact]
    public void PolicyHonoursModeSchemeAndSize()
    {
        const string url = "https://example.com/big.iso";
        Assert.Equal(FdmDecision.UseEngine, new FdmPolicy(FdmMode.Off).Decide(url, 1, true));
        Assert.Equal(FdmDecision.UseEngine, new FdmPolicy(FdmMode.Always).Decide(url, 1, false));
        Assert.Equal(FdmDecision.SendToFdm, new FdmPolicy(FdmMode.Always).Decide(url, 1, true));
        Assert.Equal(FdmDecision.AskUser, new FdmPolicy(FdmMode.Ask).Decide(url, null, true));
        Assert.Equal(FdmDecision.UseEngine, new FdmPolicy(FdmMode.Always).Decide("blob:https://x/123", 1, true));
        Assert.Equal(FdmDecision.UseEngine, new FdmPolicy(FdmMode.Always).Decide("data:text/plain,hi", 1, true));
        var threshold = new FdmPolicy(FdmMode.Always, 10_000_000);
        Assert.Equal(FdmDecision.UseEngine, threshold.Decide(url, 5_000, true));
        Assert.Equal(FdmDecision.SendToFdm, threshold.Decide(url, 50_000_000, true));
        // Unknown size counts as large.
        Assert.Equal(FdmDecision.SendToFdm, threshold.Decide(url, -1, true));
        Assert.Equal(FdmDecision.SendToFdm, threshold.Decide(url, null, true));
    }

    [Fact]
    public void FramesAndUnframesMessages()
    {
        var data = FdmNativeMessage.Frame(FdmNativeMessage.Handshake("1"));
        Assert.Equal(data.Length - 4, BitConverter.ToInt32(data, 0));
        var buffer = new List<byte>(data);
        buffer.AddRange(FdmNativeMessage.Frame(new JsonObject { ["id"] = "2", ["type"] = "x" }));
        buffer.AddRange(new byte[] { 9, 0 }); // start of an incomplete frame
        var messages = FdmNativeMessage.TakeMessages(buffer);
        Assert.Equal(2, messages.Count);
        Assert.Equal("handshake", messages[0]["type"]!.GetValue<string>());
        Assert.Equal("2", messages[1]["id"]!.GetValue<string>());
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void CreateDownloadsCarriesOnlyWhatWasAllowed()
    {
        var message = FdmNativeMessage.CreateDownloads("7", [new FdmDownloadRequest("https://x/a.zip", Referrer: "https://x/page", UserAgent: "UA")]);
        var entry = message["create_downloads"]!["downloads"]![0]!.AsObject();
        Assert.Equal("create_downloads", message["type"]!.GetValue<string>());
        Assert.Equal("https://x/a.zip", entry["url"]!.GetValue<string>());
        Assert.Equal("https://x/a.zip", entry["originalUrl"]!.GetValue<string>());
        Assert.Equal("https://x/page", entry["httpReferer"]!.GetValue<string>());
        Assert.Equal("UA", entry["userAgent"]!.GetValue<string>());
        Assert.False(entry.ContainsKey("httpCookies"));
        Assert.Equal("1", message["create_downloads"]!["waitResponse"]!.GetValue<string>());
    }

    [Fact]
    public void InterpretsResponses()
    {
        Assert.IsType<FdmOutcome.Accepted>(FdmNativeMessage.Outcome(new JsonObject { ["id"] = "2", ["error"] = "", ["result"] = "" }));
        Assert.IsType<FdmOutcome.CancelledByUser>(FdmNativeMessage.Outcome(new JsonObject { ["id"] = "2", ["error"] = "", ["result"] = "0" }));
        Assert.Equal(new FdmOutcome.Failed("HTTP Error 404"), FdmNativeMessage.Outcome(new JsonObject { ["id"] = "2", ["error"] = "HTTP Error 404" }));
    }

    [Fact]
    public void FindsTheHelperFromItsManifest()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fdm");
        var manifest = Path.Combine(folder, "manifest.json");
        Assert.Equal(Path.Combine(folder, "wenativehost.exe"),
            FreeDownloadManager.HostFromManifest(manifest, """{"name":"org.freedownloadmanager.fdm5.cnh","path":"wenativehost.exe"}"""));
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "wenativehost.exe");
        Assert.Equal(absolute, FreeDownloadManager.HostFromManifest(manifest, new JsonObject { ["path"] = absolute }.ToJsonString()));
        Assert.Null(FreeDownloadManager.HostFromManifest(manifest, "{}"));
        Assert.Null(FreeDownloadManager.HostFromManifest(manifest, "nope"));
    }

    [Fact]
    public void ResolvesTheInstallFolder()
    {
        var present = new HashSet<string>
        {
            Path.Combine("B", "fdm.exe"), Path.Combine("B", "wenativehost.exe"), Path.Combine("C", "fdm.exe"),
        };
        var found = FreeDownloadManager.Resolve(["A", "B", "C"], null, null, present.Contains, _ => "6.30");
        Assert.Equal(new FdmInstallation(Path.Combine("B", "fdm.exe"), Path.Combine("B", "wenativehost.exe"), "6.30"), found);
        var noHost = FreeDownloadManager.Resolve(["C"], null, "6.36", present.Contains, _ => null);
        Assert.Equal(new FdmInstallation(Path.Combine("C", "fdm.exe"), null, "6.36"), noHost);
        Assert.Null(FreeDownloadManager.Resolve(["A"], null, null, present.Contains, _ => null));
    }
}

/// <summary>A stand-in for <c>wenativehost.exe</c>: answers over pipes like the real helper.</summary>
internal sealed class FakeFdmHost : IFdmHostProcess
{
    private readonly AnonymousPipeServerStream toHost = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream hostReads;
    private readonly AnonymousPipeServerStream fromHost = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream mosaicReads;
    private readonly Task serving;

    public FakeFdmHost(Func<JsonObject, IEnumerable<JsonObject>> respond)
    {
        hostReads = new AnonymousPipeClientStream(PipeDirection.In, toHost.ClientSafePipeHandle);
        mosaicReads = new AnonymousPipeClientStream(PipeDirection.In, fromHost.ClientSafePipeHandle);
        serving = Task.Run(async () =>
        {
            var buffer = new List<byte>();
            var chunk = new byte[4096];
            try
            {
                while (true)
                {
                    var count = await hostReads.ReadAsync(chunk);
                    if (count <= 0) break;
                    buffer.AddRange(chunk.AsSpan(0, count).ToArray());
                    foreach (var message in FdmNativeMessage.TakeMessages(buffer))
                    {
                        Received.Add(message);
                        foreach (var reply in respond(message))
                        {
                            await fromHost.WriteAsync(FdmNativeMessage.Frame(reply));
                            await fromHost.FlushAsync();
                        }
                    }
                }
            }
            catch (IOException) { }
        });
    }

    public System.Collections.Concurrent.ConcurrentBag<JsonObject> Received { get; } = [];
    public bool Disposed { get; private set; }

    public Stream Input => toHost;
    public Stream Output => mosaicReads;

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        await toHost.DisposeAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        await fromHost.DisposeAsync();
        await hostReads.DisposeAsync();
        await mosaicReads.DisposeAsync();
    }
}

public class FreeDownloadManagerHandOffTests
{
    private static readonly FdmInstallation Installed = new(@"C:\FDM\fdm.exe", @"C:\FDM\wenativehost.exe", "6.36");

    private static IEnumerable<JsonObject> Reply(JsonObject message, string? result = "", string error = "")
    {
        var id = message["id"]!.GetValue<string>();
        if (message["type"]!.GetValue<string>() == "handshake")
        {
            // The helper also asks things of the browser; Mosaic declines them.
            yield return new JsonObject { ["id"] = "q1", ["type"] = "query_settings", ["isResponse"] = false };
            yield return new JsonObject { ["id"] = id, ["type"] = "handshake", ["error"] = "" };
        }
        else if (message["type"]!.GetValue<string>() == "create_downloads")
        {
            yield return new JsonObject { ["id"] = id, ["type"] = "create_downloads", ["error"] = error, ["result"] = result };
        }
    }

    [Fact]
    public async Task HandsOverThroughTheHelperAndReportsTheAnswer()
    {
        FakeFdmHost? host = null;
        var outcome = new TaskCompletionSource<FdmOutcome>();
        var fdm = new FreeDownloadManager(() => Installed, _ => host = new FakeFdmHost(m => Reply(m, result: "1")), (_, _) => false);
        var route = await fdm.SendAsync(new FdmDownloadRequest("https://x/a.zip", Referrer: "https://x/", UserAgent: "UA"),
            bringToFront: false, o => outcome.TrySetResult(o));
        Assert.Equal(FdmRoute.Integration, route);
        Assert.IsType<FdmOutcome.Accepted>(await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var create = host!.Received.Single(m => m["type"]!.GetValue<string>() == "create_downloads");
        Assert.Equal("https://x/", create["create_downloads"]!["downloads"]![0]!["httpReferer"]!.GetValue<string>());
        Assert.Contains(host.Received, m => m["type"]!.GetValue<string>() == "query_settings" && m["error"]?.GetValue<string>() == "not_implemented");
    }

    [Fact]
    public async Task ReportsCancellationInFdm()
    {
        var outcome = new TaskCompletionSource<FdmOutcome>();
        var fdm = new FreeDownloadManager(() => Installed, _ => new FakeFdmHost(m => Reply(m, result: "0")), (_, _) => false);
        await fdm.SendAsync(new FdmDownloadRequest("https://x/a.zip"), false, o => outcome.TrySetResult(o));
        Assert.IsType<FdmOutcome.CancelledByUser>(await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task FallsBackToTheProgramWhenTheHelperIsSilent()
    {
        string? opened = null;
        var fdm = new FreeDownloadManager(() => Installed, _ => new FakeFdmHost(_ => []), (_, url) => { opened = url; return true; })
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(200),
        };
        var route = await fdm.SendAsync(new FdmDownloadRequest("https://x/a.zip"), false, _ => { });
        Assert.Equal(FdmRoute.OpenUrl, route);
        Assert.Equal("https://x/a.zip", opened);
    }

    [Fact]
    public async Task FailsWhenNotInstalled()
    {
        var fdm = new FreeDownloadManager(() => null);
        Assert.False(fdm.IsInstalled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fdm.SendAsync(new FdmDownloadRequest("https://x/a"), false, _ => { }));
    }
}
