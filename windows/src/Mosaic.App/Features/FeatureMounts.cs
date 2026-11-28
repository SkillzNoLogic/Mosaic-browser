using Microsoft.UI.Xaml;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;

namespace Mosaic.App.Features;

/// <summary>
/// Where feature areas plug their UI into the app, so no area edits another's files.
/// An area registers from a <c>[ModuleInitializer]</c> in its own folder
/// (<c>Features/&lt;Area&gt;/&lt;Area&gt;Mounts.cs</c>), which runs when the app starts:
/// <code>
/// internal static class LibraryMounts
/// {
///     [ModuleInitializer]
///     internal static void Register()
///     {
///         FeatureMounts.SettingsPages[SettingsCategory.Search] = model => new SearchSettingsPage(model);
///         FeatureMounts.InternalPages["bookmarks"] = model => new BookmarksPage(model);
///     }
/// }
/// </code>
/// The shell and winui areas read these registries; until a page is registered the shell
/// shows a placeholder.
/// </summary>
public static class FeatureMounts
{
    /// <summary>The page for each Settings category (shown inside the main window).</summary>
    public static readonly Dictionary<SettingsCategory, Func<BrowserModel, UIElement>> SettingsPages = [];

    /// <summary>
    /// Toolbar/sidebar components by the component id used in Mosaic layouts
    /// (<c>Sources/MosaicCore/Shell</c> component ids, e.g. "downloads", "blocker",
    /// "extensions", "passwords", "reader"). The shell places them; the area draws them.
    /// </summary>
    public static readonly Dictionary<string, Func<BrowserModel, UIElement>> Components = [];

    /// <summary>Overlays above the content area (prompts, notices), top-trailing, in registration order.</summary>
    public static readonly List<(string Id, Func<BrowserModel, UIElement> View)> Overlays = [];

    /// <summary>Mosaic's own pages, by host: <c>mosaic://bookmarks</c> → "bookmarks".</summary>
    public static readonly Dictionary<string, Func<BrowserModel, UIElement>> InternalPages = [];

    /// <summary>Called once with the model after the window and engines exist, in registration order.</summary>
    public static readonly List<(string Area, Action<BrowserModel, Window> Start)> Startup = [];

    /// <summary>
    /// The window's content: the shell area's composed shell (toolbar, tabs, sidebar, the page
    /// area, settings). Until it is registered, the winui area shows its own minimal UI.
    /// The shell places the engines' page area with the element the winui area provides.
    /// </summary>
    public static Func<BrowserModel, Window, UIElement>? Shell;

    private static readonly HashSet<string> covers = [];

    /// <summary>
    /// Says that Mosaic is (or stops) drawing over the page area: a menu, flyout, palette,
    /// prompt or panel. Gecko's page is a Win32 child window that would cover WinUI content,
    /// so while any cover is active the winui area hides it. Use a stable id per surface.
    /// </summary>
    public static void CoverPage(string id, bool covering)
    {
        bool changed;
        lock (covers) changed = covering ? covers.Add(id) : covers.Remove(id);
        if (changed) PageCoversChanged?.Invoke();
    }

    public static bool IsPageCovered
    {
        get { lock (covers) return covers.Count > 0; }
    }

    public static IReadOnlyCollection<string> PageCovers
    {
        get { lock (covers) return [.. covers]; }
    }

    /// <summary>Raised on the thread that called <see cref="CoverPage"/>.</summary>
    public static event Action? PageCoversChanged;
}
