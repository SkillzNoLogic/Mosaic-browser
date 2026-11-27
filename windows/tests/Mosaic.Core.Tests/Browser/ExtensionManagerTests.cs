using System.Net;
using Mosaic.Core.Browser;
using Mosaic.Core.Extensions;

namespace Mosaic.Core.Tests.Browser;

public class ExtensionManagerTests
{
    private const string Id = TestRules.DarkReader;
    private const string Listing = $"https://chromewebstore.google.com/detail/dark-reader/{Id}";

    /// <summary>A fixture whose Gecko is already running, as it is once any Gecko page is open.</summary>
    private static BrowserFixture WithGeckoRunning(Action<BrowserFixture>? configure = null)
    {
        var fixture = new BrowserFixture(configure);
        fixture.OpenGeckoTab("https://example.com/");
        fixture.Gecko.Calls.Clear();
        return fixture;
    }

    // ---- Installing ----

    [Fact]
    public void InstallFromAListingLinkDownloadsConvertsAndLoadsIntoGecko() => TestLoop.Run(async () =>
    {
        using var fixture = WithGeckoRunning();
        var manager = fixture.Extensions;
        fixture.Http.Respond = _ => Task.FromResult(StubHttpHandler.Package("Dark Reader|4.9"));

        var installed = await manager.InstallFromWebStoreAsync(Listing);

        Assert.True(installed);
        Assert.Equal([new Uri($"https://store.test/crx/{Id}")], fixture.Http.Requests);
        var extension = Assert.Single(manager.Extensions);
        Assert.Equal(Id, extension.Id);
        Assert.Equal("Dark Reader", extension.Name);
        Assert.True(manager.IsInstalled(Id));
        Assert.True(manager.HasExtensions);
        // Not quiet: a first install may show the extension's welcome page.
        Assert.Equal([$"/library/{Id}/gecko", false], Assert.Single(fixture.Gecko.CallsTo("installExtension")));
        Assert.True(manager.IsLoaded(extension));
        Assert.Null(manager.ErrorMessage);
        Assert.False(manager.IsBusy);

        var row = Assert.Single(manager.Items);
        Assert.Equal("Dark Reader", row.Name);
        Assert.Equal("4.9", row.Version);
        Assert.Equal("MV3", row.ManifestBadge);
        Assert.True(row.IsRunning);
        Assert.True(row.IsEnabled);
        Assert.Equal($"/library/{Id}/gecko/icon.png", row.IconFile);
    });

    [Fact]
    public void InstallByBareIdWorksTheSame() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();

        Assert.True(await fixture.Extensions.InstallFromWebStoreAsync(Id));

        Assert.True(fixture.Extensions.IsInstalled(Id));
    });

    [Fact]
    public void InstallWhileGeckoIsNotRunningWaitsForTheNextStart() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var manager = fixture.Extensions;

        Assert.True(await manager.InstallFromWebStoreAsync(Id));

        Assert.Empty(fixture.Gecko.Calls);
        Assert.Equal(0, fixture.Gecko.StartCount);
        Assert.False(manager.IsLoaded(manager.Extensions[0]));
        Assert.False(manager.Items[0].IsRunning);
    });

    [Fact]
    public void SomethingThatIsNotAnExtensionReferenceIsRejectedBeforeAnyDownload() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var manager = fixture.Extensions;

        Assert.False(await manager.InstallFromWebStoreAsync("dark reader please"));

        Assert.Equal("That is not a Chrome Web Store link or extension ID.", manager.ErrorMessage);
        Assert.True(manager.HasError);
        Assert.Empty(fixture.Http.Requests);
        Assert.False(manager.IsBusy);
    });

    [Fact]
    public void StoreRefusalIsExplainedWithItsStatus() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var manager = fixture.Extensions;
        fixture.Http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.False(await manager.InstallFromWebStoreAsync(Id));

        Assert.Equal(
            "The Chrome Web Store did not provide this extension (HTTP 204). It may be unlisted, paid, or removed.",
            manager.ErrorMessage);
        Assert.Empty(manager.Extensions);
        Assert.False(manager.IsBusy);
    });

    [Fact]
    public void EmptyDownloadIsARefusalToo() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        fixture.Http.Respond = _ => Task.FromResult(StubHttpHandler.Package(""));

        Assert.False(await fixture.Extensions.InstallFromWebStoreAsync(Id));

        Assert.Contains("(HTTP 200)", fixture.Extensions.ErrorMessage);
    });

    [Fact]
    public void NetworkFailureIsReportedNotThrown() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        fixture.Http.Respond = _ => throw new HttpRequestException("No such host is known.");

        Assert.False(await fixture.Extensions.InstallFromWebStoreAsync(Id));

        Assert.Equal("No such host is known.", fixture.Extensions.ErrorMessage);
        Assert.False(fixture.Extensions.IsBusy);
    });

    [Fact]
    public void PackageTheLibraryRejectsLeavesEverythingAsItWas() => TestLoop.Run(async () =>
    {
        using var fixture = WithGeckoRunning(f => f.Library.Add("aaaa", "Alpha"));
        var manager = fixture.Extensions;
        fixture.Library.InstallFailure = new ExtensionStoreException("The package has no manifest.json, so it is not an extension.");

        Assert.False(await manager.InstallFromWebStoreAsync(Id));

        Assert.Equal("The package has no manifest.json, so it is not an extension.", manager.ErrorMessage);
        Assert.Equal("Alpha", Assert.Single(manager.Extensions).Name);
        Assert.Empty(fixture.Gecko.CallsTo("installExtension"));
    });

    [Fact]
    public void NewAttemptClearsTheOldError() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var manager = fixture.Extensions;
        await manager.InstallFromWebStoreAsync("nonsense");
        Assert.NotNull(manager.ErrorMessage);

        Assert.True(await manager.InstallFromWebStoreAsync(Id));

        Assert.Null(manager.ErrorMessage);
        Assert.False(manager.HasError);
    });

    [Fact]
    public void InstallFromAFileReadsThePackageFromDisk() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var path = fixture.PathFor("sideloaded.crx");
        await File.WriteAllTextAsync(path, "Sideloaded|0.3");

        Assert.True(await fixture.Extensions.InstallFileAsync(path));

        var extension = Assert.Single(fixture.Extensions.Extensions);
        // No ID is known up front for a file; the library works it out from the package.
        Assert.Equal(FakeExtensionLibrary.FileId, extension.Id);
        Assert.Equal("Sideloaded", extension.Name);
        Assert.Empty(fixture.Http.Requests);
    });

    [Fact]
    public void MissingFileIsReported() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();

        Assert.False(await fixture.Extensions.InstallFileAsync(fixture.PathFor("nowhere.crx")));

        Assert.NotNull(fixture.Extensions.ErrorMessage);
        Assert.Empty(fixture.Extensions.Extensions);
    });

    [Fact]
    public void OnlyOneInstallRunsAtATime() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var manager = fixture.Extensions;
        var download = new TaskCompletionSource<HttpResponseMessage>();
        fixture.Http.Respond = _ => download.Task;

        var first = manager.InstallFromWebStoreAsync(Id);
        await TestLoop.Settle();
        Assert.True(manager.IsBusy);
        Assert.False(manager.IsIdle);

        Assert.False(await manager.InstallFromWebStoreAsync(Id));
        Assert.Single(fixture.Http.Requests);
        Assert.Null(manager.ErrorMessage);

        download.SetResult(StubHttpHandler.Package("Dark Reader|4.9"));
        Assert.True(await first);
        Assert.False(manager.IsBusy);
        Assert.Single(manager.Extensions);
    });

    [Fact]
    public void UnpackingRunsOffTheUiThread() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();

        await fixture.Extensions.InstallFromWebStoreAsync(Id);

        Assert.NotNull(fixture.Library.InstallThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, fixture.Library.InstallThread);
    });

    [Fact]
    public void ReinstallKeepsTheExtensionDisabledAndOutOfGecko() => TestLoop.Run(async () =>
    {
        using var fixture = WithGeckoRunning(f => f.Library.Add(Id, "Dark Reader", "4.8", enabled: false));
        var manager = fixture.Extensions;

        Assert.True(await manager.InstallFromWebStoreAsync(Id));

        var extension = Assert.Single(manager.Extensions);
        Assert.Equal("2.0", extension.Version);
        Assert.False(extension.Enabled);
        Assert.Empty(fixture.Gecko.CallsTo("installExtension"));
    });

    // ---- Removing, enabling ----

    [Fact]
    public void ReinstallUnloadsTheOldCopyBeforeReplacingItsFiles() => TestLoop.Run(async () =>
    {
        // On Windows an engine holding the old files open would make the store's swap fail.
        using var fixture = WithGeckoRunning(f => f.Library.Add(Id, "Dark Reader"));
        var manager = fixture.Extensions;
        Assert.True(manager.IsLoaded(manager.Extensions[0]));
        fixture.Http.Respond = _ => Task.FromResult(StubHttpHandler.Package("Dark Reader|5.0"));

        Assert.True(await manager.InstallFromWebStoreAsync(Id));

        var methods = fixture.Gecko.Calls.Select(call => call.Method)
            .Where(method => method is "uninstallExtension" or "installExtension").ToList();
        Assert.Equal(["uninstallExtension", "installExtension"], methods.TakeLast(2));
        Assert.True(manager.IsLoaded(Assert.Single(manager.Extensions)));
    });

    [Fact]
    public void RemoveUnloadsFromGeckoAndForgetsTheExtension() => TestLoop.Run(async () =>
    {
        using var fixture = WithGeckoRunning(f => f.Library.Add("aaaa", "Alpha"));
        var manager = fixture.Extensions;
        var extension = manager.Extensions[0];
        Assert.True(manager.IsLoaded(extension));

        await manager.RemoveAsync(extension);

        Assert.Equal(["aaaa@crx.mosaic"], Assert.Single(fixture.Gecko.CallsTo("uninstallExtension")));
        Assert.Empty(manager.Extensions);
        Assert.Empty(manager.Items);
        Assert.False(manager.HasExtensions);
        Assert.False(manager.IsLoaded(extension));
    });

    [Fact]
    public void RemoveWhileGeckoIsStoppedOnlyTouchesTheLibrary() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => f.Library.Add("aaaa", "Alpha"));

        await fixture.Extensions.RemoveAsync(fixture.Extensions.Extensions[0]);

        Assert.Empty(fixture.Extensions.Extensions);
        Assert.Empty(fixture.Gecko.Calls);
        Assert.Equal(0, fixture.Gecko.StartCount);
    });

    [Fact]
    public void RemoveThatFailsOnDiskIsReportedAndTheRowStays() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => f.Library.Add("aaaa", "Alpha"));
        fixture.Library.RemoveFailure = new IOException("The folder is in use.");

        await fixture.Extensions.RemoveAsync(fixture.Extensions.Extensions[0]);

        Assert.Equal("Alpha could not be removed: The folder is in use.", fixture.Extensions.ErrorMessage);
        Assert.Single(fixture.Extensions.Items);
    });

    [Fact]
    public void DisablingUnloadsAndEnablingLoadsAgain() => TestLoop.Run(async () =>
    {
        using var fixture = WithGeckoRunning(f => f.Library.Add("aaaa", "Alpha"));
        var manager = fixture.Extensions;
        var row = manager.Items[0];

        await manager.SetEnabledAsync(false, manager.Extensions[0]);

        Assert.False(manager.Extensions[0].Enabled);
        Assert.False(fixture.Library.Extensions[0].Enabled);
        Assert.Single(fixture.Gecko.CallsTo("uninstallExtension"));
        Assert.False(manager.IsLoaded(manager.Extensions[0]));
        // The same row object is updated in place, so what the user expanded stays expanded.
        Assert.Same(row, manager.Items[0]);
        Assert.False(row.IsEnabled);
        Assert.False(row.IsRunning);

        await manager.SetEnabledAsync(true, manager.Extensions[0]);

        Assert.True(manager.Extensions[0].Enabled);
        Assert.Equal(["/library/aaaa/gecko", false], Assert.Single(fixture.Gecko.CallsTo("installExtension")));
        Assert.True(row.IsEnabled);
        Assert.True(row.IsRunning);
    });

    [Fact]
    public void EnablingWhileGeckoIsStoppedDoesNotStartIt() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => f.Library.Add("aaaa", "Alpha", enabled: false));

        await fixture.Extensions.SetEnabledAsync(true, fixture.Extensions.Extensions[0]);

        Assert.True(fixture.Extensions.Extensions[0].Enabled);
        Assert.Equal(0, fixture.Gecko.StartCount);
        Assert.Empty(fixture.Gecko.Calls);
    });

    // ---- Gecko ----

    [Fact]
    public void ExtensionGeckoRefusesIsReportedAndNotMarkedRunning() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add("aaaa", "Alpha");
            f.Library.Add("bbbb", "Beta");
            f.Gecko.Handlers["installExtension"] = arguments =>
                Equals(arguments[0], "/library/aaaa/gecko") ? throw new InvalidOperationException("bad manifest") : null;
        });
        var manager = fixture.Extensions;

        fixture.OpenGeckoTab("https://example.com/");

        Assert.Equal("Alpha could not be loaded into Gecko: bad manifest", manager.ErrorMessage);
        Assert.False(manager.Items[0].IsRunning);
        // One bad extension does not stop the others, or the page.
        Assert.True(manager.Items[1].IsRunning);
        Assert.Equal(1, fixture.Model.SelectedTab!.GeckoId);
    });

    [Fact]
    public void EnsureLoadedLoadsOnlyWhatIsMissing() => TestLoop.Run(async () =>
    {
        using var fixture = WithGeckoRunning(f =>
        {
            f.Library.Add("aaaa", "Alpha");
            f.Library.Add("bbbb", "Beta", enabled: false);
        });
        var manager = fixture.Extensions;

        await manager.EnsureLoadedAsync(manager.Extensions[0]);
        Assert.Empty(fixture.Gecko.CallsTo("installExtension"));

        await manager.EnsureLoadedAsync(manager.Extensions[1]);
        Assert.Equal(["/library/bbbb/gecko", false], Assert.Single(fixture.Gecko.CallsTo("installExtension")));
    });

    [Fact]
    public void WithoutALibraryExtensionsAreUnavailableButNothingBreaks() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var manager = new ExtensionManager(
            fixture.Gecko, () => throw new UnauthorizedAccessException("Access to the path is denied."),
            fixture.Settings, fixture.ChromiumExtensions, fixture.Http, fixture.Rules);

        Assert.Equal("Extensions are unavailable: Access to the path is denied.", manager.ErrorMessage);
        Assert.Empty(manager.Extensions);
        Assert.False(await manager.InstallFromWebStoreAsync(Id));
        await manager.LoadAllIntoGeckoAsync();
        await fixture.ChromiumExtensions.BecomeReadyAsync();
        Assert.Empty(fixture.Http.Requests);
        Assert.Empty(fixture.ChromiumExtensions.Loads);
    });

    // ---- Rows ----

    [Fact]
    public void RowsFollowTheLibraryOrderAndAreReused() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add("bbbb", "Beta");
            f.Library.Add("dddd", "Delta");
        });
        var manager = fixture.Extensions;
        var beta = manager.Items[0];
        var delta = manager.Items[1];
        fixture.Http.Respond = _ => Task.FromResult(StubHttpHandler.Package("Charlie|1.0"));

        await manager.InstallFromWebStoreAsync(Id);

        Assert.Equal(["Beta", "Charlie", "Delta"], manager.Items.Select(item => item.Name));
        Assert.Same(beta, manager.Items[0]);
        Assert.Same(delta, manager.Items[2]);

        await manager.RemoveAsync(beta.Extension);
        Assert.Equal(["Charlie", "Delta"], manager.Items.Select(item => item.Name));
        Assert.Same(delta, manager.Items[1]);
    });

    [Fact]
    public void RowSummarisesCompatibilityByItsWorstNote() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add("aaaa", "Clean", hasPopup: true);
            f.Library.Add("bbbb", "Informed", notes: [new(NoteSeverity.Info, "Service worker converted.")], hasOptions: true);
            f.Library.Add("cccc", "Limited", notes:
                [new(NoteSeverity.Info, "Service worker converted."), new(NoteSeverity.Warning, "sidePanel is unavailable.")]);
            f.Library.Add("dddd", "Stuck", notes:
                [new(NoteSeverity.Blocker, "Needs the debugger API."), new(NoteSeverity.Info, "Service worker converted.")]);
        });
        var rows = fixture.Extensions.Items;

        Assert.False(rows[0].HasNotes);
        Assert.Null(rows[0].WorstSeverity);
        Assert.True(rows[0].HasPopup);
        Assert.False(rows[0].HasOptions);

        Assert.Equal(NoteSeverity.Info, rows[1].WorstSeverity);
        Assert.Equal("Conversion notes", rows[1].CompatibilityLabel);
        Assert.True(rows[1].HasOptions);

        Assert.Equal(NoteSeverity.Warning, rows[2].WorstSeverity);
        Assert.Equal("Partly compatible", rows[2].CompatibilityLabel);

        Assert.Equal(NoteSeverity.Blocker, rows[3].WorstSeverity);
        Assert.Equal("May not work on Gecko", rows[3].CompatibilityLabel);
        Assert.Equal(2, rows[3].Notes.Count);
        Assert.Equal("Enable Stuck", rows[3].EnableLabel);
    });

    // ---- Add to Mosaic ----

    [Fact]
    public void WebStoreListingOffersToAddTheExtension() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        Assert.False(model.ShowsAddExtension);

        var (_, browser) = fixture.OpenChromiumTab(Listing);
        Assert.Equal(Id, model.WebStoreExtensionId);
        Assert.True(model.ShowsAddExtension);
        Assert.Equal("Add to Mosaic", model.AddExtensionLabel);

        await model.AddWebStoreExtensionAsync();

        Assert.True(fixture.Extensions.IsInstalled(Id));
        Assert.Equal("Extension added to Mosaic.", model.Notice);
        Assert.Equal("Reinstall", model.AddExtensionLabel);

        // Leaving the listing withdraws the offer.
        browser.RaiseUrl("https://chromewebstore.google.com/category/extensions");
        Assert.False(model.ShowsAddExtension);
        Assert.Null(model.WebStoreExtensionId);
    });

    [Fact]
    public void FailedAddIsAnnouncedWithTheReason() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        fixture.OpenChromiumTab(Listing);
        fixture.Http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        await fixture.Model.AddWebStoreExtensionAsync();

        Assert.Contains("(HTTP 404)", fixture.Model.Notice);
        Assert.Equal("Add to Mosaic", fixture.Model.AddExtensionLabel);
    });

    [Fact]
    public void AddDoesNothingAwayFromAListing() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        fixture.OpenChromiumTab($"https://example.com/{Id}");

        await fixture.Model.AddWebStoreExtensionAsync();

        Assert.Empty(fixture.Http.Requests);
        Assert.Null(fixture.Model.Notice);
    });

    // ---- Chromium (experimental) ----

    [Fact]
    public void WhenChromiumComesUpEnabledExtensionsAreLoadedUnconverted() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add("aaaa", "Alpha");
            f.Library.Add("bbbb", "Beta", enabled: false);
        });
        var host = fixture.ChromiumExtensions;

        await host.BecomeReadyAsync();

        Assert.Equal(["/library/aaaa/chromium"], host.Loads);
        Assert.True(fixture.Extensions.Items[0].IsInChromium);
        Assert.False(fixture.Extensions.Items[1].IsInChromium);
        Assert.Equal("cr:/library/aaaa/chromium", fixture.ReloadSettings().ChromiumExtensionIds["aaaa"]);
        // Nothing about this touches Gecko.
        Assert.Empty(fixture.Gecko.Calls);
        Assert.Equal(0, fixture.Gecko.StartCount);
    });

    [Fact]
    public void ExtensionTheProfileKeptFromLastRunIsNotInstalledAgain() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add("aaaa", "Alpha");
            var kept = FakeChromiumExtensionHost.IdFor("/library/aaaa/chromium");
            f.ChromiumExtensions.Profile[kept] = "/library/aaaa/chromium";
            var settings = new BrowserSettings(f.SettingsPath);
            settings.SetChromiumExtensionId("aaaa", kept);
            settings.Save();
        });
        var host = fixture.ChromiumExtensions;

        await host.BecomeReadyAsync();

        // Installing it again would throw away what the extension has stored.
        Assert.Empty(host.Loads);
        Assert.Empty(host.Unloads);
        Assert.True(fixture.Extensions.Items[0].IsInChromium);
    });

    [Fact]
    public void ProfileExtensionsMosaicNoLongerWantsAreRemoved() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add("aaaa", "Alpha", enabled: false);
            f.ChromiumExtensions.Profile["cr:/library/aaaa/chromium"] = "/library/aaaa/chromium";
            f.ChromiumExtensions.Profile["cr:/library/gone/chromium"] = "/library/gone/chromium";
            var settings = new BrowserSettings(f.SettingsPath);
            settings.SetChromiumExtensionId("aaaa", "cr:/library/aaaa/chromium");
            settings.SetChromiumExtensionId("gone", "cr:/library/gone/chromium");
            settings.Save();
        });
        var host = fixture.ChromiumExtensions;

        await host.BecomeReadyAsync();

        Assert.Empty(host.Profile);
        Assert.Equal(2, host.Unloads.Count);
        Assert.Empty(fixture.ReloadSettings().ChromiumExtensionIds);
    });

    [Fact]
    public void ChromiumRefusalIsNotedOnTheRowAndNowhereElse() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add("aaaa", "Alpha");
            f.Library.Add("bbbb", "Beta");
            f.ChromiumExtensions.RefusedFolders.Add("/library/aaaa/chromium");
        });
        var manager = fixture.Extensions;

        await fixture.ChromiumExtensions.BecomeReadyAsync();

        Assert.False(manager.Items[0].IsInChromium);
        Assert.True(manager.Items[0].HasChromiumError);
        Assert.Equal("Manifest version 2 is not supported.", manager.Items[0].ChromiumError);
        Assert.True(manager.Items[1].IsInChromium);
        Assert.False(manager.Items[1].HasChromiumError);
        // The Gecko path's error channel stays clean, and Gecko still loads the extension.
        Assert.Null(manager.ErrorMessage);
        fixture.OpenGeckoTab("https://example.com/");
        Assert.True(manager.Items[0].IsRunning);
        Assert.Null(manager.ErrorMessage);
    });

    [Fact]
    public void ProfileThatCannotBeListedLeavesExtensionsGeckoOnly() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add("aaaa", "Alpha");
            f.ChromiumExtensions.ListFails = true;
        });

        await fixture.ChromiumExtensions.BecomeReadyAsync();

        Assert.False(fixture.Extensions.Items[0].IsInChromium);
        Assert.Null(fixture.Extensions.ErrorMessage);
    });

    [Fact]
    public void InstallAndToggleReachChromiumOnlyWhileItsProfileIsUp() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var manager = fixture.Extensions;
        var host = fixture.ChromiumExtensions;

        // No Chromium tab has been opened: nothing to load into.
        await manager.InstallFromWebStoreAsync(Id);
        Assert.Empty(host.Loads);
        Assert.False(manager.Items[0].IsInChromium);

        // The first Chromium page brings the profile up, and the extension with it.
        await host.BecomeReadyAsync();
        var folder = $"/library/{Id}/chromium";
        Assert.Equal([folder], host.Loads);
        Assert.True(manager.Items[0].IsInChromium);

        await manager.SetEnabledAsync(false, manager.Extensions[0]);
        Assert.Equal([FakeChromiumExtensionHost.IdFor(folder)], host.Unloads);
        Assert.False(manager.Items[0].IsInChromium);
        Assert.Empty(fixture.ReloadSettings().ChromiumExtensionIds);

        await manager.SetEnabledAsync(true, manager.Extensions[0]);
        Assert.Equal([folder, folder], host.Loads);
        Assert.True(manager.Items[0].IsInChromium);
    });

    [Fact]
    public void ReinstallReloadsTheChangedFilesIntoChromium() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => f.Library.Add(Id, "Dark Reader", "4.8"));
        var host = fixture.ChromiumExtensions;
        await host.BecomeReadyAsync();
        Assert.Single(host.Loads);

        await fixture.Extensions.InstallFromWebStoreAsync(Id);

        Assert.Equal(2, host.Loads.Count);
        Assert.True(fixture.Extensions.Items[0].IsInChromium);
    });

    [Fact]
    public void ReinstallWhileChromiumIsDownForgetsTheStaleProfileCopy() => TestLoop.Run(async () =>
    {
        var folder = $"/library/{Id}/chromium";
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add(Id, "Dark Reader", "4.8");
            f.ChromiumExtensions.Profile[FakeChromiumExtensionHost.IdFor(folder)] = folder;
            var settings = new BrowserSettings(f.SettingsPath);
            settings.SetChromiumExtensionId(Id, FakeChromiumExtensionHost.IdFor(folder));
            settings.Save();
        });
        var host = fixture.ChromiumExtensions;

        await fixture.Extensions.InstallFromWebStoreAsync(Id);
        Assert.Empty(fixture.ReloadSettings().ChromiumExtensionIds);

        // Next time Chromium comes up, the copy in the profile is replaced rather than trusted.
        await host.BecomeReadyAsync();
        Assert.Equal([folder], host.Loads);
    });

    [Fact]
    public void RemoveTakesTheExtensionOutOfChromiumToo() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => f.Library.Add("aaaa", "Alpha"));
        var host = fixture.ChromiumExtensions;
        await host.BecomeReadyAsync();

        await fixture.Extensions.RemoveAsync(fixture.Extensions.Extensions[0]);

        Assert.Empty(host.Profile);
        Assert.Empty(fixture.ReloadSettings().ChromiumExtensionIds);
    });

    [Fact]
    public void ManagerWorksWithoutAnyChromiumExtensionSupport() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var manager = new ExtensionManager(
            fixture.Gecko, () => fixture.Library, fixture.Settings, chromium: null, fixture.Http, fixture.Rules);

        Assert.True(await manager.InstallFromWebStoreAsync(Id));
        await manager.SetEnabledAsync(false, manager.Extensions[0]);
        await manager.RemoveAsync(manager.Extensions[0]);

        Assert.Empty(manager.Extensions);
        Assert.Null(manager.ErrorMessage);
    });
}
