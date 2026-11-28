using System.Text.Json.Nodes;
using Mosaic.Core.Engine;
using Mosaic.Core.Permissions;

namespace Mosaic.Core.Tests.Engine;

internal sealed class TemporaryFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("mosaic-engine-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Port of <c>Tests/MosaicCoreTests/EngineTests.swift</c>'s SitePermissionStoreTests.</summary>
public sealed class SitePermissionStoreTests : IDisposable
{
    private readonly TemporaryFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void NormalisesSitesToOrigins()
    {
        Assert.Equal("https://example.com", SitePermissionStore.Site("https://Example.com/path?q=1"));
        Assert.Equal("https://example.com", SitePermissionStore.Site("https://example.com:443/"));
        Assert.Equal("http://localhost:8000", SitePermissionStore.Site("http://localhost:8000/a.html"));
        Assert.Equal("http://example.com", SitePermissionStore.Site("http://example.com:80"));
        Assert.Equal("https://example.org", SitePermissionStore.Site("example.org"));
        Assert.Equal("file://", SitePermissionStore.Site("file:///C:/Users/me/page.html"));
        Assert.Equal("http://[::1]:8080", SitePermissionStore.Site("http://[::1]:8080/x"));
        Assert.Equal("https://xn--bcher-kva.de", SitePermissionStore.Site("https://bücher.de/"));
        Assert.Null(SitePermissionStore.Site("about:blank"));
        Assert.Null(SitePermissionStore.Site("data:text/html,hi"));
        Assert.Null(SitePermissionStore.Site("mosaic://settings"));
        Assert.Null(SitePermissionStore.Site(""));
    }

    [Fact]
    public void RemembersAndForgetsDecisions()
    {
        var store = new SitePermissionStore(folder.File("permissions.json"));
        Assert.Null(store.Decision(SitePermission.Camera, "https://meet.example"));
        store.Set(SitePermissionDecision.Allow, SitePermission.Camera, "https://meet.example/room/42");
        store.Set(SitePermissionDecision.Block, SitePermission.Location, "https://meet.example");
        Assert.Equal(SitePermissionDecision.Allow, store.Decision(SitePermission.Camera, "https://meet.example"));
        Assert.Equal(SitePermissionDecision.Block, store.Decision(SitePermission.Location, "https://meet.example/x"));
        // Another origin (scheme or port) is another site.
        Assert.Null(store.Decision(SitePermission.Camera, "http://meet.example"));
        store.Set(null, SitePermission.Camera, "https://meet.example");
        Assert.Null(store.Decision(SitePermission.Camera, "https://meet.example"));
        Assert.Equal([new SitePermissionEntry("https://meet.example", SitePermission.Location, SitePermissionDecision.Block)], store.Entries());
    }

    [Fact]
    public async Task PersistsAcrossInstancesInTheMacOSFormat()
    {
        var path = folder.File("permissions.json");
        var store = new SitePermissionStore(path);
        store.Set(SitePermissionDecision.Allow, SitePermission.Notifications, "https://b.example");
        store.Set(SitePermissionDecision.Block, SitePermission.Popups, "https://a.example");
        store.Set(SitePermissionDecision.Allow, SitePermission.Camera, "https://a.example");
        await store.FlushAsync();

        var reopened = new SitePermissionStore(path);
        Assert.Equal(SitePermissionDecision.Allow, reopened.Decision(SitePermission.Notifications, "https://b.example"));
        // Sorted by site, then in the order permissions are declared.
        Assert.Equal(
            ["https://a.example camera allow", "https://a.example popups block", "https://b.example notifications allow"],
            reopened.Entries().Select(entry => $"{entry.Site} {SitePermissionStore.Name(entry.Permission)} {SitePermissionStore.Name(entry.Decision)}"));

        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal(1, json["version"]!.GetValue<int>());
        Assert.Equal("camera", json["entries"]![0]!["permission"]!.GetValue<string>());
        Assert.Equal("https://a.example", json["entries"]![0]!["site"]!.GetValue<string>());
        Assert.Equal("allow", json["entries"]![0]!["decision"]!.GetValue<string>());
    }

    [Fact]
    public void ReadsAFileWrittenByMacOS()
    {
        var path = folder.File("SitePermissions.json");
        File.WriteAllText(path, """
            {
              "entries" : [
                { "decision" : "allow", "permission" : "microphone", "site" : "https://meet.example" },
                { "decision" : "block", "permission" : "telepathy", "site" : "https://meet.example" },
                { "decision" : "block", "permission" : "location", "site" : "about:blank" }
              ],
              "version" : 1
            }
            """);
        var store = new SitePermissionStore(path);
        Assert.Equal([new SitePermissionEntry("https://meet.example", SitePermission.Microphone, SitePermissionDecision.Allow)], store.Entries());
    }

    [Fact]
    public void IgnoresUnreadableFilesAndNonWebSites()
    {
        var path = folder.File("permissions.json");
        File.WriteAllText(path, "not json");
        var store = new SitePermissionStore(path);
        Assert.Empty(store.Entries());
        store.Set(SitePermissionDecision.Allow, SitePermission.Camera, "about:blank");
        Assert.Empty(store.Entries());
    }

    [Fact]
    public void RaisesChanges()
    {
        var store = new SitePermissionStore(folder.File("permissions.json"));
        var received = new List<string>();
        store.Changed += change => received.Add($"{change.Site} {(change.Decision is { } decision ? SitePermissionStore.Name(decision) : "forgotten")}");
        store.Set(SitePermissionDecision.Allow, SitePermission.Clipboard, "https://x.example");
        store.Set(SitePermissionDecision.Allow, SitePermission.Clipboard, "https://x.example"); // unchanged: nothing
        store.RemoveAll("https://x.example");
        Assert.Equal(["https://x.example allow", "https://x.example forgotten"], received);
    }

    [Fact]
    public void DisplayNames()
    {
        Assert.Equal("example.com", SitePermissionStore.DisplayName("https://example.com"));
        Assert.Equal("http://localhost:8000", SitePermissionStore.DisplayName("http://localhost:8000"));
        Assert.Equal("Local files", SitePermissionStore.DisplayName("file://"));
    }
}

public sealed class PageZoomTests : IDisposable
{
    private readonly TemporaryFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void StepsInAndOut()
    {
        Assert.Equal(1.1, PageZoom.ZoomedIn(1));
        Assert.Equal(1.1, PageZoom.ZoomedIn(1.05));
        Assert.Equal(0.9, PageZoom.ZoomedOut(1));
        Assert.Equal(0.9, PageZoom.ZoomedOut(0.95));
        Assert.Equal(5, PageZoom.ZoomedIn(5));
        Assert.Equal(0.33, PageZoom.ZoomedOut(0.33));
        // A factor set elsewhere (Gecko's own Ctrl+ steps 10 %) snaps to the next step.
        Assert.Equal(1.25, PageZoom.ZoomedIn(1.2));
    }

    [Fact]
    public void ChromiumLevelsRoundTrip()
    {
        Assert.True(Math.Abs(PageZoom.ChromiumLevel(1)) < 1e-9);
        Assert.True(Math.Abs(PageZoom.ChromiumLevel(1.2) - 1) < 1e-9);
        foreach (var factor in PageZoom.Steps)
        {
            Assert.True(Math.Abs(PageZoom.FactorForChromiumLevel(PageZoom.ChromiumLevel(factor)) - factor) < 1e-9);
        }
    }

    [Fact]
    public void ZoomSitesIgnoreWwwAndScheme()
    {
        Assert.Equal("example.com", PageZoom.Site("https://www.Example.com/a"));
        Assert.Equal("example.com", PageZoom.Site("http://example.com:8080/"));
        Assert.Null(PageZoom.Site("file:///C:/a.html"));
        Assert.Null(PageZoom.Site("about:blank"));
        Assert.Null(PageZoom.Site(""));
    }

    [Fact]
    public async Task StoreRemembersPerSiteAndForgetsDefault()
    {
        var path = folder.File("zoom.json");
        var store = new SiteZoomStore(path);
        Assert.Equal(1, store.Factor("https://example.com"));
        Assert.True(store.SetFactor(1.25, "https://www.example.com/page"));
        Assert.Equal(1.25, store.Factor("http://example.com/other"));
        Assert.False(store.SetFactor(2, "about:blank"));
        await store.FlushAsync();
        Assert.Equal(1.25, new SiteZoomStore(path).Factor("https://example.com"));
        Assert.Equal(1.25, JsonNode.Parse(await File.ReadAllTextAsync(path))!["example.com"]!.GetValue<double>());
        store.SetFactor(1, "https://example.com");
        Assert.Empty(store.Sites);
        await store.FlushAsync();
        Assert.Equal(1, new SiteZoomStore(path).Factor("https://example.com"));
        store.SetFactor(99, "https://big.example");
        Assert.Equal(PageZoom.Maximum, store.Factor("https://big.example"));
    }

    [Fact]
    public void PercentLabels()
    {
        Assert.Equal("110 %", PageZoom.PercentLabel(1.1));
        Assert.Equal("67 %", PageZoom.PercentLabel(0.67));
        Assert.Equal("33 %", PageZoom.PercentLabel(0.33));
    }

    [Fact]
    public void FindStatusLabels()
    {
        Assert.Equal("No matches", new FindStatus(0, 0).Label);
        Assert.Equal("3 of 12", new FindStatus(3, 12).Label);
        Assert.Equal("4 matches", new FindStatus(0, 4).Label);
        Assert.Equal("2 of 1000+", new FindStatus(2, 1000, 1000).Label);
        Assert.Equal("Match 3 of 12", new FindStatus(3, 12).AccessibleLabel);
        Assert.False(new FindStatus(0, 0).HasMatches);
    }
}

public sealed class PermissionPromptingTests : IDisposable
{
    private readonly TemporaryFolder folder = new();

    public void Dispose() => folder.Dispose();

    private SitePermissionStore Store() => new(folder.File($"{Guid.NewGuid():N}.json"));

    [Fact]
    public void Sentences()
    {
        Assert.Equal("meet.example wants to use your camera and microphone",
            new PermissionRequestInfo("https://meet.example", PermissionKind.Camera, PermissionKind.Microphone).Sentence);
        Assert.Equal("http://localhost:8000 wants to know your location",
            new PermissionRequestInfo("http://localhost:8000", PermissionKind.Location).Sentence);
        Assert.Equal("a.example wants to show notifications and control your MIDI devices",
            new PermissionRequestInfo("https://a.example", [PermissionKind.Notifications, PermissionKind.Other], ["midi"]).Sentence);
        Assert.Equal("This page wants to see text and images you copy",
            new PermissionRequestInfo("null", PermissionKind.Clipboard).Sentence);
    }

    [Fact]
    public void DecidesFromRememberedChoices()
    {
        var store = Store();
        var request = new PermissionRequestInfo("https://meet.example", PermissionKind.Camera, PermissionKind.Microphone);
        Assert.Null(request.AutomaticDecision(store));
        store.Set(SitePermissionDecision.Allow, SitePermission.Camera, "https://meet.example");
        Assert.Null(request.AutomaticDecision(store)); // microphone still undecided
        store.Set(SitePermissionDecision.Allow, SitePermission.Microphone, "https://meet.example");
        Assert.Equal(SitePermissionDecision.Allow, request.AutomaticDecision(store));
        store.Set(SitePermissionDecision.Block, SitePermission.Camera, "https://meet.example");
        Assert.Equal(SitePermissionDecision.Block, request.AutomaticDecision(store));
    }

    [Fact]
    public void ScreenSharingAndUnknownKindsAreAlwaysAsked()
    {
        var store = Store();
        store.Set(SitePermissionDecision.Allow, SitePermission.Camera, "https://meet.example");
        var screen = new PermissionRequestInfo("https://meet.example", PermissionKind.Screen);
        Assert.False(screen.IsRememberable);
        Assert.Null(screen.AutomaticDecision(store));
        Assert.False(new PermissionRequestInfo("null", PermissionKind.Camera).IsRememberable);
    }

    [Fact]
    public void MergesRequestsForOneQuestion()
    {
        var merged = new PermissionRequestInfo("https://meet.example", PermissionKind.Camera)
            .Merging(new PermissionRequestInfo("https://meet.example", PermissionKind.Microphone, PermissionKind.Camera));
        Assert.Equal([PermissionKind.Camera, PermissionKind.Microphone], merged.Kinds);
    }

    [Fact]
    public void WindowsPrivacySwitches()
    {
        Assert.Equal(WindowsCapabilityAccess.Allowed, WindowsPrivacy.Interpret(device: false, user: false, desktopApps: false));
        Assert.Equal(WindowsCapabilityAccess.DeniedForDesktopApps, WindowsPrivacy.Interpret(false, false, true));
        Assert.Equal(WindowsCapabilityAccess.DeniedForUser, WindowsPrivacy.Interpret(false, true, true));
        Assert.Equal(WindowsCapabilityAccess.DeniedForDevice, WindowsPrivacy.Interpret(true, false, false));
        Assert.Null(WindowsPrivacy.Explanation(WindowsCapability.Camera, WindowsCapabilityAccess.Allowed));
        Assert.Null(WindowsPrivacy.Explanation(WindowsCapability.Camera, WindowsCapabilityAccess.Unknown));
        Assert.Contains("Let desktop apps access your camera", WindowsPrivacy.Explanation(WindowsCapability.Camera, WindowsCapabilityAccess.DeniedForDesktopApps));
        Assert.Contains("Location services are turned off", WindowsPrivacy.Explanation(WindowsCapability.Location, WindowsCapabilityAccess.DeniedForDevice));
        Assert.Equal("ms-settings:privacy-microphone", WindowsPrivacy.SettingsUri(WindowsCapability.Microphone));
        if (!OperatingSystem.IsWindows()) Assert.Equal(WindowsCapabilityAccess.Unknown, WindowsPrivacy.Read(WindowsCapability.Camera));
        else Assert.NotEqual(WindowsCapabilityAccess.Unknown, WindowsPrivacy.Read(WindowsCapability.Camera));
    }
}
