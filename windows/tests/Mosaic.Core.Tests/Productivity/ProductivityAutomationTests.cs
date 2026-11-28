using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Mosaic.Core.Tests.Browser;
using static Mosaic.Core.Tests.Productivity.ProductivityTestSupport;

namespace Mosaic.Core.Tests.Productivity;

public class ProductivityAutomationTests
{
    /// <summary>
    /// Runs a command through the area's handler. (Not through <see cref="AutomationRegistry"/>: it is
    /// process-wide, and registering there would add a "productivity" key to other areas' dump tests.)
    /// </summary>
    private sealed class Harness(BrowserModel model)
    {
        public async Task<JsonObject> Run(string command, string value = "")
        {
            if (command == "dump")
            {
                return new JsonObject { ["ok"] = true, ["state"] = new JsonObject { ["productivity"] = ProductivityAutomation.State(model) } };
            }
            var reply = await ProductivityAutomation.HandleAsync(model, command, value, null);
            Assert.NotNull(reply);
            return (JsonObject)JsonNode.Parse(reply.Json)!;
        }
    }

    [Fact]
    public void PaletteCommandsAndDump() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var automation = new Harness(fixture.Model);

        Assert.True((bool)(await automation.Run("palette", "open"))["presented"]!);
        var query = await automation.Run("paletteQuery", "next search");
        Assert.Equal("search.next", query["results"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("Ctrl+Shift+S", query["results"]![0]!["shortcut"]!.GetValue<string>());

        var before = fixture.Model.SearchEngine;
        Assert.True((bool)(await automation.Run("palettePerform", "selection"))["ok"]!);
        Assert.NotEqual(before, fixture.Model.SearchEngine);
        Assert.False((bool)(await automation.Run("palettePerform", "no.such.action"))["ok"]!);

        var dump = await automation.Run("dump");
        var state = (JsonObject)dump["state"]!["productivity"]!;
        Assert.True((bool)state["started"]!);
        Assert.False((bool)state["palette"]!["presented"]!);
        Assert.Contains(state["shortcuts"]!.AsArray(), s => s!["id"]!.GetValue<string>() == "View/Reload Page"
            && s["default"]!.GetValue<string>() == "Ctrl+R, F5");
        Assert.Contains(state["geckoForwarding"]!.AsArray(), p => p![0]!.GetValue<string>() == "accel+k");
        Assert.Contains("mosaic.builtin", state["paletteProviders"]!.AsArray().Select(n => n!.GetValue<string>()));
    });

    [Fact]
    public void ShortcutCommands() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var automation = new Harness(fixture.Model);

        Assert.Equal("conflict: File/New Tab", (await automation.Run("setShortcut", "Tools/Command Palette…=ctrl+t"))["result"]!.GetValue<string>());
        Assert.Equal("ok, replaced File/New Tab", (await automation.Run("setShortcut", "Tools/Command Palette…=ctrl+t!"))["result"]!.GetValue<string>());
        Assert.Equal("reserved by Windows for closing the window", (await automation.Run("setShortcut", "View/Stop=alt+f4"))["result"]!.GetValue<string>());
        Assert.Equal("needs a modifier", (await automation.Run("setShortcut", "View/Stop=s"))["result"]!.GetValue<string>());
        Assert.Equal("ok", (await automation.Run("setShortcut", "View/Stop=ctrl+="))["result"]!.GetValue<string>());
        Assert.Equal("Ctrl+Plus", fixture.Model.Productivity.Shortcuts.Combo("View/Stop")?.DisplayString);
        Assert.Equal("cleared", (await automation.Run("setShortcut", "View/Stop="))["result"]!.GetValue<string>());
        Assert.Equal("no command Nope", (await automation.Run("setShortcut", "Nope=ctrl+j"))["result"]!.GetValue<string>());

        Assert.Equal("ok", (await automation.Run("performShortcut", "key:ctrl+t"))["result"]!.GetValue<string>());
        Assert.True(fixture.Model.Productivity.Palette.IsPresented);
        Assert.True((bool)(await automation.Run("resetShortcut", "all"))["ok"]!);
        Assert.Equal("ok", (await automation.Run("performShortcut", "File/New Tab"))["result"]!.GetValue<string>());
        Assert.Equal(2, fixture.Model.Tabs.Count);
        Assert.False((bool)(await automation.Run("resetShortcut", "Nope"))["ok"]!);
    });

    [Fact]
    public void ReaderAndSpeedDialCommands() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var automation = new Harness(fixture.Model);

        var settings = await automation.Run("readerSettings", """{"fontSize":7,"theme":"sepia"}""");
        Assert.Equal(7, settings["settings"]!["fontSize"]!.GetValue<int>());
        Assert.Equal(ReaderTheme.Sepia, fixture.Model.Productivity.Reader.Settings.Theme);
        Assert.False((bool)(await automation.Run("readerSettings", "nope"))["ok"]!);

        Assert.Equal("ok", (await automation.Run("speedDial", """{"op":"add","title":"A","url":"a.com"}"""))["result"]!.GetValue<string>());
        Assert.Equal("ok", (await automation.Run("speedDial", """{"op":"add","title":"B","url":"b.com"}"""))["result"]!.GetValue<string>());
        Assert.Equal("That site is already in Speed Dial.", (await automation.Run("speedDial", """{"op":"add","url":"https://www.a.com"}"""))["result"]!.GetValue<string>());
        Assert.Equal("ok", (await automation.Run("speedDial", """{"op":"move","url":"b.com","to":0}"""))["result"]!.GetValue<string>());
        Assert.Equal("ok", (await automation.Run("speedDial", """{"op":"edit","url":"a.com","title":"Alpha","newURL":"alpha.com"}"""))["result"]!.GetValue<string>());
        Assert.Equal("ok", (await automation.Run("speedDial", """{"op":"hide","url":"https://top.example/"}"""))["result"]!.GetValue<string>());
        Assert.Equal("ok", (await automation.Run("speedDial", """{"op":"showIcons","value":false}"""))["result"]!.GetValue<string>());
        Assert.Equal("not pinned", (await automation.Run("speedDial", """{"op":"remove","url":"zzz.com"}"""))["result"]!.GetValue<string>());
        Assert.Equal("unknown op dance", (await automation.Run("speedDial", """{"op":"dance"}"""))["result"]!.GetValue<string>());

        var dial = (await automation.Run("dump"))["state"]!["productivity"]!["speedDial"]!;
        Assert.Equal(["B", "Alpha"], dial["pinned"]!.AsArray().Select(s => s!["title"]!.GetValue<string>()));
        Assert.Equal("https://alpha.com/", dial["pinned"]![1]!["url"]!.GetValue<string>());
        Assert.Equal(["top.example"], dial["hiddenTopSites"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.False((bool)dial["showsIcons"]!);

        await fixture.Model.Productivity.Files.Flush();
        var saved = SpeedDial.FromJson(File.ReadAllText(fixture.Model.Productivity.Files.PathOf(ProductivityFiles.SpeedDialFile)))!;
        Assert.Equal(["B", "Alpha"], saved.Pinned.Select(s => s.Title));
    });

    [Fact]
    public void ReaderCommandTogglesTheSelectedTab() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var automation = new Harness(fixture.Model);
        await fixture.Model.Productivity.StartAsync();
        Assert.Null(await ProductivityAutomation.HandleAsync(fixture.Model, "navigate", "x", null));
        var tab = fixture.OpenGeckoTab("https://news.example/story");
        Assert.True((bool)(await automation.Run("reader", "on"))["ok"]!);
        await TestLoop.Settle();
        Assert.Equal([tab.GeckoId!.Value, true], fixture.Gecko.CallsTo("setReaderMode").Single());
        var reader = (await automation.Run("dump"))["state"]!["productivity"]!["reader"]!;
        Assert.Equal("gecko", reader["tabs"]![1]!["engine"]!.GetValue<string>());
    });
}
