using Mosaic.Core.Shell;

namespace Mosaic.Core.Tests.Shell;

public sealed class ShellPlanTests
{
    private static readonly ShellMetrics Regular = new(1, ShellDensity.Regular);

    private static ShellPlan Plan(MosaicLayout layout, bool sidebarVisible) => new(layout, showsHome: true, sidebarVisible, Regular);

    [Fact]
    public void ChromeHasTabsInTheTitleRow()
    {
        var plan = Plan(MosaicLayout.Factory(MosaicPresetID.Chrome), false);
        Assert.Equal(CaptionHost.TitleRow, plan.CaptionHost);
        Assert.Equal(["titleRow", "toolbar", "bookmarksBar"], plan.Parts);
        var strip = Assert.Single(plan.TitleItems, i => i.Component == ShellComponent.TabStrip);
        Assert.True(strip.AttachesNewTab);
        Assert.DoesNotContain(plan.TitleItems, i => i.Component == ShellComponent.NewTab);
        Assert.True(Assert.Single(plan.ToolbarItems, i => i.Component == ShellComponent.AddressField).EmbedsStar);
        Assert.Equal(Regular.TabRowHeight, plan.CaptionRowHeight);
    }

    [Fact]
    public void ZenArcPutsTheCaptionButtonsInAThinStrip()
    {
        var plan = Plan(MosaicLayout.Factory(MosaicPresetID.ZenArc), true);
        Assert.Equal(CaptionHost.TitleStrip, plan.CaptionHost);
        Assert.Equal(["titleStrip", "sidebarLeading"], plan.Parts);
        Assert.Equal(32, plan.CaptionRowHeight);
        Assert.Empty(plan.CollapsedStripItems);
    }

    [Fact]
    public void CollapsedZenArcShowsNavigationAbove()
    {
        var plan = Plan(MosaicLayout.Factory(MosaicPresetID.ZenArc), false);
        Assert.Contains(plan.CollapsedStripItems, i => i.Component == ShellComponent.AddressField);
        Assert.Contains(plan.CollapsedStripItems, i => i.Component == ShellComponent.Back);
        Assert.Equal(Regular.RowHeight, plan.CaptionRowHeight);
    }

    [Fact]
    public void EdgeVerticalPutsTheCaptionButtonsInTheToolbar()
    {
        var layout = MosaicLayout.Factory(MosaicPresetID.Edge).WithTabPlacement(TabPlacement.SidebarLeading);
        var plan = Plan(layout, true);
        Assert.Equal(CaptionHost.Toolbar, plan.CaptionHost);
        Assert.Contains("trailingRail", plan.Parts);
        var collapsed = Plan(layout, false);
        Assert.True(collapsed.SidebarIconStrip);
    }

    [Fact]
    public void ATrailingSidebarHoldsTheCaptionButtons()
    {
        var layout = MosaicLayout.Factory(MosaicPresetID.ZenArc).WithTabPlacement(TabPlacement.SidebarTrailing);
        Assert.Equal(CaptionHost.TrailingSidebar, Plan(layout, true).CaptionHost);
        Assert.Equal(CaptionHost.TitleStrip, Plan(layout, false).CaptionHost);
    }

    [Fact]
    public void HiddenHomeAndAGapOnlyRowDrawNothing()
    {
        var layout = MosaicLayout.Factory(MosaicPresetID.Chrome);
        Assert.DoesNotContain(layout.DrawnItems(ShellRegion.Toolbar, showsHome: false), i => i.Component == ShellComponent.Home);
        layout.Regions[ShellRegion.TabBar] = [ShellComponent.FlexibleSpace];
        Assert.Empty(layout.DrawnItems(ShellRegion.TabBar, true));
        layout.Regions[ShellRegion.Toolbar].Insert(0, ShellComponent.SidebarToggle);
        Assert.Contains(layout.DrawnItems(ShellRegion.Toolbar, true), i => i.Component == ShellComponent.SidebarToggle);
    }

    [Fact]
    public void MetricsFollowDensityAndTextSize()
    {
        Assert.Equal(38, new ShellMetrics(1, ShellDensity.Compact).RowHeight);
        Assert.Equal(50, new ShellMetrics(1, ShellDensity.Comfortable).RowHeight);
        Assert.Equal(44 * 1.25, new ShellMetrics(1.25, ShellDensity.Regular).RowHeight);
    }
}
