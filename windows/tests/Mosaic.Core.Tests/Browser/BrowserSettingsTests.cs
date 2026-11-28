using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Tests.Browser;

public sealed class BrowserSettingsTests : IDisposable
{
    private readonly string directory =
        Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(directory, "nested", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void MissingFileMeansDefaults()
    {
        var settings = new BrowserSettings(SettingsPath);

        Assert.Null(settings.SearchEngineId);
        Assert.Null(settings.Engine);
        Assert.Empty(settings.ChromiumExtensionIds);
        // Reading never creates anything.
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void EverythingSurvivesARoundTrip()
    {
        var settings = new BrowserSettings(SettingsPath) { SearchEngineId = "brave", Engine = EngineKind.Chromium };
        settings.SetChromiumExtensionId("aaaa", "chromium-id-1");
        settings.SetChromiumExtensionId("bbbb", "chromium-id-2");
        settings.SetChromiumExtensionId("bbbb", null);
        settings.Save();

        var reread = new BrowserSettings(SettingsPath);

        Assert.Equal("brave", reread.SearchEngineId);
        Assert.Equal(EngineKind.Chromium, reread.Engine);
        Assert.Equal("chromium-id-1", Assert.Single(reread.ChromiumExtensionIds).Value);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void FileIsPlainJsonWithStableKeys()
    {
        new BrowserSettings(SettingsPath) { SearchEngineId = "duckduckgo", Engine = EngineKind.Gecko }.Save();

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();

        Assert.Equal("duckduckgo", (string?)json["searchEngine"]);
        Assert.Equal("gecko", (string?)json["engine"]);
        Assert.Equal(2, json.Count);
    }

    [Fact]
    public void SavingAgainReplacesTheFile()
    {
        var settings = new BrowserSettings(SettingsPath) { Engine = EngineKind.Chromium };
        settings.Save();
        settings.Engine = EngineKind.Gecko;
        settings.Save();

        Assert.Equal(EngineKind.Gecko, new BrowserSettings(SettingsPath).Engine);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{"engine": 7, "searchEngine": ["google"], "chromiumExtensions": "none"}""")]
    [InlineData("""{"engine": "servo"}""")]
    public void DamagedOrUnexpectedContentMeansDefaults(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, content);

        var settings = new BrowserSettings(SettingsPath);

        Assert.Null(settings.SearchEngineId);
        Assert.Null(settings.Engine);
        Assert.Empty(settings.ChromiumExtensionIds);
    }

    [Fact]
    public void LocationThatCannotBeWrittenIsNotAnError()
    {
        // The parent "folder" is a file, so the settings file can never be created.
        Directory.CreateDirectory(directory);
        var blocker = Path.Combine(directory, "blocker");
        File.WriteAllText(blocker, "");
        var settings = new BrowserSettings(Path.Combine(blocker, "settings.json")) { Engine = EngineKind.Chromium };

        settings.Save();

        Assert.Equal(EngineKind.Chromium, settings.Engine);
    }

    [Fact]
    public void EngineIdsRoundTrip()
    {
        foreach (var kind in Enum.GetValues<EngineKind>())
        {
            Assert.Equal(kind, EngineKindExtensions.FromId(kind.Id()));
            Assert.NotEqual(kind, kind.Other());
            Assert.Equal(kind, kind.Other().Other());
        }
        Assert.Null(EngineKindExtensions.FromId("Gecko"));
        Assert.Null(EngineKindExtensions.FromId(null));
        Assert.Equal("Mozilla", EngineKind.Gecko.DisplayName());
        Assert.Equal("Blink", EngineKind.Chromium.EngineName());
    }

    [Fact]
    public void WindowPlacementSurvivesARoundTrip()
    {
        new BrowserSettings(SettingsPath) { WindowPlacement = new WindowPlacement(-1200, 40, 1366, 740, Maximized: true) }.Save();

        var reread = new BrowserSettings(SettingsPath);

        Assert.Equal(new WindowPlacement(-1200, 40, 1366, 740, true), reread.WindowPlacement);
        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!["windowPlacement"]!.AsObject();
        Assert.Equal(1366, (int)json["width"]!);
        Assert.True((bool)json["maximized"]!);
    }

    [Theory]
    [InlineData("""{"windowPlacement": {"x": 0, "y": 0, "width": 0, "height": 600}}""")]
    [InlineData("""{"windowPlacement": {"x": 0, "y": 0, "width": 800}}""")]
    [InlineData("""{"windowPlacement": "800x600"}""")]
    public void ADamagedWindowPlacementIsIgnored(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, file);

        Assert.Null(new BrowserSettings(SettingsPath).WindowPlacement);
    }

    [Fact]
    public void KeysThisVersionDoesNotKnowAreKept()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{"engine": "gecko", "someArea": {"setting": 3}, "searchEngine": "bing"}""");

        var settings = new BrowserSettings(SettingsPath) { SearchEngineId = null, Engine = EngineKind.Chromium };
        settings.Save();

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal(3, (int)json["someArea"]!["setting"]!);
        Assert.Equal("chromium", (string?)json["engine"]);
        // A known key that was cleared goes, rather than surviving from the old file.
        Assert.False(json.ContainsKey("searchEngine"));
    }
}
