using System.Text.Json.Nodes;
using Mosaic.Core.Productivity;

namespace Mosaic.Core.Tests.Productivity;

public class ReaderSettingsTests
{
    [Fact]
    public void DefaultsMatchFirefox()
    {
        var settings = new ReaderSettings();
        Assert.Equal(20, settings.FontSizePixels);
        Assert.Equal(30, settings.ContentWidthEm);
        Assert.Equal(1.6, settings.LineHeightEm, 4);
        Assert.Equal(ReaderFont.Sans, settings.Font);
        Assert.Equal(ReaderTheme.Light, settings.Theme);
    }

    [Fact]
    public void ClampsSteps()
    {
        var settings = new ReaderSettings { FontSize = 40, ContentWidth = 0, LineHeight = -3 };
        Assert.Equal((9, 1, 1), (settings.FontSize, settings.ContentWidth, settings.LineHeight));
        Assert.Equal(9, (new ReaderSettings() with { FontSize = 12 }).FontSize);
    }

    [Fact]
    public void GeckoPreferencesRoundTrip()
    {
        var settings = new ReaderSettings { Font = ReaderFont.Serif, FontSize = 7, ContentWidth = 5, LineHeight = 6, Theme = ReaderTheme.Sepia };
        var prefs = settings.GeckoPreferences();
        Assert.Equal("serif", prefs["reader.font_type"]!.GetValue<string>());
        Assert.Equal("sepia", prefs["reader.color_scheme"]!.GetValue<string>());
        // Through JSON, as the agent's readerPrefs() returns them.
        var parsed = (JsonObject)JsonNode.Parse(prefs.ToJsonString())!;
        Assert.Equal(settings, new ReaderSettings().MergingGeckoPreferences(parsed));
    }

    [Fact]
    public void MergingIgnoresValuesMosaicDoesNotOffer()
    {
        var merged = new ReaderSettings { Theme = ReaderTheme.Dark }.MergingGeckoPreferences(new JsonObject
        {
            ["reader.color_scheme"] = "auto", ["reader.font_type"] = "monospace", ["reader.font_size"] = 14,
        });
        Assert.Equal(ReaderTheme.Dark, merged.Theme);
        Assert.Equal(ReaderFont.Sans, merged.Font);
        Assert.Equal(9, merged.FontSize);
    }

    [Fact]
    public void DecodesPartialAndInvalidStoredValues()
    {
        var settings = ReaderSettings.FromJson("""{"font":"serif","fontSize":99,"theme":"neon"}""");
        Assert.Equal(ReaderFont.Serif, settings.Font);
        Assert.Equal(9, settings.FontSize);
        Assert.Equal(ReaderTheme.Light, settings.Theme);
        Assert.Equal(4, settings.LineHeight);
        Assert.Equal(settings, ReaderSettings.FromJson(settings.ToJson()));
        Assert.Equal(new ReaderSettings(), ReaderSettings.FromJson("garbage"));
    }

    [Fact]
    public void MergingChangesKeepsTheRest()
    {
        var settings = new ReaderSettings { Font = ReaderFont.Serif }.Merging(new JsonObject { ["fontSize"] = 7, ["theme"] = "sepia" });
        Assert.Equal(new ReaderSettings { Font = ReaderFont.Serif, FontSize = 7, Theme = ReaderTheme.Sepia }, settings);
    }
}
