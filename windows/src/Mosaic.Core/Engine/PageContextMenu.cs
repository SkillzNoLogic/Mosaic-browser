using Mosaic.Core.Browser;
using Mosaic.Core.Search;

namespace Mosaic.Core.Engine;

/// <summary>What was right-clicked, in engine-neutral terms (WebView2's ContextMenuTarget).</summary>
public sealed record PageContextTarget(
    PageContextKind Kind,
    string PageUrl,
    string? LinkUrl = null,
    string? LinkText = null,
    string? SourceUrl = null,
    string? SelectionText = null,
    bool IsEditable = false)
{
    public bool HasLink => !string.IsNullOrEmpty(LinkUrl);
    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectionText);
}

public enum PageContextKind { Page, Image, SelectedText, Audio, Video }

public enum NativeMenuItemKind { Command, Separator, Submenu, CheckBox, Radio }

/// <summary>One of the engine's own context-menu items (WebView2's CoreWebView2ContextMenuItem).</summary>
public sealed record NativeMenuItem(
    string Name,
    string Label,
    int CommandId,
    NativeMenuItemKind Kind = NativeMenuItemKind.Command,
    bool IsEnabled = true,
    bool IsChecked = false,
    string? Shortcut = null,
    IReadOnlyList<NativeMenuItem>? Children = null);

/// <summary>Mosaic's own context-menu commands; <see cref="Native"/> runs the engine's item.</summary>
public enum PageMenuCommand
{
    Native,
    Separator,
    OpenLinkInNewTab,
    OpenLinkInOtherEngine,
    OpenImageInNewTab,
    SearchSelection,
    FindInPage,
    TakeScreenshot,
    SavePageAsPdf,
}

/// <summary>An item of the menu Mosaic shows.</summary>
public sealed record PageMenuEntry(
    PageMenuCommand Command,
    string Label = "",
    int NativeId = 0,
    string? Icon = null,
    bool IsEnabled = true,
    bool IsCheckable = false,
    bool IsChecked = false,
    string? Shortcut = null,
    IReadOnlyList<PageMenuEntry>? Children = null)
{
    public bool IsSeparator => Command == PageMenuCommand.Separator;

    public static PageMenuEntry Separator { get; } = new(PageMenuCommand.Separator);
}

/// <summary>
/// The page context menu Mosaic shows for a Chromium tab: the engine's own items, minus the
/// ones that make no sense in Mosaic (new windows, Edge features), plus Mosaic's (open in the
/// other engine, search with the chosen engine, find, screenshot, PDF). Gecko tabs keep
/// Firefox's menu, trimmed by the agent.
/// </summary>
public static class PageContextMenu
{
    /// <summary>Engine items that are dropped: Mosaic has one window and its own versions of these.</summary>
    public static IReadOnlySet<string> Removed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "openLinkInNewWindow", "createQrCode", "share", "webCapture", "addToCollections", "openLinkInNewPrivateWindow",
        "openLinkInProfile", "readAloud", "translate", "visualSearch", "copyLinkToHighlight",
    };

    /// <summary>Longest selection quoted in "Search … for “…”".</summary>
    public const int QuoteLength = 24;

    public static IReadOnlyList<PageMenuEntry> Build(
        PageContextTarget target, IReadOnlyList<NativeMenuItem> native, EngineKind engine, bool otherEngineAvailable, SearchEngine searchEngine)
    {
        var mosaic = new List<PageMenuEntry>();
        if (target.HasLink)
        {
            mosaic.Add(new(PageMenuCommand.OpenLinkInNewTab, "Open Link in New Tab", Icon: "plus"));
            if (otherEngineAvailable)
            {
                mosaic.Add(new(PageMenuCommand.OpenLinkInOtherEngine, $"Open Link in {engine.Other().DisplayName()} Tab", Icon: "arrow.triangle.swap"));
            }
        }
        if (target.Kind == PageContextKind.Image && !string.IsNullOrEmpty(target.SourceUrl) && !target.SourceUrl.StartsWith("data:", StringComparison.Ordinal))
        {
            mosaic.Add(new(PageMenuCommand.OpenImageInNewTab, "Open Image in New Tab", Icon: "photo"));
        }

        var items = Convert(native).ToList();

        if (target.HasSelection)
        {
            var search = new PageMenuEntry(PageMenuCommand.SearchSelection, $"Search {searchEngine.Name} for “{Quote(target.SelectionText!)}”", Icon: "magnifyingglass");
            // Right after Copy, where people look for it.
            var copy = items.FindIndex(item => item.Command == PageMenuCommand.Native && NameOf(native, item.NativeId) == "copy");
            if (copy >= 0) items.Insert(copy + 1, search);
            else mosaic.Add(search);
        }
        else if (!target.HasLink && !target.IsEditable && target.Kind == PageContextKind.Page)
        {
            var page = new List<PageMenuEntry>
            {
                new(PageMenuCommand.FindInPage, "Find in Page…", Icon: "magnifyingglass", Shortcut: "Ctrl+F"),
                new(PageMenuCommand.TakeScreenshot, "Take Screenshot", Icon: "camera.viewfinder"),
                new(PageMenuCommand.SavePageAsPdf, "Save Page as PDF…", Icon: "doc.richtext"),
            };
            // Before "Inspect", which stays last.
            var inspect = items.FindIndex(item => item.Command == PageMenuCommand.Native && NameOf(native, item.NativeId) == "inspectElement");
            var at = inspect >= 0 ? inspect : items.Count;
            items.InsertRange(at, [PageMenuEntry.Separator, .. page, PageMenuEntry.Separator]);
        }

        if (mosaic.Count > 0) items.InsertRange(0, [.. mosaic, PageMenuEntry.Separator]);
        return Tidy(items);
    }

    private static IEnumerable<PageMenuEntry> Convert(IReadOnlyList<NativeMenuItem> native)
    {
        foreach (var item in native)
        {
            if (Removed.Contains(item.Name)) continue;
            switch (item.Kind)
            {
                case NativeMenuItemKind.Separator:
                    yield return PageMenuEntry.Separator;
                    break;
                case NativeMenuItemKind.Submenu:
                    var children = Tidy([.. Convert(item.Children ?? [])]);
                    if (children.Count > 0) yield return new(PageMenuCommand.Native, item.Label, item.CommandId, IsEnabled: item.IsEnabled, Children: children);
                    break;
                default:
                    yield return new(
                        PageMenuCommand.Native, item.Label, item.CommandId, IsEnabled: item.IsEnabled,
                        IsCheckable: item.Kind is NativeMenuItemKind.CheckBox or NativeMenuItemKind.Radio,
                        IsChecked: item.IsChecked, Shortcut: item.Shortcut);
                    break;
            }
        }
    }

    private static string? NameOf(IReadOnlyList<NativeMenuItem> native, int id)
    {
        foreach (var item in native)
        {
            if (item.CommandId == id && item.Kind != NativeMenuItemKind.Separator) return item.Name;
            if (item.Children is { } children && NameOf(children, id) is { } name) return name;
        }
        return null;
    }

    /// <summary>No separator first, last, or next to another.</summary>
    private static List<PageMenuEntry> Tidy(List<PageMenuEntry> items)
    {
        var tidy = new List<PageMenuEntry>();
        foreach (var item in items)
        {
            if (item.IsSeparator && (tidy.Count == 0 || tidy[^1].IsSeparator)) continue;
            tidy.Add(item);
        }
        while (tidy.Count > 0 && tidy[^1].IsSeparator) tidy.RemoveAt(tidy.Count - 1);
        return tidy;
    }

    /// <summary>The selection, on one line and shortened with an ellipsis.</summary>
    public static string Quote(string selection)
    {
        var text = string.Join(' ', selection.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= QuoteLength ? text : text[..(QuoteLength - 1)].TrimEnd() + "…";
    }
}

public sealed partial class PageFeatures
{
    /// <summary>The menu for a right-click in a Chromium tab (see <see cref="PageContextMenu"/>).</summary>
    public IReadOnlyList<PageMenuEntry> ContextMenu(BrowserTab tab, PageContextTarget target, IReadOnlyList<NativeMenuItem> native) =>
        PageContextMenu.Build(target, native, tab.Engine, tab.Engine == EngineKind.Gecko ? model.ChromiumAvailable : model.Gecko.Runtime is not null, model.SearchEngine);

    /// <summary>Runs one of Mosaic's context-menu commands for <paramref name="tab"/>.</summary>
    public void RunMenuCommand(BrowserTab tab, PageMenuCommand command, PageContextTarget target)
    {
        switch (command)
        {
            case PageMenuCommand.OpenLinkInNewTab when target.HasLink:
                // In the background, as browsers do for a link opened from the menu.
                model.NewTab(target.LinkUrl!, tab.Engine, select: false);
                break;
            case PageMenuCommand.OpenLinkInOtherEngine when target.HasLink:
                model.NewTab(target.LinkUrl!, tab.Engine.Other());
                break;
            case PageMenuCommand.OpenImageInNewTab when !string.IsNullOrEmpty(target.SourceUrl):
                model.NewTab(target.SourceUrl!, tab.Engine, select: false);
                break;
            case PageMenuCommand.SearchSelection when target.HasSelection:
                if (model.SearchEngine.SearchUrl(target.SelectionText!.Trim()) is { } url) model.NewTab(url, tab.Engine);
                break;
            case PageMenuCommand.FindInPage:
                ShowFindBar();
                break;
            case PageMenuCommand.TakeScreenshot:
                _ = TakeScreenshotAsync(fullPage: false);
                break;
            case PageMenuCommand.SavePageAsPdf:
                _ = ExportPdfAsync();
                break;
        }
    }
}
