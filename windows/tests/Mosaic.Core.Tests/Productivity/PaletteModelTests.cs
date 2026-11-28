using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Mosaic.Core.Tests.Browser;
using static Mosaic.Core.Tests.Productivity.ProductivityTestSupport;

namespace Mosaic.Core.Tests.Productivity;

public class PaletteModelTests
{
    [Fact]
    public void PresentingListsActionsAndOtherTabsWithShortcuts() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        var other = model.NewTab("https://example.com/", EngineKind.Gecko, select: false);
        var palette = model.Productivity.Palette;

        palette.Present();
        Assert.True(palette.IsPresented);
        Assert.Equal("", palette.Query);
        var ids = palette.Results.Select(a => a.Id).ToList();
        Assert.Contains("tab.new", ids);
        Assert.Contains($"tab:{other.Id}", ids);
        Assert.DoesNotContain($"tab:{model.SelectedTab!.Id}", ids);
        Assert.Equal("Ctrl+T", palette.Results.First(a => a.Id == "tab.new").Shortcut?.DisplayString);
        Assert.Equal(1, fixture.WindowActivationRequests);

        palette.Query = "exam";
        Assert.Equal($"tab:{other.Id}", palette.Results[0].Id);
        palette.PerformSelection();
        Assert.False(palette.IsPresented);
        Assert.Same(other, model.SelectedTab);
    });

    [Fact]
    public void TypingOffersToOpenAnAddressOrSearch() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        var palette = model.Productivity.Palette;
        palette.Present();

        palette.Query = "example.org";
        var open = palette.Results.Single(a => a.Id == "web.open");
        Assert.Equal("https://example.org", open.Subtitle?.TrimEnd('/'));
        Assert.Contains(palette.Results, a => a.Id == "web.search");

        // The start page tab is reused for the address.
        var start = model.SelectedTab!;
        Assert.True(palette.Perform("web.open"));
        Assert.Same(start, model.SelectedTab);
        Assert.StartsWith("https://example.org", start.Url);

        palette.Present();
        palette.Query = "kittens";
        Assert.Equal("Search Google for “kittens”", palette.Results.Last().Title);
        palette.Perform("web.search");
        // A page was open, so the search opens in a new tab.
        Assert.Equal(2, model.Tabs.Count);
        Assert.Contains("kittens", model.SelectedTab!.Url);
    });

    [Fact]
    public void ArrowKeysWrapAndNoMatchesLeavesOnlyWebFallbacks() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var palette = fixture.Model.Productivity.Palette;
        await fixture.Model.Productivity.StartAsync();
        palette.Present();
        var count = palette.Results.Count;
        palette.MoveSelection(-1);
        Assert.Equal(count - 1, palette.Selection);
        palette.MoveSelection(1);
        Assert.Equal(0, palette.Selection);

        palette.Query = "qqqqzzzz";
        Assert.All(palette.Results, a => Assert.Equal(PaletteKind.Web, a.Kind));
        palette.Dismiss();
        Assert.False(palette.IsPresented);
    });

    [Fact]
    public void RecentChoicesComeFirstNextTimeAndArePersisted() => TestLoop.Run(async () =>
    {
        string folder;
        using (var fixture = Create())
        {
            folder = fixture.PathFor("productivity");
            var palette = fixture.Model.Productivity.Palette;
            await fixture.Model.Productivity.StartAsync();
            palette.Present();
            Assert.True(palette.Perform("search.next"));
            palette.Present();
            Assert.Equal("search.next", palette.Results[0].Id);
            await fixture.Model.Productivity.Files.Flush();
            Assert.Contains("search.next", File.ReadAllText(Path.Combine(folder, ProductivityFiles.PaletteRecentsFile)));

            using var second = new BrowserFixture();
            second.Model.UseProductivityOptions(new ProductivityOptions { Directory = folder, ReaderResources = ReaderResources });
            await second.Model.Productivity.StartAsync();
            Assert.Equal(["search.next"], second.Model.Productivity.Palette.RecentIds);
        }
    });

    [Fact]
    public void ReaderAndBlockerActionsFollowThePage() => TestLoop.Run(async () =>
    {
        using var fixture = Create(f => f.ShipBlocker());
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        var palette = model.Productivity.Palette;
        palette.Present();
        Assert.DoesNotContain(palette.Results, a => a.Id == "reader.toggle");

        fixture.OpenGeckoTab("https://news.example/story");
        palette.Present();
        palette.Query = "reader";
        var reader = palette.Results.First(a => a.Id == "reader.toggle");
        Assert.Equal("Show Reader View", reader.Title);
        Assert.Equal("F9", reader.Shortcut?.DisplayString);
        Assert.Equal("This page may not be an article", reader.Subtitle);
        palette.Query = "switch";
        Assert.Contains(palette.Results, a => a.Id == "engine.switch" && a.Title == "Switch Tab to Chromium");
    });

    [Fact]
    public void CommandsOtherAreasRegisterAppearInThePalette() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        var id = $"Edit/Find {Guid.NewGuid():N}";
        var found = 0;
        ShortcutCommands.Register(new ShortcutCommand(id, "Find in Page Test", "Edit", Key("ctrl+alt+shift+f")), _ => found++);
        try
        {
            var palette = model.Productivity.Palette;
            palette.Present();
            palette.Query = "find in page test";
            var action = palette.Results.First();
            Assert.Equal($"command:{id}", action.Id);
            Assert.Equal("Ctrl+Alt+Shift+F", action.Shortcut?.DisplayString);
            palette.PerformSelection();
            Assert.Equal(1, found);
        }
        finally
        {
            ShortcutCommands.Unregister(id);
        }
    });

    [Fact]
    public void ARegisteredProviderThatThrowsDoesNotEmptyThePalette() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var id = $"test.{Guid.NewGuid():N}";
        PaletteRegistry.Register(id, _ => throw new InvalidOperationException("bug"));
        try
        {
            var palette = fixture.Model.Productivity.Palette;
            await fixture.Model.Productivity.StartAsync();
            palette.Present();
            Assert.Contains(palette.Results, a => a.Id == "tab.new");
        }
        finally
        {
            PaletteRegistry.Unregister(id);
        }
    });
}

[Collection(ProductivityStatics.Name)]
public class PaletteIntegrationTests
{
    [Fact]
    public void SearchProvidersAddLibraryResultsButNotPagesAlreadyOpen() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        fixture.OpenGeckoTab("https://open.example/");
        PaletteRegistry.RegisterSearch("test.library", (_, query) =>
        [
            new PaletteAction("bookmark:https://docs.example/", "Docs Example", () => model.OpenFromProductivity("https://docs.example/"))
                { Subtitle = "https://docs.example/", Kind = PaletteKind.Bookmark, Boost = 3 },
            new PaletteAction("history:https://open.example/", "Open Example", () => { })
                { Subtitle = "https://open.example/", Kind = PaletteKind.History },
        ]);
        try
        {
            var palette = model.Productivity.Palette;
            palette.Present();
            Assert.DoesNotContain(palette.Results, a => a.Kind == PaletteKind.Bookmark);  // only once something is typed
            palette.Query = "example";
            Assert.Contains(palette.Results, a => a.Id == "bookmark:https://docs.example/");
            Assert.DoesNotContain(palette.Results, a => a.Id == "history:https://open.example/");
        }
        finally
        {
            PaletteRegistry.Unregister("test.library");
        }
    });

    [Fact]
    public void SettingsCategoriesAppearOnceTheShellCanOpenThem() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        Mosaic.Core.Shell.SettingsCategory? opened = null;
        ProductivityIntegration.OpenSettings = (_, category) => opened = category;
        try
        {
            var palette = model.Productivity.Palette;
            palette.Present();
            palette.Query = "shortcuts settings";
            Assert.Equal("settings.shortcuts", palette.Results[0].Id);
            palette.PerformSelection();
            Assert.Equal(Mosaic.Core.Shell.SettingsCategory.Shortcuts, opened);
            palette.Present();
            palette.Query = "customise keyboard";
            Assert.Equal("shortcuts.customise", palette.Results[0].Id);
        }
        finally
        {
            ProductivityIntegration.OpenSettings = null;
        }
    });

    [Fact]
    public void CopyAddressUsesTheAppsClipboard() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        string? copied = null;
        ProductivityIntegration.CopyText = text => copied = text;
        try
        {
            fixture.OpenGeckoTab("https://example.com/page");
            var palette = model.Productivity.Palette;
            palette.Present();
            Assert.True(palette.Perform("page.copyAddress"));
            Assert.Equal("https://example.com/page", copied);
            Assert.Equal("Address copied", model.Notice);
        }
        finally
        {
            ProductivityIntegration.CopyText = null;
        }
    });
}
