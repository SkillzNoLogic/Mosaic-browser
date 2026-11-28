using Mosaic.Core.Shell;

namespace Mosaic.Core.Tests.Shell;

public sealed class MosaicStoreTests
{
    private static string TempFile() => Path.Combine(Directory.CreateTempSubdirectory("mosaic-store-").FullName, "Shell", "mosaics.json");

    [Fact]
    public async Task ApplyEditRestoreAndReload()
    {
        var path = TempFile();
        var store = new MosaicStore(path);
        var changes = 0;
        store.Changed += () => changes++;
        Assert.Equal(MosaicPresetID.ZenArc, store.ActivePreset);
        store.Apply(MosaicPresetID.Edge);
        store.Update(l => l.Appearance.Accent = "#123456");
        Assert.True(store.IsCustomized(MosaicPresetID.Edge));
        Assert.False(store.IsCustomized(MosaicPresetID.Chrome));
        Assert.Equal(2, changes);
        store.Update(l => l.Appearance.Accent = "#123456");  // no change, no event
        Assert.Equal(2, changes);
        await store.FlushAsync();

        var reloaded = new MosaicStore(path);
        Assert.Equal(MosaicPresetID.Edge, reloaded.ActivePreset);
        Assert.Equal("#123456", reloaded.Layout.Appearance.Accent);
        reloaded.RestoreDefaults(MosaicPresetID.Edge);
        Assert.False(reloaded.IsCustomized(MosaicPresetID.Edge));
    }

    [Fact]
    public void PreviewDoesNotChangeTheActiveLayout()
    {
        var store = new MosaicStore(null);
        store.Preview(MosaicPresetID.Chrome);
        Assert.Equal(MosaicPresetID.Chrome, store.PreviewedPreset);
        Assert.Equal(MosaicPresetID.Chrome, store.EffectiveLayout.Preset);
        Assert.Equal(MosaicPresetID.ZenArc, store.ActivePreset);
        store.EndPreview();
        Assert.Null(store.PreviewedPreset);
        store.Preview(MosaicPresetID.Opera);
        store.Apply(MosaicPresetID.Opera);
        Assert.Null(store.PreviewLayout);
        Assert.Equal(MosaicPresetID.Opera, store.ActivePreset);
    }

    [Fact]
    public void ImportMakesTheLayoutsPresetActive()
    {
        var store = new MosaicStore(null);
        var layout = MosaicLayout.Factory(MosaicPresetID.Chrome);
        layout.BookmarksBar = false;
        store.Import(layout);
        Assert.Equal(MosaicPresetID.Chrome, store.ActivePreset);
        Assert.False(store.Layout.BookmarksBar);
    }

    [Fact]
    public void DamagedFileGivesDefaults()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not json");
        var store = new MosaicStore(path);
        Assert.Equal(MosaicPresetID.ZenArc, store.ActivePreset);
        Assert.False(store.IsCustomized(MosaicPresetID.ZenArc));
    }

    [Fact]
    public async Task ShellSettingsPersist()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("mosaic-settings-").FullName, "settings.json");
        var settings = new ShellSettings(path)
        {
            Homepage = "example.com",
            ReduceTransparency = AccessibilityOverride.On,
            TextSize = TextSize.Larger,
            LastSettingsCategory = SettingsCategory.Accessibility,
            SidebarPeek = true,
        };
        await settings.FlushAsync();
        var reloaded = new ShellSettings(path);
        Assert.Equal("example.com", reloaded.Homepage);
        Assert.Equal(AccessibilityOverride.On, reloaded.ReduceTransparency);
        Assert.Equal(TextSize.Larger, reloaded.TextSize);
        Assert.Equal(SettingsCategory.Accessibility, reloaded.LastSettingsCategory);
        Assert.False(reloaded.SidebarPeek);  // not saved
        Assert.Equal("https://example.com", reloaded.HomepageUrl(Mosaic.Core.Search.SearchEngine.Default)?.TrimEnd('/'));
    }
}
