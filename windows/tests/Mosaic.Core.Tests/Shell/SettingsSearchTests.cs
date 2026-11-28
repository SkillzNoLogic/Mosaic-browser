using Mosaic.Core.Shell;

namespace Mosaic.Core.Tests.Shell;

public sealed class SettingsSearchTests
{
    [Fact]
    public void ListHasAllFourteenCategoriesOnce()
    {
        var ordered = SettingsSearch.Ordered.ToList();
        Assert.Equal(14, ordered.Count);
        Assert.Equal(Enum.GetValues<SettingsCategory>().Order(), ordered.Order());
    }

    [Fact]
    public void EmptyQueryMatchesEverything() => Assert.Equal(14, SettingsSearch.Search("  ").Count);

    [Fact]
    public void TitlesAndLabelsMatch()
    {
        Assert.Equal(SettingsCategory.Privacy, SettingsSearch.Search("privacy").First().Category);
        var mica = Assert.Single(SettingsSearch.Search("mica"));
        Assert.Equal(SettingsCategory.Mosaics, mica.Category);
        Assert.Equal(["Mica"], mica.Labels);
        Assert.Contains(SettingsSearch.Search("vertical tabs"), m => m.Category == SettingsCategory.Mosaics);
        Assert.Contains(SettingsSearch.Search("high contrast"), m => m.Category == SettingsCategory.Accessibility);
        Assert.Empty(SettingsSearch.Search("safari"));
        Assert.Empty(SettingsSearch.Search("zzzz"));
    }

    [Fact]
    public void AreasAddTheirWords()
    {
        SettingsSearch.AddLabels(SettingsCategory.Downloads, "Quokka folder");
        Assert.Equal(SettingsCategory.Downloads, Assert.Single(SettingsSearch.Search("quokka")).Category);
    }
}
