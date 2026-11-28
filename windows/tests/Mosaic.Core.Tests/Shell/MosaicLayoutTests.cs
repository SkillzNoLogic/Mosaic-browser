using Mosaic.Core.Shell;

namespace Mosaic.Core.Tests.Shell;

public sealed class MosaicLayoutTests
{
    private static MosaicLayout Edge => MosaicLayout.Factory(MosaicPresetID.Edge);

    [Fact]
    public void WindowsHasNoSafariPreset()
    {
        Assert.Equal([MosaicPresetID.ZenArc, MosaicPresetID.Chrome, MosaicPresetID.Opera, MosaicPresetID.Edge], Enum.GetValues<MosaicPresetID>());
        Assert.Null(JsonEnum.Parse<MosaicPresetID>("safari"));
        Assert.Equal("edge", MosaicPresetID.Edge.Raw());
    }

    [Fact]
    public void EveryPresetIsAlreadyNormalised()
    {
        foreach (var preset in Enum.GetValues<MosaicPresetID>())
        {
            var layout = MosaicLayout.Factory(preset);
            Assert.True(layout.ContentEquals(layout.Normalized()), preset.ToString());
            Assert.Equal(preset, layout.Preset);
            Assert.Equal(preset.Title(), layout.Name);
        }
    }

    [Fact]
    public void EdgeLooksLikeEdge()
    {
        var edge = Edge;
        Assert.Equal(TabPlacement.Top, edge.TabPlacement);
        Assert.Equal(TabStyle.Opera, edge.TabStyle);  // rounded tabs
        Assert.Equal(AddressBarStyle.Omnibox, edge.AddressBar);
        Assert.Equal([ShellComponent.Workspaces, ShellComponent.VerticalTabs, ShellComponent.TabStrip, ShellComponent.NewTab, ShellComponent.FlexibleSpace],
            edge.Components(ShellRegion.TitleBar));
        Assert.Contains(ShellComponent.AddressField, edge.Components(ShellRegion.Toolbar));
        Assert.Equal(ShellRegion.TrailingRail, edge.RegionOf(ShellComponent.Settings));
        Assert.Equal(ChromeMaterial.LiquidGlass, edge.Appearance.Material);  // Mica
        Assert.Equal(HexColor.System, edge.Appearance.Accent);
        Assert.True(edge.Appearance.ContentCornerRadius > 0);
        Assert.Contains("\"trailingRail\"", edge.JsonText(), StringComparison.Ordinal);
    }

    [Fact]
    public void EdgeVerticalTabsKeepTheToolbar()
    {
        var vertical = Edge.WithTabPlacement(TabPlacement.SidebarLeading);
        Assert.Equal(TabPlacement.SidebarLeading, vertical.TabPlacement);
        Assert.Empty(vertical.Components(ShellRegion.TitleBar));
        Assert.Equal(Edge.Components(ShellRegion.Toolbar), vertical.Components(ShellRegion.Toolbar));
        Assert.Equal([ShellComponent.Workspaces, ShellComponent.VerticalTabs, ShellComponent.FlexibleSpace, ShellComponent.SidebarToggle],
            vertical.Components(ShellRegion.SidebarTop));
        Assert.Contains(ShellComponent.NewTab, vertical.Components(ShellRegion.SidebarBottom));
        Assert.False(vertical.Contains(ShellComponent.TabStrip));
        Assert.Equal(SidebarBehavior.Collapsible, vertical.Sidebar);
        Assert.True(vertical.ContentEquals(vertical.Normalized()));
    }

    [Fact]
    public void EdgeRoundTripsBetweenModes()
    {
        var back = Edge.WithTabPlacement(TabPlacement.SidebarLeading).WithTabPlacement(TabPlacement.Top);
        Assert.True(back.ContentEquals(Edge), back.JsonText());
        var trailing = Edge.WithTabPlacement(TabPlacement.SidebarTrailing).WithTabPlacement(TabPlacement.SidebarLeading);
        Assert.Equal(TabPlacement.SidebarLeading, trailing.TabPlacement);
    }

    [Fact]
    public void EdgeHorizontalPicksUpWhatWasAddedToThePane()
    {
        var vertical = Edge.WithTabPlacement(TabPlacement.SidebarLeading);
        vertical.Insert(ShellComponent.SearchEngine, ShellRegion.SidebarBottom, 0);
        var top = vertical.WithTabPlacement(TabPlacement.Top);
        var toolbar = top.Components(ShellRegion.Toolbar).ToList();
        Assert.True(toolbar.IndexOf(ShellComponent.SearchEngine) == toolbar.IndexOf(ShellComponent.Menu) - 1);
        Assert.Equal(ShellRegion.TitleBar, top.RegionOf(ShellComponent.TabStrip));
    }

    [Fact]
    public void InsertMovesAndRespectsRegions()
    {
        var layout = MosaicLayout.Factory(MosaicPresetID.Chrome);
        Assert.False(layout.Insert(ShellComponent.AddressField, ShellRegion.Rail));
        Assert.False(layout.Insert(ShellComponent.TabStrip, ShellRegion.TrailingRail));
        Assert.False(layout.Insert(ShellComponent.TabStrip, ShellRegion.SidebarTop));
        Assert.True(layout.Insert(ShellComponent.Menu, ShellRegion.Toolbar, 0));
        Assert.Equal(ShellComponent.Menu, layout.Components(ShellRegion.Toolbar)[0]);
        Assert.Single(layout.Components(ShellRegion.Toolbar), c => c == ShellComponent.Menu);
        Assert.True(layout.Insert(ShellComponent.FlexibleSpace, ShellRegion.Toolbar, 1));
        Assert.Equal(2, layout.Regions.Values.SelectMany(l => l).Count(c => c == ShellComponent.FlexibleSpace));
        // Moving within a region accounts for the removal.
        var back = layout.Components(ShellRegion.Toolbar).ToList().IndexOf(ShellComponent.Back);
        layout.Insert(ShellComponent.Back, ShellRegion.Toolbar, back + 2);
        Assert.Equal(back + 1, layout.Components(ShellRegion.Toolbar).ToList().IndexOf(ShellComponent.Back));
    }

    [Fact]
    public void MoveAndRemove()
    {
        var layout = MosaicLayout.Factory(MosaicPresetID.Opera);
        Assert.True(layout.Move(ShellRegion.Rail, 0, ShellRegion.Toolbar, 0));
        Assert.Equal(ShellComponent.Home, layout.Components(ShellRegion.Toolbar)[0]);
        Assert.False(layout.Move(ShellRegion.Toolbar, 99, ShellRegion.Rail, 0));
        var addressIndex = layout.Components(ShellRegion.Toolbar).ToList().IndexOf(ShellComponent.AddressField);
        Assert.False(layout.Move(ShellRegion.Toolbar, addressIndex, ShellRegion.Rail, 0));
        Assert.Equal(ShellComponent.Home, layout.RemoveAt(0, ShellRegion.Toolbar));
        Assert.Null(layout.RemoveAt(99, ShellRegion.Toolbar));
        layout.Remove(ShellComponent.FlexibleSpace);
        Assert.False(layout.Contains(ShellComponent.FlexibleSpace));
    }

    [Fact]
    public void NormalisationRepairsLayouts()
    {
        var layout = MosaicLayout.Factory(MosaicPresetID.Chrome);
        layout.Regions.Clear();
        layout.Regions[ShellRegion.Toolbar] = [ShellComponent.FlexibleSpace, ShellComponent.FlexibleSpace, ShellComponent.Back, ShellComponent.Back];
        layout.Regions[ShellRegion.TrailingRail] = [ShellComponent.AddressField, ShellComponent.Home];
        layout.SidebarWidth = double.NaN;
        layout.Appearance.CornerRadius = 50;
        layout.Appearance.Accent = "teal";
        var n = layout.Normalized();
        Assert.Equal([ShellComponent.FlexibleSpace, ShellComponent.Back, ShellComponent.AddressField], n.Components(ShellRegion.Toolbar));
        Assert.Equal([ShellComponent.Home], n.Components(ShellRegion.TrailingRail));
        Assert.Equal(ShellRegion.TitleBar, n.RegionOf(ShellComponent.TabStrip));
        Assert.Equal(240, n.SidebarWidth);
        Assert.Equal(20, n.Appearance.CornerRadius);
        Assert.Equal(HexColor.System, n.Appearance.Accent);
    }

    [Fact]
    public void CollapsibleVerticalSidebarGetsAButton()
    {
        var layout = MosaicLayout.Factory(MosaicPresetID.ZenArc);
        layout.Remove(ShellComponent.SidebarToggle);
        Assert.Equal(ShellComponent.SidebarToggle, layout.Normalized().Components(ShellRegion.SidebarTop)[0]);
    }

    [Fact]
    public void GXStylingOnAndOff()
    {
        var gx = MosaicLayout.Factory(MosaicPresetID.Opera).WithGXStyling();
        Assert.True(gx.HasGXStyling);
        Assert.Equal("#FA1E4E", gx.Appearance.Accent);
        var off = gx.WithoutGXStyling();
        Assert.False(off.HasGXStyling);
        Assert.True(off.ContentEquals(MosaicLayout.Factory(MosaicPresetID.Opera)));
    }

    [Fact]
    public void HexColours()
    {
        Assert.Equal((1.0, 0.0, 0.0, 1.0), HexColor.Components("#FF0000"));
        Assert.Equal(0x80 / 255.0, HexColor.Components("#00000080")!.Value.Alpha);
        Assert.Null(HexColor.Components("#FFF"));
        Assert.True(HexColor.IsValid("system"));
        Assert.Equal("#1A73E8", HexColor.String(0x1A / 255.0, 0x73 / 255.0, 0xE8 / 255.0));
        Assert.Equal("#FFFFFF80", HexColor.String(1, 1, 1, 0.5));
    }

    [Fact]
    public void CloneIsDeep()
    {
        var a = MosaicLayout.Factory(MosaicPresetID.ZenArc);
        var b = a.Clone();
        b.Regions[ShellRegion.SidebarTop].Clear();
        b.Appearance.Background.Gradient.Stops[0].Color = "#000000";
        Assert.NotEmpty(a.Regions[ShellRegion.SidebarTop]);
        Assert.Equal("#C9BFF7", a.Appearance.Background.Gradient.Stops[0].Color);
    }

    [Fact]
    public void NotAnObjectIsAnError()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => MosaicLayout.FromJson("[1]"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => MosaicLayout.FromJson("not json"));
    }
}
