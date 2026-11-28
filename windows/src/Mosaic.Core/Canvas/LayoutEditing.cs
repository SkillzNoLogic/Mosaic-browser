using Mosaic.Core.Canvas.Interim;

// The layout canvas's rules, ported from macOS (Sources/MosaicCore/Canvas/LayoutEditing.swift):
// which drops are allowed (and why not), the palette of components not placed, the edits the
// editor makes (drag, palette, keyboard), the guard that keeps every layout usable, and the
// undo history. UI-independent; the same messages as macOS. Owner: canvas.

namespace Mosaic.Core.Canvas;

/// <summary>A component at a position in the layout.</summary>
public readonly record struct CanvasSlot(ShellRegion Region, int Index);

/// <summary>Whether an edit may happen, with the reason shown to the user when not.</summary>
public sealed record CanvasVerdict(string? Message)
{
    public static readonly CanvasVerdict Allowed = new((string?)null);

    public static CanvasVerdict Rejected(string message) => new(message);

    public bool IsAllowed => Message is null;
}

/// <summary>The result of an edit: the new (usable) layout and selection, or why nothing changed.</summary>
public sealed record CanvasEdit(MosaicLayout? Layout, CanvasSlot? Selection, string? Message)
{
    public static CanvasEdit Changed(MosaicLayout layout, CanvasSlot? selection) => new(layout, selection, null);

    public static CanvasEdit Rejected(string message) => new(null, null, message);
}

public static class LayoutCanvasRules
{
    /// <summary>Regions in the order the keyboard steps through them.</summary>
    public static readonly IReadOnlyList<ShellRegion> RegionOrder =
    [
        ShellRegion.TitleBar, ShellRegion.Toolbar, ShellRegion.TabBar, ShellRegion.Rail,
        ShellRegion.SidebarTop, ShellRegion.SidebarMiddle, ShellRegion.SidebarBottom,
    ];

    /// <summary>Regions on screen whenever the window is (a horizontal layout's sidebar is hidden until opened).</summary>
    public static HashSet<ShellRegion> AlwaysVisibleRegions(MosaicLayout layout)
    {
        var regions = new HashSet<ShellRegion> { ShellRegion.TitleBar, ShellRegion.Toolbar, ShellRegion.TabBar, ShellRegion.Rail };
        if (layout.TabPlacement.IsVertical() && layout.Sidebar != SidebarBehavior.AutoHide)
            regions.UnionWith([ShellRegion.SidebarTop, ShellRegion.SidebarMiddle, ShellRegion.SidebarBottom]);
        return regions;
    }

    /// <summary>The Settings button or the browser menu sits in a region that is always visible.</summary>
    public static bool HasSettingsAccess(MosaicLayout layout)
    {
        var visible = AlwaysVisibleRegions(layout);
        return new[] { ShellComponent.Settings, ShellComponent.Menu }
            .Any(c => visible.Any(r => layout.Components(r).Contains(c)));
    }

    /// <summary>The layout made safe to show: normalised, and a menu added when no way to Settings is left.</summary>
    public static MosaicLayout Usable(MosaicLayout layout)
    {
        var result = layout.Normalized();
        if (!result.TabPlacement.IsVertical() && result.RegionOf(ShellComponent.SidebarToggle)?.IsSidebar() == true)
        {
            var target = result.Components(ShellRegion.Toolbar).Count == 0 ? ShellRegion.TitleBar : ShellRegion.Toolbar;
            result.Insert(ShellComponent.SidebarToggle, target, 0);
            result = result.Normalized();
        }
        if (!HasSettingsAccess(result))
        {
            var visible = AlwaysVisibleRegions(result);
            ShellRegion[] candidates = [ShellRegion.Toolbar, ShellRegion.TitleBar, ShellRegion.SidebarBottom, ShellRegion.SidebarTop, ShellRegion.TabBar, ShellRegion.Rail];
            ShellRegion? found = null;
            foreach (var c in candidates)
            {
                if (visible.Contains(c) && result.Components(c).Count > 0) { found = c; break; }
            }
            var region = found ?? (result.TabPlacement.IsVertical() ? ShellRegion.SidebarBottom : ShellRegion.Toolbar);
            result.Insert(ShellComponent.Menu, region);
            result = result.Normalized();
        }
        return result;
    }

    /// <summary>Components not in the layout: flexible space always; the tab strip only with tabs on top.</summary>
    public static IReadOnlyList<ShellComponent> Palette(MosaicLayout layout)
    {
        var list = new List<ShellComponent> { ShellComponent.FlexibleSpace };
        foreach (var component in RawValues.All<ShellComponent>())
        {
            if (component == ShellComponent.FlexibleSpace || layout.Contains(component)) continue;
            if (component == ShellComponent.TabStrip && layout.TabPlacement.IsVertical()) continue;
            list.Add(component);
        }
        return list;
    }

    /// <summary>Whether the component is drawn where it is (the tab strip is not with vertical tabs).</summary>
    public static bool IsDrawn(ShellComponent component, MosaicLayout layout) =>
        !(component == ShellComponent.TabStrip && layout.TabPlacement.IsVertical());

    private static string Lower(ShellComponent c) => c.Title().ToLowerInvariant();

    // MARK: Verdicts

    /// <summary>May <paramref name="component"/> be placed in <paramref name="region"/>?</summary>
    public static CanvasVerdict VerdictPlacing(ShellComponent component, ShellRegion region, MosaicLayout layout, CanvasSlot? source = null)
    {
        if (!region.Accepts(component))
        {
            if (region == ShellRegion.Rail) return CanvasVerdict.Rejected($"The quick-access rail is too narrow for the {Lower(component)}.");
            if (component == ShellComponent.TabStrip)
                return CanvasVerdict.Rejected("The tab strip needs a horizontal row. For tabs in a sidebar, set Tabs to Left or Right Sidebar.");
            return CanvasVerdict.Rejected($"{region.Title()} can’t hold the {Lower(component)}.");
        }
        if (component == ShellComponent.AddressField && region.IsSidebar() && !layout.TabPlacement.IsVertical())
            return CanvasVerdict.Rejected("With tabs on top the sidebar starts hidden, so the address field stays in a row that is always visible.");
        if (component == ShellComponent.SidebarToggle && region.IsSidebar() && !layout.TabPlacement.IsVertical())
            return CanvasVerdict.Rejected("With tabs on top the sidebar starts hidden, so its button must stay outside it.");
        if (component == ShellComponent.TabStrip && (region.IsSidebar() || region == ShellRegion.Rail))
            return CanvasVerdict.Rejected("The tab strip needs a horizontal row.");
        if (component is ShellComponent.Settings or ShellComponent.Menu)
        {
            var trial = layout.Clone();
            if (source is { } s) trial.RemoveAt(s.Index, s.Region);
            else trial.Remove(component);
            if (!trial.Regions.TryGetValue(region, out var list)) trial.Regions[region] = list = [];
            list.Add(component);
            if (HasSettingsAccess(layout) && !HasSettingsAccess(trial))
                return CanvasVerdict.Rejected("Settings must stay reachable: keep the Settings button or the menu in a row that is always visible.");
        }
        return CanvasVerdict.Allowed;
    }

    /// <summary>May the component at <paramref name="slot"/> be taken out of the layout?</summary>
    public static CanvasVerdict VerdictRemoving(CanvasSlot slot, MosaicLayout layout)
    {
        var list = layout.Components(slot.Region);
        if (slot.Index < 0 || slot.Index >= list.Count) return CanvasVerdict.Rejected("Nothing is there.");
        switch (list[slot.Index])
        {
            case ShellComponent.AddressField:
                return CanvasVerdict.Rejected("The address field is always part of the window. Drag it to another row instead.");
            case ShellComponent.TabStrip when layout.TabPlacement == TabPlacement.Top:
                return CanvasVerdict.Rejected("With tabs on top the tab strip is required. Set Tabs to a sidebar first, or move it to another row.");
            case ShellComponent.SidebarToggle when layout.TabPlacement.IsVertical() && layout.Sidebar == SidebarBehavior.Collapsible:
                return CanvasVerdict.Rejected("A collapsible sidebar needs its button. Choose Always Shown or Auto-Hide first.");
        }
        var trial = layout.Clone();
        trial.RemoveAt(slot.Index, slot.Region);
        if (HasSettingsAccess(layout) && !HasSettingsAccess(trial))
            return CanvasVerdict.Rejected("Settings must stay reachable: keep the Settings button or the menu in the window.");
        return CanvasVerdict.Allowed;
    }

    // MARK: Edits

    /// <summary>
    /// Moves the component at <paramref name="source"/> so it lands before the item now at
    /// <paramref name="index"/> of <paramref name="region"/> (as drawn before the move; count appends).
    /// </summary>
    public static CanvasEdit Move(CanvasSlot source, ShellRegion region, int index, MosaicLayout layout)
    {
        var list = layout.Components(source.Region);
        if (source.Index < 0 || source.Index >= list.Count) return CanvasEdit.Rejected("Nothing is there.");
        var component = list[source.Index];
        if (VerdictPlacing(component, region, layout, source).Message is { } message) return CanvasEdit.Rejected(message);
        var target = Math.Min(Math.Max(index, 0), layout.Components(region).Count);
        if (region == source.Region)
        {
            if (target == source.Index || target == source.Index + 1) return CanvasEdit.Changed(layout, source);
            if (source.Index < target) target -= 1;
        }
        var edited = layout.Clone();
        if (!edited.Move(source.Region, source.Index, region, target))
            return CanvasEdit.Rejected($"{region.Title()} can’t hold the {Lower(component)}.");
        return Finish(edited, component, new CanvasSlot(region, target));
    }

    /// <summary>Adds <paramref name="component"/> from the palette before <paramref name="index"/> (null appends).</summary>
    public static CanvasEdit Add(ShellComponent component, ShellRegion region, int? index, MosaicLayout layout)
    {
        if (component != ShellComponent.FlexibleSpace && layout.RegionOf(component) is { } existing)
        {
            var at = layout.Components(existing).ToList().IndexOf(component);
            return Move(new CanvasSlot(existing, at), region, index ?? layout.Components(region).Count, layout);
        }
        if (VerdictPlacing(component, region, layout).Message is { } message) return CanvasEdit.Rejected(message);
        var count = layout.Components(region).Count;
        var target = Math.Min(Math.Max(index ?? count, 0), count);
        var edited = layout.Clone();
        var list = edited.Components(region).ToList();
        list.Insert(target, component);
        edited.Regions[region] = list;
        return Finish(edited, component, new CanvasSlot(region, target));
    }

    /// <summary>Takes the component at <paramref name="slot"/> out of the layout.</summary>
    public static CanvasEdit Remove(CanvasSlot slot, MosaicLayout layout)
    {
        if (VerdictRemoving(slot, layout).Message is { } message) return CanvasEdit.Rejected(message);
        var edited = layout.Clone();
        edited.RemoveAt(slot.Index, slot.Region);
        var result = Usable(edited);
        var remaining = result.Components(slot.Region);
        CanvasSlot? selection = remaining.Count == 0 ? null : new CanvasSlot(slot.Region, Math.Min(slot.Index, remaining.Count - 1));
        return CanvasEdit.Changed(result, selection);
    }

    /// <summary>Keyboard: one place left (−1) or right (+1) in its row.</summary>
    public static CanvasEdit Shift(CanvasSlot slot, int offset, MosaicLayout layout)
    {
        var list = layout.Components(slot.Region);
        if (slot.Index < 0 || slot.Index >= list.Count) return CanvasEdit.Rejected("Nothing is selected.");
        var target = slot.Index + offset;
        if (target < 0 || target >= list.Count)
            return CanvasEdit.Rejected(offset < 0 ? $"Already first in {slot.Region.Title()}." : $"Already last in {slot.Region.Title()}.");
        var edited = layout.Clone();
        edited.Move(slot.Region, slot.Index, slot.Region, target);
        return Finish(edited, list[slot.Index], new CanvasSlot(slot.Region, target));
    }

    /// <summary>Keyboard: to the next region up (−1) or down (+1) in <see cref="RegionOrder"/> that accepts it.</summary>
    public static CanvasEdit MoveToAdjacentRegion(CanvasSlot slot, int direction, MosaicLayout layout)
    {
        var list = layout.Components(slot.Region);
        var start = RegionOrder.ToList().IndexOf(slot.Region);
        if (slot.Index < 0 || slot.Index >= list.Count || start < 0) return CanvasEdit.Rejected("Nothing is selected.");
        var component = list[slot.Index];
        var position = start + direction;
        string? lastRejection = null;
        while (position >= 0 && position < RegionOrder.Count)
        {
            var region = RegionOrder[position];
            var verdict = VerdictPlacing(component, region, layout, slot);
            if (verdict.IsAllowed)
            {
                var index = direction < 0 ? layout.Components(region).Count : 0;
                return Move(slot, region, index, layout);
            }
            lastRejection = verdict.Message;
            position += direction;
        }
        return CanvasEdit.Rejected(lastRejection ?? (direction < 0
            ? $"No row above takes the {Lower(component)}."
            : $"No row below takes the {Lower(component)}."));
    }

    /// <summary>Where the palette puts a component added without a position.</summary>
    public static ShellRegion? SuggestedRegion(ShellComponent component, MosaicLayout layout)
    {
        ShellRegion[] order = layout.TabPlacement.IsVertical()
            ? [ShellRegion.SidebarBottom, ShellRegion.SidebarTop, ShellRegion.Toolbar, ShellRegion.TitleBar, ShellRegion.TabBar, ShellRegion.Rail]
            : [ShellRegion.Toolbar, ShellRegion.TitleBar, ShellRegion.TabBar, ShellRegion.Rail, ShellRegion.SidebarBottom];
        foreach (var region in order.Where(r => layout.Components(r).Count > 0).Concat(order))
        {
            if (VerdictPlacing(component, region, layout).IsAllowed) return region;
        }
        return null;
    }

    private static CanvasEdit Finish(MosaicLayout edited, ShellComponent component, CanvasSlot preferred)
    {
        var result = Usable(edited);
        var list = result.Components(preferred.Region);
        if (preferred.Index >= 0 && preferred.Index < list.Count && list[preferred.Index] == component)
            return CanvasEdit.Changed(result, preferred);
        if (result.RegionOf(component) is { } region)
        {
            var index = result.Components(region).ToList().IndexOf(component);
            return CanvasEdit.Changed(result, component == ShellComponent.FlexibleSpace ? null : new CanvasSlot(region, index));
        }
        return CanvasEdit.Changed(result, null);
    }
}

/// <summary>Undo and redo for values: <c>Record</c> the value before each change.</summary>
public sealed class EditHistory<T>(int limit = 100)
{
    private readonly List<T> undo = [];
    private readonly List<T> redo = [];

    public int Limit { get; } = Math.Max(1, limit);
    public IReadOnlyList<T> UndoStack => undo;
    public IReadOnlyList<T> RedoStack => redo;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>Call before changing <paramref name="previous"/> into something else. Clears redo.</summary>
    public void Record(T previous)
    {
        if (undo.Count > 0 && EqualityComparer<T>.Default.Equals(undo[^1], previous)) return;
        undo.Add(previous);
        if (undo.Count > Limit) undo.RemoveRange(0, undo.Count - Limit);
        redo.Clear();
    }

    public bool TryUndo(T current, out T value)
    {
        if (undo.Count == 0) { value = default!; return false; }
        value = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add(current);
        return true;
    }

    public bool TryRedo(T current, out T value)
    {
        if (redo.Count == 0) { value = default!; return false; }
        value = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add(current);
        return true;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }
}
