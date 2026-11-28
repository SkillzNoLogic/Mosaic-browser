using System.Text.Json.Nodes;
using Mosaic.Core.Canvas;
using Mosaic.Core.Canvas.Interim;

namespace Mosaic.Core.Tests.Canvas;

public class CanvasColorTests
{
    [Fact]
    public void EveryEncodingRoundTripsAColour()
    {
        string[] samples = ["#FF7E5F", "#2C73D2", "#000000", "#FFFFFF", "#808080", "#13A35B", "#FDFCFB"];
        foreach (var hex in samples)
        {
            var color = ColorValue.FromHex(hex)!.Value;
            foreach (var encoding in Enum.GetValues<ColorEncoding>().Where(e => e != ColorEncoding.Hex))
            {
                Assert.Equal(hex, encoding.Color(encoding.Values(color)).Hex);
            }
        }
    }

    [Fact]
    public void HexParsingAcceptsShortLongAndAlphaFormsAndRejectsJunk()
    {
        Assert.Equal("#AABBCC", ColorValue.FromHex("#abc")?.Hex);
        Assert.Equal("#112233", ColorValue.FromHex("112233")?.Hex);
        Assert.Equal("#112233", ColorValue.FromHex("#11223344")?.Hex);
        Assert.Null(ColorValue.FromHex("#12345G"));
        Assert.Null(ColorValue.FromHex(""));
        Assert.Null(ColorValue.FromHex("#-12345"));
    }

    [Fact]
    public void TypedComponentValuesAreClampedAndUnreadableOnesKeepTheCurrentValue()
    {
        var current = ColorValue.FromHex("#336699")!.Value;
        Assert.Equal("#FF6600", ColorEncoding.Rgb.Color(["300", "abc", "-5"], current).Hex);
        Assert.Equal("#00FF00", ColorEncoding.Hsl.Color(["120", "100", "50"], current).Hex);
    }

    [Fact]
    public void CssStyleStrings()
    {
        var c = ColorValue.FromHex("#FF0000")!.Value;
        Assert.Equal("rgb(255, 0, 0)", ColorEncoding.Rgb.String(c));
        Assert.Equal("hsl(0 100% 50%)", ColorEncoding.Hsl.String(c));
        Assert.Equal("#FF0000", ColorEncoding.Hex.String(c));
    }

    [Fact]
    public void ConversionsMatchTheMacOSApp()
    {
        foreach (var entry in CanvasFixtures.Expectations["colors"]!.AsArray())
        {
            var hex = entry!["hex"]!.GetValue<string>();
            var color = ColorValue.FromHex(hex)!.Value;
            foreach (var encoding in Enum.GetValues<ColorEncoding>().Where(e => e != ColorEncoding.Hex))
            {
                var expected = entry[encoding.Raw()]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                var actual = encoding.Values(color);
                Assert.Equal(expected.Length, actual.Length);
                for (var i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) < 1e-9, $"{hex} {encoding} [{i}]: {expected[i]} vs {actual[i]}");
                Assert.Equal(entry[encoding.Raw() + "-string"]!.GetValue<string>(), encoding.String(color));
            }
            var ok = entry["oklab"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
            Assert.True(Math.Abs(ok[0] - color.Oklab.L) < 1e-12 && Math.Abs(ok[1] - color.Oklab.A) < 1e-12 && Math.Abs(ok[2] - color.Oklab.B) < 1e-12);
        }
    }
}

public class CanvasGradientTests
{
    private static GradientSpec Decode(string json) => GradientSpec.FromJson(JsonNode.Parse(json))!;

    [Fact]
    public void OldGradientJsonStillDecodesWithDefaultsForNewFields()
    {
        var g = Decode("""{"kind":"radial","angle":90,"stops":[{"color":"#112233","location":0},{"color":"#445566","location":1}]}""");
        Assert.Equal(GradientKind.Radial, g.Kind);
        Assert.Equal(90, g.Angle);
        Assert.Equal(2, g.Stops.Count);
        Assert.Empty(g.Sources);
        Assert.Equal(2.2, g.Sharpness);
        Assert.Equal(0, g.Noise);
        Assert.Equal(1, g.Opacity);
        Assert.False(g.Raw);
        Assert.Equal(0.5, g.CenterX);
        Assert.Equal(0.5, g.CenterY);
    }

    [Fact]
    public void UnknownKindsFallBackAndEminenceSourcesDecode()
    {
        var g = Decode("""{"kind":"conic","sources":[{"x":0.2,"y":0.3,"hex":"#FF0000"},{"x":"bad"}],"sharpness":3}""");
        Assert.Equal(GradientKind.Linear, g.Kind);
        Assert.Equal(2, g.Sources.Count);
        Assert.Equal("#FF0000", g.Sources[0].Color);
        Assert.Equal(0.5, g.Sources[1].X);
        Assert.Equal(3, g.Sharpness);
    }

    [Fact]
    public void ALayoutWrittenBeforeTheStudioLoadsAndKeepsItsBackground()
    {
        var layout = MosaicLayout.FromJson("""
            {"preset":"chrome","name":"Old","tabPlacement":"top","regions":{"titleBar":["tabStrip","newTab"],"toolbar":["back","addressField","menu"]},
             "appearance":{"accent":"#FF0000","background":{"kind":"gradient","gradient":{"kind":"linear","angle":45,"stops":[{"color":"#000000","location":0},{"color":"#FFFFFF","location":1}]}}}}
            """);
        Assert.Equal(BackgroundKind.Gradient, layout.Appearance.Background.Kind);
        Assert.Equal(45, layout.Appearance.Background.Gradient.Angle);
        Assert.Equal(NewTabBackgroundMode.Plain, layout.Appearance.NewTabBackground.Mode);
        Assert.Empty(layout.Appearance.WorkspaceBackgrounds);
        Assert.Equal([ShellComponent.Back, ShellComponent.AddressField, ShellComponent.Menu], layout.Components(ShellRegion.Toolbar));
    }

    [Fact]
    public void AMeshBackgroundRoundTripsThroughALayoutFile()
    {
        var layout = MosaicLayout.Factory(MosaicPresetID.ZenArc);
        var g = GradientPresets.Named("Aurora")!;
        g.Noise = 0.3;
        g.Opacity = 0.8;
        g.Raw = true;
        layout.Appearance.Background = new BackgroundSpec(BackgroundKind.Gradient, gradient: g);
        layout.Appearance.NewTabBackground = new NewTabBackground { Mode = NewTabBackgroundMode.Custom, Custom = new BackgroundSpec(BackgroundKind.Solid, "#123456") };
        layout.Appearance.WorkspaceBackgrounds = new() { ["W1"] = new BackgroundSpec(BackgroundKind.Gradient, gradient: GradientPresets.Named("Ocean")) };
        var decoded = MosaicLayout.FromJson(layout.JsonText());
        CanvasFixtures.AssertSameLayout(layout.Normalized(), decoded);
        Assert.Equal(4, decoded.Appearance.Background.Gradient.Sources.Count);
        Assert.Equal(2, decoded.Appearance.BackgroundForWorkspace("W1").Gradient.Sources.Count);
        Assert.Equal(decoded.Appearance.Background, decoded.Appearance.BackgroundForWorkspace("other"));
    }

    [Fact]
    public void BoundsClampNumbersDropInvalidColoursAndCapLists()
    {
        var g = new GradientSpec(GradientKind.Mesh, double.PositiveInfinity, [.. Enumerable.Range(0, 40).Select(i => new GradientStop("#000000", i))])
        {
            Sources = [.. Enumerable.Range(0, 50).Select(i => Gradients.NewSource(3, -2, i == 0 ? "nope" : "#FFFFFF", 99))],
            Sharpness = 40, Noise = -1, Opacity = 0, CenterX = 7,
        };
        var c = g.Clamped();
        Assert.Equal(135, c.Angle);
        Assert.Equal(GradientSpec.MaxStops, c.Stops.Count);
        Assert.All(c.Stops, s => Assert.InRange(s.Location, 0, 1));
        Assert.Equal(GradientSpec.MaxSources, c.Sources.Count);
        Assert.All(c.Sources, s => Assert.True(s.Color == "#FFFFFF" && s.X == 1 && s.Y == 0 && s.Spread == 3));
        Assert.Equal(c.Sources.Count, c.Sources.Select(s => s.Id).Distinct().Count());
        Assert.True(c.Sharpness == 6 && c.Noise == 0 && c.Opacity == 0.1 && c.CenterX == 1);
    }

    [Fact]
    public void PresetsAreValidAndUnchangedByClamping()
    {
        foreach (var (name, gradient) in GradientPresets.All) Assert.True(Gradients.Same(gradient.Clamped(), gradient), name);
        Assert.NotNull(GradientPresets.Named("four corners"));
        Assert.Equal(12, GradientPresets.MeshPresets.Count);
        Assert.Equal(18, GradientPresets.All.Count);
    }

    [Fact]
    public void MeshRendersTheRequestedSizeAndIsItsSourcesColourAtEachSource()
    {
        var g = GradientPresets.Named("Four Corners")!;
        var image = g.RenderMesh(64, 2);
        Assert.Equal(64, image.Width);
        Assert.Equal(32, image.Height);
        Assert.Equal(64 * 32 * 4, image.Pixels.Length);
        var expected = ColorValue.FromHex("#FF6B6B")!.Value;
        Assert.True(Math.Abs(image.Pixels[0] / 255.0 - expected.R) < 0.12 && Math.Abs(image.Pixels[1] / 255.0 - expected.G) < 0.12);
        Assert.Equal("#6BCB77", g.MeshColor(1, 1, 2).Hex);
        var empty = new GradientSpec { Kind = GradientKind.Mesh }.RenderMesh(0, double.NaN);
        Assert.Equal(1, empty.Width);
        Assert.Equal(empty.Width * empty.Height * 4, empty.Pixels.Length);
    }

    [Fact]
    public void SwitchingKindsKeepsTheColoursInOrder()
    {
        var linear = new GradientSpec(GradientKind.Linear, 0, [new("#FF0000", 0), new("#00FF00", 0.5), new("#0000FF", 1)]);
        var mesh = linear.Converted(GradientKind.Mesh);
        Assert.Equal(GradientKind.Mesh, mesh.Kind);
        Assert.Equal(["#FF0000", "#00FF00", "#0000FF"], mesh.Sources.Select(s => s.Color));
        Assert.True(mesh.Sources[0].X == 0 && mesh.Sources[2].X == 1);
        var back = mesh.Converted(GradientKind.Linear);
        Assert.Equal(["#FF0000", "#00FF00", "#0000FF"], back.Stops.Select(s => s.Color));
        Assert.True(back.Stops[0].Location == 0 && back.Stops[^1].Location == 1);
        Assert.Equal(linear.Stops.Select(s => (s.Color, s.Location)), linear.Converted(GradientKind.Radial).Stops.Select(s => (s.Color, s.Location)));
    }

    [Fact]
    public void ColourAlongTheStops()
    {
        var g = new GradientSpec(GradientKind.Linear, 135, new("#000000", 0), new("#FFFFFF", 1));
        Assert.Equal("#808080", g.StopColor(0.5).Hex);
        Assert.Equal("#000000", g.StopColor(-3).Hex);
        Assert.Equal("#000000", g.StopColor(double.NaN).Hex);
    }

    private static void AssertNear(string expectedHex, ColorValue actual, string context)
    {
        var e = ColorValue.FromHex(expectedHex)!.Value;
        Assert.True(Math.Abs(e.R - actual.R) <= 1.01 / 255 && Math.Abs(e.G - actual.G) <= 1.01 / 255 && Math.Abs(e.B - actual.B) <= 1.01 / 255,
            $"{context}: {expectedHex} vs {actual.Hex}");
    }

    [Fact]
    public void PresetsRenderAndConvertAsOnMacOS()
    {
        foreach (var entry in CanvasFixtures.Expectations["gradients"]!.AsArray())
        {
            var name = entry!["name"]!.GetValue<string>();
            var g = GradientPresets.Named(name)!;
            AssertNear(entry["average"]!.GetValue<string>(), g.AverageColor(), $"{name} average");
            if (g.Kind == GradientKind.Mesh)
            {
                var image = g.RenderMesh(24, 1.6);
                Assert.Equal(entry["meshWidth"]!.GetValue<int>(), image.Width);
                Assert.Equal(entry["meshHeight"]!.GetValue<int>(), image.Height);
                var expected = entry["meshPixels"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
                var actual = image.Pixels.Where((_, i) => i % 4 != 3).Select(b => (int)b).ToArray();
                Assert.Equal(expected.Length, actual.Length);
                // Same maths; libm and .NET may differ in the last bit of pow/cbrt.
                Assert.True(expected.Zip(actual).All(p => Math.Abs(p.First - p.Second) <= 1), $"{name}: mesh pixels differ");
                var at = entry["meshAt"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
                AssertNear(at[0], g.MeshColor(0.1, 0.2, 1.6), name);
                AssertNear(at[1], g.MeshColor(0.5, 0.5, 1.6), name);
                AssertNear(at[2], g.MeshColor(0.9, 0.7, 1.6), name);
                var stops = g.Converted(GradientKind.Linear).Stops;
                var expectedStops = entry["asLinear"]!.AsArray();
                Assert.Equal(expectedStops.Count, stops.Count);
                for (var i = 0; i < stops.Count; i++)
                {
                    Assert.Equal(expectedStops[i]!["color"]!.GetValue<string>(), stops[i].Color);
                    Assert.True(Math.Abs(expectedStops[i]!["location"]!.GetValue<double>() - stops[i].Location) < 1e-9);
                }
            }
            else
            {
                double[] at = [0, 0.25, 0.5, 0.8, 1];
                var expected = entry["stopsAt"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
                Assert.Equal(expected, at.Select(t => g.StopColor(t).Hex));
                var sources = g.Converted(GradientKind.Mesh).Sources;
                var expectedSources = entry["asMesh"]!.AsArray();
                Assert.Equal(expectedSources.Count, sources.Count);
                for (var i = 0; i < sources.Count; i++)
                {
                    Assert.Equal(expectedSources[i]!["color"]!.GetValue<string>(), sources[i].Color);
                    Assert.True(Math.Abs(expectedSources[i]!["x"]!.GetValue<double>() - sources[i].X) < 1e-9);
                    Assert.True(Math.Abs(expectedSources[i]!["y"]!.GetValue<double>() - sources[i].Y) < 1e-9);
                }
            }
        }
    }

    [Fact]
    public void RasterDrawsEveryKindWithSwiftUIGeometry()
    {
        // Linear at 0°: left edge is the first stop, right edge the last.
        var linear = new GradientSpec(GradientKind.Linear, 0, [new("#000000", 0), new("#FFFFFF", 1)]);
        var (pixels, width, height) = GradientRaster.Render(linear, 100, 2, bgra: false);
        Assert.Equal((100, 50), (width, height));
        Assert.True(pixels[0] < 5 && pixels[(width - 1) * 4] > 250);
        // 90° runs top to bottom.
        var down = GradientRaster.Render(new GradientSpec(GradientKind.Linear, 90, [.. linear.Stops]), 10, 1, bgra: false);
        Assert.True(down.Pixels[0] < 20 && down.Pixels[(9 * 10) * 4] > 235);
        // Radial: the centre is the first stop.
        var radial = new GradientSpec(GradientKind.Radial, 0, new("#FF0000", 0), new("#0000FF", 1));
        var r = GradientRaster.Render(radial, 21, 1, bgra: false);
        var centre = (10 * 21 + 10) * 4;
        Assert.True(r.Pixels[centre] > 240 && r.Pixels[centre + 2] < 20);
        // Angular from 0°: just below the +x axis is the start, just above it the end.
        var angular = new GradientSpec(GradientKind.Angular, 0, [new("#000000", 0), new("#FFFFFF", 1)]);
        var a = GradientRaster.Render(angular, 41, 1, bgra: false);
        Assert.True(a.Pixels[(21 * 41 + 40) * 4] < 30, "clockwise start below the axis");
        Assert.True(a.Pixels[(19 * 41 + 40) * 4] > 225, "end above the axis");
        // BGRA swaps red and blue.
        var bgra = GradientRaster.Render(radial, 21, 1);
        Assert.True(bgra.Pixels[centre + 2] > 240 && bgra.Pixels[centre] < 20);
        var tile = GradientRaster.GrainTile();
        Assert.Equal(128 * 128 * 4, tile.Pixels.Length);
        Assert.Equal(tile.Pixels, GradientRaster.GrainTile().Pixels);
    }

    [Fact]
    public void AdaptationDimsLightGradientsInDarkModeUnlessRaw()
    {
        var light = GradientPresets.Named("Pastel")!;
        Assert.Equal((false, 0.66), GradientRaster.Adaptation(light, darkMode: true));
        Assert.Null(GradientRaster.Adaptation(light, darkMode: false));
        light.Raw = true;
        Assert.Null(GradientRaster.Adaptation(light, darkMode: true));
        Assert.Equal((true, 0.62), GradientRaster.Adaptation(GradientPresets.Named("Midnight")!, darkMode: false));
    }
}
