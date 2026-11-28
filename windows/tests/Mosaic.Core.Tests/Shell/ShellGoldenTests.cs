using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mosaic.Core.Shell;

namespace Mosaic.Core.Tests.Shell;

/// <summary>
/// The JSON is byte-for-byte what macOS writes: the golden files in <c>Golden/</c> were produced
/// by the macOS sources (<c>Golden/generate.swift</c>).
/// </summary>
public sealed partial class ShellGoldenTests
{
    private static string GoldenDirectory([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Golden");

    internal static string Golden(string name) => File.ReadAllText(Path.Combine(GoldenDirectory(), name), Encoding.UTF8);

    [Theory]
    [InlineData(MosaicPresetID.ZenArc, "zenArc")]
    [InlineData(MosaicPresetID.Chrome, "chrome")]
    [InlineData(MosaicPresetID.Opera, "opera")]
    public void PresetsAreByteIdenticalToMacOS(MosaicPresetID preset, string name)
    {
        Assert.Equal(Golden($"preset-{name}.json"), MosaicLayout.Factory(preset).JsonText());
        Assert.Equal(Golden($"preset-{name}.compact.json"), MosaicLayout.Factory(preset).JsonText(pretty: false));
    }

    [Fact]
    public void GXStylingMatches() =>
        Assert.Equal(Golden("opera-gx.json"), MosaicLayout.Factory(MosaicPresetID.Opera).WithGXStyling().JsonText());

    [Fact]
    public void CustomisedLayoutMatches()
    {
        var c = MosaicLayout.Factory(MosaicPresetID.Chrome).WithTabPlacement(TabPlacement.SidebarTrailing);
        c.Appearance.Accent = "#12AB34";
        c.Appearance.CornerRadius = 12.5;
        c.Appearance.ContentInset = 0.1;
        c.Appearance.WorkspaceBackgrounds = new(StringComparer.Ordinal)
        {
            ["B"] = new BackgroundSpec(BackgroundKind.Solid, "#101010"),
            ["A"] = new BackgroundSpec(),
            ["a_"] = new BackgroundSpec(),
        };
        c.Name = "My \"Chrome\" / 1 é\U0001F600\t\u001b";
        Assert.Equal(Golden("chrome-custom.json"), c.Normalized().JsonText());
    }

    [Theory]
    [InlineData("zen-top.json", MosaicPresetID.ZenArc, TabPlacement.Top)]
    [InlineData("zen-trailing.json", MosaicPresetID.ZenArc, TabPlacement.SidebarTrailing)]
    [InlineData("chrome-vertical.json", MosaicPresetID.Chrome, TabPlacement.SidebarLeading)]
    [InlineData("opera-vertical.json", MosaicPresetID.Opera, TabPlacement.SidebarLeading)]
    public void TabPlacementChangesMatch(string file, MosaicPresetID preset, TabPlacement placement) =>
        Assert.Equal(Golden(file), MosaicLayout.Factory(preset).WithTabPlacement(placement).JsonText());

    [Fact]
    public void VerticalAndBackMatches() =>
        Assert.Equal(Golden("opera-vertical-top.json"), MosaicLayout.Factory(MosaicPresetID.Opera)
            .WithTabPlacement(TabPlacement.SidebarLeading).WithTabPlacement(TabPlacement.Top).JsonText());

    [Theory]
    [InlineData("messy")]
    [InlineData("empty")]
    [InlineData("wrongtypes")]
    [InlineData("future")]
    [InlineData("nullregions")]
    public void ForgivingDecodingMatches(string name)
    {
        var layout = MosaicLayout.FromJson(Golden($"{name}.input.json"));
        // Windows knows the trailing rail (Edge's sidebar) that macOS drops; otherwise identical.
        if (name == "future")
        {
            Assert.Equal([ShellComponent.Settings], layout.Components(ShellRegion.TrailingRail));
            layout.Regions.Remove(ShellRegion.TrailingRail);
        }
        var decoded = layout.JsonText();
        Assert.Equal(MaskUuids(Golden($"{name}.json")), MaskUuids(decoded));
    }

    [Theory]
    [MemberData(nameof(LayoutFiles))]
    public void EveryGoldenLayoutRoundTripsExactly(string file)
    {
        var text = Golden(file);
        var layout = MosaicLayout.FromJson(text);
        var pretty = !file.EndsWith(".compact.json", StringComparison.Ordinal);
        Assert.Equal(text, layout.JsonText(pretty));
    }

    public static TheoryData<string> LayoutFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(GoldenDirectory(), "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.Contains(".input.") || name.StartsWith("mosaics", StringComparison.Ordinal) || name.StartsWith("preset-safari", StringComparison.Ordinal)) continue;
            if (name is "messy.json") continue;  // holds a random id that re-normalising keeps, but compare masked elsewhere
            data.Add(name);
        }
        return data;
    }

    [Fact]
    public void SafariLayoutsBecomeEdge()
    {
        var layout = MosaicLayout.FromJson(Golden("preset-safari.json"));
        Assert.Equal(MosaicPresetID.Edge, layout.Preset);
        Assert.True(layout.ContentEquals(MosaicLayout.Factory(MosaicPresetID.Edge)));
        Assert.DoesNotContain("safari", layout.JsonText(), StringComparison.Ordinal);
    }

    [Fact]
    public void CustomisedSafariKeepsItsLook()
    {
        var node = JsonNode.Parse(Golden("preset-safari.json"))!.AsObject();
        node["tabPlacement"] = "sidebarLeading";
        node["bookmarksBar"] = true;
        node["appearance"]!["accent"] = "#FF0000";
        node["appearance"]!["colorScheme"] = "dark";
        node["appearance"]!["material"] = "solid";
        var layout = MosaicLayout.FromJson(node.ToJsonString());
        Assert.Equal(MosaicPresetID.Edge, layout.Preset);
        Assert.True(layout.TabPlacement.IsVertical());
        Assert.True(layout.BookmarksBar);
        Assert.Equal("#FF0000", layout.Appearance.Accent);
        Assert.Equal(ColorSchemePreference.Dark, layout.Appearance.ColorScheme);
        Assert.Equal(ChromeMaterial.Solid, layout.Appearance.Material);
        Assert.Equal(4, layout.Appearance.ContentInset);  // Edge's own page card
    }

    [Fact]
    public void StoreWritesTheMacOSFile()
    {
        // The macOS file plus Edge, which macOS keeps untouched as an unknown key... in the other
        // direction: Windows keeps macOS's Safari entry untouched.
        var folder = Directory.CreateTempSubdirectory("mosaic-shell-").FullName;
        var path = Path.Combine(folder, "mosaics.json");
        File.WriteAllText(path, Golden("mosaics.json"));
        var store = new MosaicStore(path);
        Assert.Equal(MosaicPresetID.Opera, store.ActivePreset);
        var expected = JsonNode.Parse(Golden("mosaics.json"))!.AsObject();
        expected["layouts"]!["edge"] = MosaicLayout.Factory(MosaicPresetID.Edge).ToJsonObject();
        Assert.Equal(SwiftJson.Write(expected), store.StateJson());
        // Without Edge (only the presets macOS knows), it is byte-identical.
        var macOnly = JsonNode.Parse(store.StateJson())!.AsObject();
        macOnly["layouts"]!.AsObject().Remove("edge");
        Assert.Equal(Golden("mosaics.json"), SwiftJson.Write(macOnly));
    }

    [Fact]
    public void ActiveSafariOpensAsEdgeWithItsLook()
    {
        var folder = Directory.CreateTempSubdirectory("mosaic-shell-").FullName;
        var path = Path.Combine(folder, "mosaics.json");
        File.WriteAllText(path, Golden("mosaics-safari.json"));
        var store = new MosaicStore(path);
        Assert.Equal(MosaicPresetID.Edge, store.ActivePreset);
        Assert.True(store.Layout.TabPlacement.IsVertical());
        // The Safari entry is written back as it was.
        var saved = JsonNode.Parse(store.StateJson())!["layouts"]!["safari"]!;
        Assert.Equal(SwiftJson.Write(JsonNode.Parse(Golden("mosaics-safari.json"))!["layouts"]!["safari"]), SwiftJson.Write(saved));
    }

    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(12.5, "12.5")]
    [InlineData(1e-5, "1e-05")]
    [InlineData(1e20, "1e+20")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000")]
    [InlineData(123456789012345680.0, "1.2345678901234568e+17")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(248.0, "248")]
    [InlineData(-0.0, "-0")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(2.5e-7, "2.5e-07")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1234567890123456.7, "1234567890123456.8")]
    [InlineData(1.5e16, "1.5e+16")]
    [InlineData(9007199254740993.0, "9007199254740992")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(99999999999999990.0, "9.999999999999998e+16")]
    public void NumbersAreWrittenAsSwiftWritesThem(double value, string expected) =>
        Assert.Equal(expected, SwiftJson.FormatNumber(value));

    [Fact]
    public void KeysSortByUtf8Bytes()
    {
        var o = new JsonObject { ["a"] = 1, ["B"] = 2, ["b10"] = 3, ["b9"] = 4, ["_x"] = 5, ["Z"] = 6, ["é"] = 7, ["ab"] = 8, ["a_"] = 9 };
        Assert.Equal("{\"B\":2,\"Z\":6,\"_x\":5,\"a\":1,\"a_\":9,\"ab\":8,\"b10\":3,\"b9\":4,\"é\":7}", SwiftJson.Write(o, pretty: false));
    }

    private static string MaskUuids(string text) => UuidPattern().Replace(text, "UUID");

    [GeneratedRegex("[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}")]
    private static partial Regex UuidPattern();
}
