using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Shell;

public sealed class ShellAutomationTests
{
    private static async Task<JsonObject> Run(Automation automation, string command, string value = "")
    {
        var line = new JsonObject { ["do"] = command, ["value"] = value }.ToJsonString();
        return JsonNode.Parse((await automation.HandleAsync(line)).Json)!.AsObject();
    }

    private static (BrowserFixture Fixture, Automation Automation) Make()
    {
        ShellAutomation.Register();
        var fixture = new BrowserFixture();
        fixture.Model.ShellInMemory = true;
        return (fixture, new Automation(fixture.Model, _ => Task.CompletedTask));
    }

    [Fact]
    public void MosaicCommandsAndDump() => TestLoop.Run(async () =>
    {
        var (fixture, automation) = Make();
        using var _ = fixture;
        Assert.True((bool)(await Run(automation, "mosaic", "edge"))["ok"]!);
        var state = automation.DumpState();
        Assert.Equal("edge", (string)state["mosaic"]!);
        Assert.Equal("edge", (string)state["layout"]!["preset"]!);
        Assert.Equal("Mosaic: Microsoft Edge", fixture.Model.Notice);

        Assert.True((bool)(await Run(automation, "verticalTabs", "on"))["ok"]!);
        Assert.Equal("sidebarLeading", (string)automation.DumpState()["layout"]!["tabPlacement"]!);
        Assert.Equal("toolbar", (string)automation.DumpState()["shell"]!["captionHost"]!);

        Assert.True((bool)(await Run(automation, "mosaic", "operaGX"))["ok"]!);
        Assert.True((bool)automation.DumpState()["shell"]!["gx"]!);
        Assert.True((bool)(await Run(automation, "mosaic", "safari"))["ok"]!);
        Assert.Equal("edge", (string)automation.DumpState()["mosaic"]!);
        Assert.False((bool)(await Run(automation, "mosaic", "netscape"))["ok"]!);

        Assert.True((bool)(await Run(automation, "mosaicSet", "materials=off"))["ok"]!);
        Assert.Equal("solid", (string)automation.DumpState()["layout"]!["appearance"]!["material"]!);
        Assert.False((bool)(await Run(automation, "mosaicSet", "bogus=1"))["ok"]!);
    });

    [Fact]
    public void SettingsCommands() => TestLoop.Run(async () =>
    {
        var (fixture, automation) = Make();
        using var _ = fixture;
        Assert.True((bool)(await Run(automation, "openSettings", "accessibility"))["ok"]!);
        Assert.Equal("accessibility", (string)automation.DumpState()["settingsCategory"]!);
        Assert.False((bool)(await Run(automation, "openSettings", "appearance"))["ok"]!);
        await Run(automation, "settingsSearch", "mica");
        var matches = automation.DumpState()["shell"]!["settingsMatches"]!.AsArray();
        Assert.Equal("mosaics", (string)Assert.Single(matches)!["id"]!);
        await Run(automation, "closeSettings");
        Assert.Equal("", (string)automation.DumpState()["settingsCategory"]!);
        Assert.Equal("", (string)automation.DumpState()["shell"]!["settingsQuery"]!);
    });

    [Fact]
    public void ImportAndExportUseTheMacOSFormat() => TestLoop.Run(async () =>
    {
        var (fixture, automation) = Make();
        using var _ = fixture;
        var source = fixture.PathFor("in.json");
        File.WriteAllText(source, ShellGoldenTests.Golden("preset-chrome.json"));
        Assert.True((bool)(await Run(automation, "importLayout", source))["ok"]!);
        Assert.Equal("chrome", (string)automation.DumpState()["mosaic"]!);
        var target = fixture.PathFor("out.json");
        Assert.True((bool)(await Run(automation, "exportLayout", target))["ok"]!);
        Assert.Equal(ShellGoldenTests.Golden("preset-chrome.json"), File.ReadAllText(target));
        File.WriteAllText(source, "nope");
        Assert.False((bool)(await Run(automation, "importLayout", source))["ok"]!);
    });

    [Fact]
    public void SidebarAndShellSettings() => TestLoop.Run(async () =>
    {
        var (fixture, automation) = Make();
        using var _ = fixture;
        Assert.True(fixture.Model.IsSidebarVisible);  // Zen / Arc
        await Run(automation, "sidebar", "hide");
        Assert.False((bool)automation.DumpState()["sidebarVisible"]!);
        await Run(automation, "sidebar", "toggle");
        Assert.True(fixture.Model.IsSidebarVisible);
        Assert.True((bool)(await Run(automation, "shellSet", "textSize=large"))["ok"]!);
        Assert.Equal("large", (string)automation.DumpState()["shell"]!["settings"]!["textSize"]!);
        Assert.False((bool)(await Run(automation, "shellSet", "nope=1"))["ok"]!);
    });

    [Fact]
    public void InternalPages()
    {
        Assert.Equal("bookmarks", BrowserModel.InternalHost("mosaic://bookmarks"));
        Assert.Equal("settings", BrowserModel.InternalHost("MOSAIC://Settings/privacy"));
        Assert.Null(BrowserModel.InternalHost("https://example.com"));
        Assert.Null(BrowserModel.InternalHost("mosaic://"));
    }
}
