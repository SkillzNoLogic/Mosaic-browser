using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Browser;
using Windows.ApplicationModel.DataTransfer;

namespace Mosaic.App.Features.Tabs;

/// <summary>
/// Drag and drop of tabs, for the shell's tab strips and sidebars, which draw the tabs (the
/// tabs area owns the reordering rules: <see cref="BrowserModel.MoveTab(BrowserTab, int)"/>,
/// <see cref="BrowserModel.MoveTabBefore"/>, <see cref="BrowserModel.MoveGroupBefore"/>,
/// <see cref="BrowserModel.AddTabToGroup"/>, <see cref="BrowserModel.MoveTabToWorkspace"/>).
/// <code>
/// tabItem.CanDrag = true;
/// tabItem.DragStarting += (_, e) => TabDragDrop.Start(e, tab);
/// tabItem.AllowDrop = true;
/// tabItem.DragOver += (_, e) => TabDragDrop.AcceptMove(e);
/// tabItem.Drop += (_, e) => TabDragDrop.DropBefore(model, e, tab);  // or DropAfter
/// </code>
/// The workspace switcher accepts the same drags (moves the tab to that workspace). Links and
/// URLs dropped on a strip are the system area's (they open tabs).
/// </summary>
public static class TabDragDrop
{
    /// <summary>The data format carrying the dragged tab's <see cref="BrowserTab.Id"/>.</summary>
    public const string Format = "application/x-mosaic-tab";

    /// <summary>The data format carrying a dragged group's ID (a group header being dragged).</summary>
    public const string GroupFormat = "application/x-mosaic-tab-group";

    public static void Start(DragStartingEventArgs e, BrowserTab tab)
    {
        e.Data.SetData(Format, tab.Id.ToString());
        e.Data.RequestedOperation = DataPackageOperation.Move;
        // Also a link, so dropping a tab on another app or on the desktop makes sense.
        if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri)) e.Data.SetWebLink(uri);
        e.Data.Properties.Title = tab.DisplayTitle;
    }

    public static void StartGroup(DragStartingEventArgs e, Guid groupId)
    {
        e.Data.SetData(GroupFormat, groupId.ToString());
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    /// <summary>Accepts a tab or group being dragged over the element (call from <c>DragOver</c>).</summary>
    public static bool AcceptMove(DragEventArgs e)
    {
        if (!e.DataView.Contains(Format) && !e.DataView.Contains(GroupFormat)) return false;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        return true;
    }

    /// <summary>The dragged tab, when the drag carries one of this model's tabs.</summary>
    public static async Task<BrowserTab?> DraggedTabAsync(BrowserModel model, DragEventArgs e)
    {
        if (!e.DataView.Contains(Format)) return null;
        var deferral = e.GetDeferral();
        try
        {
            return await e.DataView.GetDataAsync(Format) is string text && Guid.TryParse(text, out var id) ? model.TabWithId(id) : null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    public static async Task<Guid?> DraggedGroupAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(GroupFormat)) return null;
        var deferral = e.GetDeferral();
        try
        {
            return await e.DataView.GetDataAsync(GroupFormat) is string text && Guid.TryParse(text, out var id) ? id : null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Drops the dragged tab (or group) just before <paramref name="target"/>; null target: at the end.</summary>
    public static async Task DropBefore(BrowserModel model, DragEventArgs e, BrowserTab? target)
    {
        if (await DraggedTabAsync(model, e) is { } tab)
        {
            if (target is not null && model.WorkspaceIdOf(target) != model.WorkspaceIdOf(tab)) model.MoveTabToWorkspace(tab, model.WorkspaceIdOf(target));
            model.MoveTabBefore(tab, target);
        }
        else if (await DraggedGroupAsync(e) is { } group)
        {
            model.MoveGroupBefore(group, target);
        }
    }

    /// <summary>Drops the dragged tab just after <paramref name="target"/>.</summary>
    public static Task DropAfter(BrowserModel model, DragEventArgs e, BrowserTab target)
    {
        var order = model.TabsIn(model.WorkspaceIdOf(target));
        var index = order.IndexOf(target);
        return DropBefore(model, e, index >= 0 && index + 1 < order.Count ? order[index + 1] : null);
    }

    /// <summary>
    /// Drops on <paramref name="target"/> as Chrome does: in its left half before it, in its right
    /// half after it (vertical strips: top and bottom halves).
    /// </summary>
    public static Task DropOn(BrowserModel model, DragEventArgs e, BrowserTab target, FrameworkElement item, bool vertical = false)
    {
        var point = e.GetPosition(item);
        var before = vertical ? point.Y < item.ActualHeight / 2 : point.X < item.ActualWidth / 2;
        return before ? DropBefore(model, e, target) : DropAfter(model, e, target);
    }
}
