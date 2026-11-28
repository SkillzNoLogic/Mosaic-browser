using System.Text;
using System.Text.Json.Nodes;
using Mosaic.Core.Canvas;
using Mosaic.Core.Canvas.Interim;

namespace Mosaic.Core.Tests.Canvas;

public class CanvasConfigurationTests
{
    private static MosaicLayout P(MosaicPresetID id) => MosaicLayout.Factory(id);

    [Fact]
    public void SaveUniqueNamesRenameDeleteActive()
    {
        var lib = new ConfigurationLibrary();
        var a = lib.Save("  Work  ", P(MosaicPresetID.Chrome))!;
        Assert.Equal("Work", a.Name);
        Assert.Equal("Work", a.Layout.Name);
        Assert.Equal(a.Id, lib.ActiveID);
        var b = lib.Save("work", P(MosaicPresetID.ZenArc))!;
        Assert.Equal("work 2", b.Name);
        Assert.True(lib.Rename(b.Id, "Work"));
        Assert.Equal("Work 2", lib.Configuration(b.Id)?.Name);
        Assert.Equal("Work 2", lib.Configuration(b.Id)?.Layout.Name);
        Assert.False(lib.Rename(b.Id, "   "));
        Assert.Equal(a.Id, lib.Find("WORK")?.Id);
        Assert.Equal(b.Id, lib.ActiveID);
        lib.ActiveID = a.Id;
        Assert.True(lib.Delete(b.Id));
        Assert.Equal(a.Id, lib.ActiveID);
        Assert.True(lib.Delete(a.Id));
        Assert.Null(lib.ActiveID);
        Assert.Equal(ConfigurationLibrary.MaxNameLength, ConfigurationLibrary.CleanName(new string('x', 500))?.Length);
        Assert.Equal("ab", ConfigurationLibrary.CleanName("a\u0007b​"));
        Assert.Null(ConfigurationLibrary.CleanName(" \n\t "));
    }

    [Fact]
    public void TheLibrarySurvivesAWriteLoadRoundTripAndADamagedEntry()
    {
        var lib = new ConfigurationLibrary();
        lib.Save("One", P(MosaicPresetID.Opera));
        lib.Save("Two", P(MosaicPresetID.Edge));
        var dir = Path.Combine(Path.GetTempPath(), "canvas-" + Guid.NewGuid());
        var path = Path.Combine(dir, "configurations.json");
        try
        {
            ConfigurationLibrary.WriteFile(path, lib.ToJson());
            var loaded = ConfigurationLibrary.Load(path);
            Assert.Equal(["One", "Two"], loaded.Configurations.Select(c => c.Name));
            Assert.Equal(lib.Configurations.Select(c => c.Layout.JsonText()), loaded.Configurations.Select(c => c.Layout.JsonText()));
            Assert.Equal(lib.Configurations.Select(c => c.Created), loaded.Configurations.Select(c => c.Created));

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["configurations"]![0]!["layout"] = "garbage";
            File.WriteAllText(path, json.ToJsonString());
            Assert.Equal(["Two"], ConfigurationLibrary.Load(path).Configurations.Select(c => c.Name));
            File.WriteAllText(path, "{{{");
            Assert.Empty(ConfigurationLibrary.Load(path).Configurations);
            Assert.Empty(ConfigurationLibrary.Load(Path.Combine(dir, "missing.json")).Configurations);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ExportThenImportGivesTheSameLayoutWithNoMessages()
    {
        var layout = P(MosaicPresetID.Opera).WithGXStyling();
        layout.Appearance.Background.Gradient = GradientPresets.Named("Mesh")!;
        var data = LayoutTransfer.Export("GX Mesh", layout);
        var report = LayoutTransfer.ImportLayout(data).Report!;
        Assert.Equal("GX Mesh", report.Name);
        Assert.Empty(report.Messages);
        var expected = LayoutCanvasRules.Usable(layout);
        expected.Name = "GX Mesh";
        CanvasFixtures.AssertSameLayout(expected, report.Layout);
    }

    [Fact]
    public void ABareLayoutImports()
    {
        var report = LayoutTransfer.ImportLayout(P(MosaicPresetID.Edge).JsonText()).Report!;
        CanvasFixtures.AssertSameLayout(P(MosaicPresetID.Edge), report.Layout);
        Assert.Empty(report.Messages);
    }

    [Fact]
    public void UnknownComponentsAndRegionsAreDroppedAndReportedAndValuesClamped()
    {
        const string json = """
            {"format":"dev.mosaic.layout","version":99,"name":"Odd\u0007 one","layout":{
              "preset":"chrome","tabPlacement":"top","sidebarWidth":5000,
              "regions":{"titleBar":["tabStrip","teleporter","newTab"],"toolbar":["back","back","addressField",42],
                         "basement":["home"],"rail":["addressField"]},
              "appearance":{"cornerRadius":-4,"accent":"blue","background":{"kind":"gradient","gradient":{"kind":"mesh","sharpness":50,"sources":[{"x":9,"y":0,"color":"#FF0000"}]}}}}}
            """;
        var report = LayoutTransfer.ImportLayout(json).Report!;
        var all = string.Join("\n", report.Messages);
        Assert.Equal("Odd one", report.Name);
        foreach (var word in new[] { "teleporter", "basement", "newer Mosaic", "sidebar width", "corner radius", "accent", "duplicates", "weren’t lists", "background", "Settings" })
            Assert.Contains(word, all);
        var layout = report.Layout;
        Assert.Equal(420, layout.SidebarWidth);
        Assert.Equal(0, layout.Appearance.CornerRadius);
        Assert.Equal(6, layout.Appearance.Background.Gradient.Sharpness);
        Assert.Equal(1, layout.Appearance.Background.Gradient.Sources[0].X);
        Assert.Single(layout.Components(ShellRegion.Toolbar), c => c == ShellComponent.Back);
        Assert.Empty(layout.Components(ShellRegion.Rail));
        Assert.True(LayoutCanvasRules.HasSettingsAccess(layout));
    }

    [Fact]
    public void MalformedFilesAreRefusedWithAReasonNeverACrash()
    {
        LayoutTransfer.ImportErrorKind? Error(string text) => LayoutTransfer.ImportLayout(text).Error?.Kind;
        Assert.Equal(LayoutTransfer.ImportErrorKind.Empty, Error(""));
        Assert.Equal(LayoutTransfer.ImportErrorKind.NotJSON, Error("{\"preset\": "));
        Assert.Equal(LayoutTransfer.ImportErrorKind.NotALayout, Error("[1,2,3]"));
        Assert.Equal(LayoutTransfer.ImportErrorKind.NotALayout, Error("\"hello\""));
        Assert.Equal(LayoutTransfer.ImportErrorKind.NotALayout, Error("{\"recipe\":\"soup\"}"));
        Assert.Equal(LayoutTransfer.ImportErrorKind.NotALayout, Error("{\"layout\": 7}"));
        var big = LayoutTransfer.ImportLayout(new byte[LayoutTransfer.MaxFileSize + 1]).Error!;
        Assert.Equal(LayoutTransfer.ImportErrorKind.TooLarge, big.Kind);
        Assert.Equal(LayoutTransfer.MaxFileSize + 1, big.Bytes);
        var bytes = new byte[4096];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 7919) & 0xFF);
        Assert.NotNull(LayoutTransfer.ImportLayout(bytes).Error);
        var weird = LayoutTransfer.ImportLayout("""{"preset":7,"regions":"none","tabPlacement":["top"],"appearance":[]}""").Report!;
        Assert.True(weird.Layout.Contains(ShellComponent.AddressField));
        Assert.True(LayoutCanvasRules.HasSettingsAccess(weird.Layout));
    }

    // MARK: Files moving between macOS and Windows

    [Theory]
    [InlineData("zenArc")]
    [InlineData("chrome")]
    [InlineData("opera")]
    [InlineData("safari")]
    [InlineData("custom")]
    public void MacOSExportsImportCleanlyAndWindowsWritesTheSameFile(string name)
    {
        var text = CanvasFixtures.Text($"macos-export-{name}.json");
        var report = LayoutTransfer.ImportLayout(text).Report!;
        var file = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(file["name"]!.GetValue<string>(), report.Name);
        if (name == "safari")
        {
            // Windows has no Safari Mosaic: the shell's model reads it as Microsoft Edge, look kept; the import says so.
            Assert.Equal(["A Safari layout from a Mac: Windows shows it as Microsoft Edge, keeping its colours and backgrounds."], report.Messages);
            Assert.Equal(MosaicPresetID.Edge, report.Layout.Preset);
            return;
        }
        Assert.Empty(report.Messages);
        if (name != "custom")
        {
            var preset = JsonEnum.Parse<MosaicPresetID>(name)!.Value;
            var expected = MosaicLayout.Factory(preset);
            expected.Name = preset.Title();
            CanvasFixtures.AssertSameLayout(expected, report.Layout);
        }
        // Windows' export of the same layout at the same moment is the same JSON document.
        var exported = Iso8601.Read(file["exported"]!.GetValue<string>())!.Value;
        var windows = LayoutTransfer.Export(report.Name, report.Layout, exported);
        CanvasFixtures.AssertSameJson(file, JsonNode.Parse(windows), $"export {name}");
        Assert.Equal(text, windows);  // byte for byte, as macOS's JSONEncoder writes it
    }

    [Fact]
    public void TheCustomMacOSExportKeepsEveryBackground()
    {
        var layout = LayoutTransfer.ImportLayout(CanvasFixtures.Text("macos-export-custom.json")).Report!.Layout;
        var a = layout.Appearance;
        Assert.Equal(GradientKind.Mesh, a.Background.Gradient.Kind);
        Assert.Equal(4, a.Background.Gradient.Sources.Count);
        Assert.Equal(0.35, a.Background.Gradient.Noise);
        Assert.True(a.Background.Gradient.Raw);
        Assert.Equal(NewTabBackgroundMode.Custom, a.NewTabBackground.Mode);
        Assert.Equal(GradientKind.Radial, a.NewTabBackground.Custom.Gradient.Kind);
        Assert.Equal(0.3, a.NewTabBackground.Custom.Gradient.CenterX);
        Assert.Equal(2, a.WorkspaceBackgrounds.Count);
        Assert.Equal("#336699", a.BackgroundForWorkspace("A0B1C2D3-0000-4000-8000-000000000001").Color);
        Assert.Contains(ShellComponent.Settings, layout.Components(ShellRegion.Toolbar));
        Assert.True(layout.HasGXStyling);
    }

    [Fact]
    public void TheMacOSConfigurationLibraryLoadsAndIsWrittenBackIdentically()
    {
        var text = CanvasFixtures.Text("macos-configurations.json");
        var lib = ConfigurationLibrary.FromJson(text);
        Assert.Equal(["Work", "GX Aurora", "Calm"], lib.Configurations.Select(c => c.Name));
        Assert.Equal("66666666-7777-4888-9999-AAAAAAAAAAAA", lib.ActiveID);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 14, 13, 20, TimeSpan.Zero), lib.Configurations[0].Created);
        Assert.Equal(lib.Configurations[0].Created.AddHours(1), lib.Configurations[0].Modified);
        Assert.Equal(GradientKind.Mesh, lib.Find("gx aurora")!.Layout.Appearance.Background.Gradient.Kind);
        CanvasFixtures.AssertSameJson(JsonNode.Parse(text), JsonNode.Parse(lib.ToJson()), "configurations.json");
        Assert.Equal(text, lib.ToJson());  // byte for byte
        // Dates are written the way macOS's decoder demands (no fractional seconds).
        Assert.Contains("\"created\" : \"2026-09-21T14:13:20Z\"", lib.ToJson());
    }

    [Fact]
    public void ABareMacOSLayoutImports()
    {
        var report = LayoutTransfer.ImportLayout(CanvasFixtures.Text("macos-bare-zenArc.json")).Report!;
        Assert.Empty(report.Messages);
        CanvasFixtures.AssertSameLayout(P(MosaicPresetID.ZenArc), report.Layout);
    }

    [Fact]
    public void RepairsAndRefusalsMatchTheMacOSApp()
    {
        foreach (var (name, expected) in CanvasFixtures.Expectations["imports"]!.AsObject())
        {
            var result = LayoutTransfer.ImportLayout(expected!["input"]!.GetValue<string>());
            if (name == "edge")
            {
                // macOS doesn't know the Edge preset (Windows only); Windows reads it as Edge.
                Assert.Equal(MosaicPresetID.Edge, result.Report!.Layout.Preset);
                Assert.DoesNotContain(result.Report.Messages, m => m.Contains("Unknown preset"));
                continue;
            }
            if (expected["error"] is { } error)
            {
                Assert.True(result.Error is not null, $"{name}: expected an error");
                Assert.Equal(expected["description"]!.GetValue<string>(), result.Error!.Description);
                continue;
            }
            Assert.True(result.Report is not null, $"{name}: {result.Error?.Description}");
            Assert.Equal(expected["name"]!.GetValue<string>(), result.Report!.Name);
            Assert.Equal(expected["messages"]!.AsArray().Select(m => m!.GetValue<string>()), result.Report.Messages);
            // Sources without an ID get a fresh random one on both platforms.
            CanvasFixtures.AssertSameJson(WithoutSourceIds(expected["layout"]!.DeepClone()), WithoutSourceIds(result.Report.Layout.ToJsonObject()), $"import {name}");
        }
    }

    private static JsonNode WithoutSourceIds(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (o["sources"] is JsonArray sources) foreach (var source in sources) (source as JsonObject)?.Remove("id");
            foreach (var (_, value) in o.ToList()) if (value is not null) WithoutSourceIds(value);
        }
        else if (node is JsonArray a)
        {
            foreach (var item in a) if (item is not null) WithoutSourceIds(item);
        }
        return node;
    }

    [Fact]
    public void ExportedTextIsUtf8WithReadableCharacters()
    {
        var layout = P(MosaicPresetID.Chrome);
        var text = LayoutTransfer.Export("Café “Mosaic”", layout, DateTimeOffset.UnixEpoch);
        Assert.Contains("Café “Mosaic”", text);
        Assert.Contains("\"exported\" : \"1970-01-01T00:00:00Z\"", text);
        Assert.Equal("Café “Mosaic”", LayoutTransfer.ImportLayout(Encoding.UTF8.GetBytes(text)).Report!.Name);
    }
}
