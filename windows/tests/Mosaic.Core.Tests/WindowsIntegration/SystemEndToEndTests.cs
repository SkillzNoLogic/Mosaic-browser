using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Mosaic.Core.Tests.WindowsIntegration;

/// <summary>Runs only when <c>MOSAIC_APP_EXE</c> points at a published Mosaic.exe (Windows).</summary>
public sealed class AppFactAttribute : FactAttribute
{
    public AppFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("MOSAIC_APP_EXE") is not { Length: > 0 } exe || !File.Exists(exe))
            Skip = "Set MOSAIC_APP_EXE to a published Mosaic.exe (Windows) to run the system area's end-to-end checks.";
    }
}

/// <summary>
/// The system area in the running app, through the automation pipe:
/// <c>$env:MOSAIC_APP_EXE = 'windows\publish\win-x64\Mosaic.exe'; dotnet test windows/tests/Mosaic.Core.Tests --filter SystemEndToEnd</c>.
/// Uses an isolated data folder and a throwaway default-browser registration name, and cleans up.
/// </summary>
public sealed class SystemEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly string data = Path.Combine(Path.GetTempPath(), "mosaic-system-e2e-" + Guid.NewGuid().ToString("N")[..8]);
    private Process? app;

    public void Dispose()
    {
        try
        {
            if (app is { HasExited: false })
            {
                try { Send("{\"do\":\"quit\"}", TimeSpan.FromSeconds(5)); } catch (Exception) { /* fall through */ }
                if (!app.WaitForExit(10_000)) app.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            app?.Dispose();
            try { Directory.Delete(data, recursive: true); } catch (Exception) { /* Gecko may hold files briefly */ }
        }
    }

    private static string Send(string line, TimeSpan timeout)
    {
        using var pipe = new NamedPipeClientStream(".", "mosaic-automation", PipeDirection.InOut);
        pipe.Connect((int)timeout.TotalMilliseconds);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        pipe.Write(bytes);
        pipe.Flush();
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var reply = reader.ReadLineAsync();
        if (!reply.Wait(timeout)) throw new TimeoutException("No reply to " + line);
        return reply.Result ?? throw new IOException("The pipe closed without a reply.");
    }

    private JsonObject Do(string command, string value = "")
    {
        var request = new JsonObject { ["do"] = command, ["value"] = value }.ToJsonString();
        var reply = JsonNode.Parse(Send(request, TimeSpan.FromSeconds(30)))!.AsObject();
        output.WriteLine($"{command} {value} → {reply.ToJsonString()}");
        return reply;
    }

    private void Launch()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MOSAIC_APP_EXE")!, "--mosaic-automation")
        {
            UseShellExecute = false,
        };
        start.Environment["MOSAIC_DATA_DIR"] = data;
        app = Process.Start(start)!;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                if (JsonNode.Parse(Send("{\"do\":\"dump\"}", TimeSpan.FromSeconds(2)))?["state"]?["system"]?["started"]?.GetValue<bool>() == true) return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline) { /* not listening yet */ }
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Mosaic did not start the system area within 60 s.");
            Thread.Sleep(500);
        }
    }

    [AppFact]
    public void SystemAreaWorksInTheRunningApp()
    {
        Launch();
        var system = Do("dump")["state"]!["system"]!;
        Assert.True(system["iconFont"]!["names"]!.GetValue<int>() > 200);
        output.WriteLine("Icon font: " + system["iconFont"]!.ToJsonString());
        output.WriteLine("Appearance: " + system["appearance"]!.ToJsonString());

        // Default browser: register under a throwaway name, check, remove.
        const string name = "MosaicE2E";
        try
        {
            var registered = Do("registerDefault", name)["status"]!;
            Assert.True(registered["registered"]!.GetValue<bool>());
            Assert.True(registered["registeredForThisCopy"]!.GetValue<bool>());
        }
        finally
        {
            Assert.False(Do("unregisterDefault", name)["status"]!["registered"]!.GetValue<bool>());
        }

        // Activation (what a second launch forwards), applied to the window.
        var tabsBefore = Do("dump")["state"]!["tabs"]!.AsArray().Count;
        var activated = Do("activate", "--single-argument https://example.com/?q=a b");
        Assert.Equal("https://example.com/?q=a%20b", activated["request"]!["targets"]![0]!.GetValue<string>());
        Assert.True(Do("dump")["state"]!["tabs"]!.AsArray().Count >= Math.Max(1, tabsBefore));

        // Notifications: the system accepts it (shown) or Mosaic falls back; either way it is recorded.
        var notified = Do("notify", "Mosaic test|End-to-end notification")["result"]!;
        output.WriteLine("Notification status: " + notified["status"]);
        Assert.False(notified["status"]!.GetValue<string>().StartsWith("failed", StringComparison.Ordinal));
        Do("notifyActivate", "area=automation;check=1");
        Assert.Equal("1", Do("dump")["state"]!["system"]!["notifications"]!["lastActivation"]!["check"]!.GetValue<string>());

        // Taskbar progress applied to the real button.
        Do("taskbarProgress", "normal:0.5");
        var taskbar = Do("dump")["state"]!["system"]!["taskbar"]!;
        Assert.Equal("ok", taskbar["status"]!.GetValue<string>());
        Assert.Equal("normal:0.5", taskbar["applied"]!.GetValue<string>());
        Do("taskbarProgress", "none");

        // Jump list written through the shell.
        Assert.Equal("written", Do("jumpList")["status"]!.GetValue<string>());

        // Virtual desktops: the API answers for Mosaic's window.
        var desktop = Do("virtualDesktop")["virtualDesktop"]!;
        Assert.NotEqual("unavailable", desktop["status"]!.GetValue<string>());

        // Context menu opens (and covers the page), then closes.
        var menu = Do("contextMenuTest");
        Assert.True(menu["open"]!.GetValue<bool>());
        Assert.True(menu["pageCovered"]!.GetValue<bool>());
        Assert.False(Do("contextMenuTest", "close")["open"]!.GetValue<bool>());

        // Drops: the pipeline after OLE (a real drag needs a person).
        var dropped = Do("simulateDrop", "{\"text\":\"https://example.org/\"}");
        Assert.Equal("https://example.org/", dropped["targets"]![0]!.GetValue<string>());
    }
}
