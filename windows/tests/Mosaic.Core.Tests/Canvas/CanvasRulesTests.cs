using System.Text.Json.Nodes;
using Mosaic.Core.Canvas;
using Mosaic.Core.Canvas.Interim;

namespace Mosaic.Core.Tests.Canvas;

public class CanvasRulesTests
{
    private static readonly MosaicLayout chrome = MosaicLayout.Factory(MosaicPresetID.Chrome);
    private static MosaicLayout Zen => MosaicLayout.Factory(MosaicPresetID.ZenArc);
    private static int IndexOf(MosaicLayout l, ShellRegion r, ShellComponent c) => l.Components(r).ToList().IndexOf(c);

    [Fact]
    public void PaletteOffersWhatIsNotPlacedAndAlwaysFlexibleSpace()
    {
        var palette = LayoutCanvasRules.Palette(chrome);
        Assert.Equal(ShellComponent.FlexibleSpace, palette[0]);
        Assert.DoesNotContain(ShellComponent.Back, palette);
        Assert.Contains(ShellComponent.Settings, palette);
        Assert.DoesNotContain(ShellComponent.TabStrip, palette);
        Assert.DoesNotContain(ShellComponent.TabStrip, LayoutCanvasRules.Palette(Zen));
    }

    [Fact]
    public void ReorderingWithinARowUsesDropIndicatorPositions()
    {
        var edit = LayoutCanvasRules.Move(new(ShellRegion.Toolbar, 0), ShellRegion.Toolbar, 3, chrome);
        Assert.Equal([ShellComponent.Forward, ShellComponent.Reload, ShellComponent.Back, ShellComponent.Home], edit.Layout!.Components(ShellRegion.Toolbar).Take(4));
        Assert.Equal(new CanvasSlot(ShellRegion.Toolbar, 2), edit.Selection);
        CanvasFixtures.AssertSameLayout(chrome, LayoutCanvasRules.Move(new(ShellRegion.Toolbar, 1), ShellRegion.Toolbar, 2, chrome).Layout);
    }

    [Fact]
    public void MovingBetweenRegions()
    {
        var layout = LayoutCanvasRules.Move(new(ShellRegion.Toolbar, 3), ShellRegion.TitleBar, 0, chrome).Layout!;
        Assert.Equal(ShellComponent.Home, layout.Components(ShellRegion.TitleBar)[0]);
        Assert.DoesNotContain(ShellComponent.Home, layout.Components(ShellRegion.Toolbar));
    }

    [Fact]
    public void InvalidDropsAreRejectedWithAReason()
    {
        var field = IndexOf(chrome, ShellRegion.Toolbar, ShellComponent.AddressField);
        Assert.NotNull(LayoutCanvasRules.Move(new(ShellRegion.Toolbar, field), ShellRegion.Rail, 0, chrome).Message);
        Assert.NotNull(LayoutCanvasRules.Move(new(ShellRegion.Toolbar, field), ShellRegion.SidebarMiddle, 0, chrome).Message);
        Assert.NotNull(LayoutCanvasRules.Move(new(ShellRegion.TitleBar, 0), ShellRegion.SidebarTop, 0, chrome).Message);
        Assert.True(LayoutCanvasRules.VerdictPlacing(ShellComponent.AddressField, ShellRegion.SidebarTop, Zen).IsAllowed);
    }

    [Fact]
    public void RequiredComponentsAndTheLastWayToSettingsCannotBeRemoved()
    {
        var field = IndexOf(chrome, ShellRegion.Toolbar, ShellComponent.AddressField);
        Assert.NotNull(LayoutCanvasRules.Remove(new(ShellRegion.Toolbar, field), chrome).Message);
        Assert.NotNull(LayoutCanvasRules.Remove(new(ShellRegion.TitleBar, 0), chrome).Message);
        var menu = IndexOf(chrome, ShellRegion.Toolbar, ShellComponent.Menu);
        Assert.NotNull(LayoutCanvasRules.Remove(new(ShellRegion.Toolbar, menu), chrome).Message);
        var withSettings = LayoutCanvasRules.Add(ShellComponent.Settings, ShellRegion.Toolbar, null, chrome).Layout!;
        var menuNow = IndexOf(withSettings, ShellRegion.Toolbar, ShellComponent.Menu);
        var removed = LayoutCanvasRules.Remove(new(ShellRegion.Toolbar, menuNow), withSettings).Layout!;
        Assert.True(!removed.Contains(ShellComponent.Menu) && removed.Contains(ShellComponent.Settings));
        var settingsAt = IndexOf(removed, ShellRegion.Toolbar, ShellComponent.Settings);
        Assert.NotNull(LayoutCanvasRules.Move(new(ShellRegion.Toolbar, settingsAt), ShellRegion.SidebarBottom, 0, removed).Message);
    }

    [Fact]
    public void UsableRestoresAWayToSettingsAndTheAddressField()
    {
        var broken = chrome.Clone();
        broken.Remove(ShellComponent.Menu);
        broken.Remove(ShellComponent.AddressField);
        var fixedLayout = LayoutCanvasRules.Usable(broken);
        Assert.True(fixedLayout.Contains(ShellComponent.AddressField));
        Assert.True(LayoutCanvasRules.HasSettingsAccess(fixedLayout));
        foreach (var preset in Enum.GetValues<MosaicPresetID>())
            CanvasFixtures.AssertSameLayout(MosaicLayout.Factory(preset), LayoutCanvasRules.Usable(MosaicLayout.Factory(preset)));
        var hidden = Zen;
        hidden.TabPlacement = TabPlacement.Top;
        Assert.False(LayoutCanvasRules.Usable(hidden).RegionOf(ShellComponent.SidebarToggle)?.IsSidebar());
        Assert.NotNull(LayoutCanvasRules.VerdictPlacing(ShellComponent.SidebarToggle, ShellRegion.SidebarTop, chrome).Message);
    }

    [Fact]
    public void PaletteAdditionsIncludingRepeatedFlexibleSpace()
    {
        var a = LayoutCanvasRules.Add(ShellComponent.DownloadsPage, ShellRegion.Rail, null, chrome).Layout!;
        Assert.Equal([ShellComponent.DownloadsPage], a.Components(ShellRegion.Rail));
        var b = LayoutCanvasRules.Add(ShellComponent.FlexibleSpace, ShellRegion.Toolbar, 1, a).Layout!;
        Assert.Equal(ShellComponent.FlexibleSpace, b.Components(ShellRegion.Toolbar)[1]);
        Assert.NotNull(LayoutCanvasRules.Add(ShellComponent.AddressField, ShellRegion.Rail, null, chrome).Message);
        Assert.Equal(ShellRegion.Toolbar, LayoutCanvasRules.SuggestedRegion(ShellComponent.Settings, chrome));
        Assert.Equal(ShellRegion.SidebarBottom, LayoutCanvasRules.SuggestedRegion(ShellComponent.DownloadsPage, Zen));
    }

    [Fact]
    public void KeyboardMoves()
    {
        var left = LayoutCanvasRules.Shift(new(ShellRegion.Toolbar, 1), -1, chrome);
        Assert.Equal([ShellComponent.Forward, ShellComponent.Back], left.Layout!.Components(ShellRegion.Toolbar).Take(2));
        Assert.NotNull(LayoutCanvasRules.Shift(new(ShellRegion.Toolbar, 0), -1, chrome).Message);
        var up = LayoutCanvasRules.MoveToAdjacentRegion(new(ShellRegion.Toolbar, 0), -1, chrome);
        Assert.Equal(ShellComponent.Back, up.Layout!.Components(ShellRegion.TitleBar)[^1]);
        var field = IndexOf(chrome, ShellRegion.Toolbar, ShellComponent.AddressField);
        var down = LayoutCanvasRules.MoveToAdjacentRegion(new(ShellRegion.Toolbar, field), 1, chrome);
        Assert.Equal([ShellComponent.AddressField], down.Layout!.Components(ShellRegion.TabBar));
        Assert.NotNull(LayoutCanvasRules.MoveToAdjacentRegion(new(ShellRegion.TabBar, 0), 1, down.Layout).Message);
    }

    [Fact]
    public void HistoryUndoRedoRedoClearedByANewChangeAndLimit()
    {
        var h = new EditHistory<int>(3);
        Assert.False(h.CanUndo);
        h.Record(1);
        h.Record(2);
        Assert.True(h.TryUndo(3, out var v) && v == 2);
        Assert.True(h.TryUndo(2, out v) && v == 1);
        Assert.False(h.TryUndo(1, out _));
        Assert.True(h.TryRedo(1, out v) && v == 2);
        h.Record(2);
        Assert.False(h.CanRedo);
        for (var i = 10; i < 20; i++) h.Record(i);
        Assert.Equal(3, h.UndoStack.Count);
        h.Clear();
        Assert.False(h.CanUndo || h.CanRedo);
    }

    // MARK: Parity with the macOS rules, edit by edit

    private static CanvasSlot Slot(JsonNode node) =>
        new(JsonEnum.Parse<ShellRegion>(node["region"]!.GetValue<string>())!.Value, node["index"]!.GetValue<int>());

    private static ShellRegion Region(JsonNode? node) => JsonEnum.Parse<ShellRegion>(node!.GetValue<string>())!.Value;

    private static void AssertEdit(JsonNode expected, CanvasEdit actual, string context)
    {
        // Windows adds Edge's trailing column (last in keyboard order): where macOS finds no row,
        // Windows may move into it.
        if (expected["rejected"] is not null && actual.Selection?.Region == ShellRegion.TrailingRail) return;
        if (expected["rejected"] is { } message)
        {
            Assert.True(actual.Message == message.GetValue<string>(), $"{context}: expected refusal “{message}”, got {actual.Message ?? "a change"}");
            return;
        }
        Assert.True(actual.Layout is not null, $"{context}: refused with “{actual.Message}”");
        CanvasFixtures.AssertSameJson(expected["regions"], actual.Layout!.ToJsonObject()["regions"], context);
        if (expected["selection"] is { } selection) Assert.True(actual.Selection == Slot(selection), $"{context}: selection {actual.Selection}");
        else Assert.True(actual.Selection is null, $"{context}: selection {actual.Selection} (expected none)");
    }

    [Fact]
    public void EveryEditMatchesTheMacOSRules()
    {
        var checkedEdits = 0;
        foreach (var entry in CanvasFixtures.Expectations["rules"]!.AsArray())
        {
            var name = entry!["name"]!.GetValue<string>();
            if (name == "safari") continue;  // no Safari Mosaic on Windows (the shell reads it as Edge)
            var layout = MosaicLayout.Decode(entry["layout"])!;
            CanvasFixtures.AssertSameJson(entry["layout"], layout.ToJsonObject(), $"{name} decode/encode");
            Assert.Equal(entry["palette"]!.AsArray().Select(v => v!.GetValue<string>()), LayoutCanvasRules.Palette(layout).Where(c => c != ShellComponent.VerticalTabs).Select(c => c.Raw()));
            CanvasFixtures.AssertSameJson(entry["usable"], LayoutCanvasRules.Usable(layout).ToJsonObject(), $"{name} usable");
            Assert.Equal(entry["hasSettingsAccess"]!.GetValue<bool>(), LayoutCanvasRules.HasSettingsAccess(layout));
            foreach (var move in entry["moves"]!.AsArray())
            {
                var from = Slot(move!["from"]!);
                var to = Region(move["to"]);
                var before = move["before"]!.GetValue<int>();
                AssertEdit(move, LayoutCanvasRules.Move(from, to, before, layout), $"{name}: move {from} → {to}:{before}");
                checkedEdits++;
            }
            foreach (var remove in entry["removes"]!.AsArray())
            {
                var from = Slot(remove!["from"]!);
                AssertEdit(remove, LayoutCanvasRules.Remove(from, layout), $"{name}: remove {from}");
                checkedEdits++;
            }
            foreach (var key in entry["keys"]!.AsArray())
            {
                var from = Slot(key!["from"]!);
                var edit = key["key"]!.GetValue<string>() switch
                {
                    "left" => LayoutCanvasRules.Shift(from, -1, layout),
                    "right" => LayoutCanvasRules.Shift(from, 1, layout),
                    "up" => LayoutCanvasRules.MoveToAdjacentRegion(from, -1, layout),
                    _ => LayoutCanvasRules.MoveToAdjacentRegion(from, 1, layout),
                };
                AssertEdit(key, edit, $"{name}: key {key["key"]} at {from}");
                checkedEdits++;
            }
            foreach (var add in entry["adds"]!.AsArray())
            {
                var component = JsonEnum.Parse<ShellComponent>(add!["component"]!.GetValue<string>())!.Value;
                var to = Region(add["to"]);
                AssertEdit(add, LayoutCanvasRules.Add(component, to, null, layout), $"{name}: add {component} → {to}");
                checkedEdits++;
            }
            foreach (var component in Enum.GetValues<ShellComponent>().Where(c => c != ShellComponent.VerticalTabs))  // Windows only
            {
                var expected = entry[$"suggested-{component.Raw()}"];
                Assert.True(expected?.GetValue<string>() == LayoutCanvasRules.SuggestedRegion(component, layout)?.Raw(), $"{name}: suggested region for {component}: {expected} vs {LayoutCanvasRules.SuggestedRegion(component, layout)}");
            }
            foreach (var (placement, expected) in entry["placements"]!.AsObject())
            {
                var actual = layout.WithTabPlacement(JsonEnum.Parse<TabPlacement>(placement)!.Value);
                CanvasFixtures.AssertSameJson(expected, actual.ToJsonObject(), $"{name} → tabs {placement}");
            }
        }
        Assert.True(checkedEdits > 2500, $"only {checkedEdits} edits compared");
    }
}
