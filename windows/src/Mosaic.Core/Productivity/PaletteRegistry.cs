using Mosaic.Core.Browser;

namespace Mosaic.Core.Productivity;

/// <summary>What a palette row is, for its icon tint and the label at its right.</summary>
public enum PaletteKind
{
    Action, Tab, Bookmark, History, Setting, Page, Web,
}

public static class PaletteKinds
{
    public static string Id(this PaletteKind kind) => kind switch
    {
        PaletteKind.Action => "action",
        PaletteKind.Tab => "tab",
        PaletteKind.Bookmark => "bookmark",
        PaletteKind.History => "history",
        PaletteKind.Setting => "setting",
        PaletteKind.Page => "page",
        _ => "web",
    };

    /// <summary>Shown at the right of a row that has no shortcut.</summary>
    public static string? Label(this PaletteKind kind) => kind switch
    {
        PaletteKind.Tab => "Tab",
        PaletteKind.Bookmark => "Bookmark",
        PaletteKind.History => "History",
        PaletteKind.Setting => "Settings",
        _ => null,
    };
}

/// <summary>
/// One entry in the command palette: something to do, a tab to switch to, a page to open.
/// Port of macOS's <c>PaletteAction</c>.
/// </summary>
public sealed class PaletteAction
{
    public PaletteAction(string id, string title, Action perform)
    {
        Id = id;
        Title = title;
        Perform = perform;
    }

    /// <summary>
    /// Stable and unique, namespaced by area ("tabs.pin", "downloads.show"): used to rank recently
    /// chosen actions higher, and by automation (<c>palettePerform &lt;id&gt;</c>).
    /// </summary>
    public string Id { get; }

    public string Title { get; init; }

    /// <summary>Secondary line (an address, the current value…).</summary>
    public string? Subtitle { get; init; }

    /// <summary>Other words people might type for it ("preferences" for Settings).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Icon name for <c>Icons.Make</c> (the SF Symbol names the macOS code uses).</summary>
    public string Icon { get; init; } = "command";

    public PaletteKind Kind { get; init; } = PaletteKind.Action;

    /// <summary>Shown as a hint. For a catalogue command use <c>model.Productivity.Shortcuts.Combo(id)</c>, so the user's shortcut is shown.</summary>
    public KeyCombo? Shortcut { get; init; }

    /// <summary>Ranking nudge; leave at 0 unless an action should win ties.</summary>
    public double Boost { get; init; }

    /// <summary>Runs after the palette has closed.</summary>
    public Action Perform { get; }
}

/// <summary>
/// Where every area adds its command-palette actions (macOS: <c>CommandPalette.register</c>):
/// <code>
/// PaletteRegistry.Register("downloads", model =>
/// [
///     new PaletteAction("downloads.show", "Show Downloads", () => model.ShowDownloads()) { Icon = "arrow.down.circle" },
/// ]);
/// </code>
/// A provider is asked for its actions every time the palette's results are computed, so titles
/// can follow the current state ("Mute Tab" / "Unmute Tab") and unavailable actions can simply be
/// left out. Search providers (bookmarks, history) are asked only once something is typed.
/// Registering an id again replaces the provider.
/// </summary>
public static class PaletteRegistry
{
    public delegate IEnumerable<PaletteAction> Provider(BrowserModel model);

    public delegate IEnumerable<PaletteAction> SearchProvider(BrowserModel model, string query);

    private static readonly List<(string Id, Provider Provider)> providers = [];
    private static readonly List<(string Id, SearchProvider Provider)> searchProviders = [];

    static PaletteRegistry()
    {
        providers.Add(("mosaic.builtin", BuiltInPaletteActions.Actions));
        providers.Add(("mosaic.integration", IntegrationPaletteActions.Actions));
    }

    public static void Register(string id, Provider provider)
    {
        lock (providers)
        {
            providers.RemoveAll(entry => entry.Id == id);
            providers.Add((id, provider));
        }
    }

    /// <summary>Adds results that depend on what is typed (the library's bookmarks and history).</summary>
    public static void RegisterSearch(string id, SearchProvider provider)
    {
        lock (searchProviders)
        {
            searchProviders.RemoveAll(entry => entry.Id == id);
            searchProviders.Add((id, provider));
        }
    }

    public static void Unregister(string id)
    {
        lock (providers) providers.RemoveAll(entry => entry.Id == id);
        lock (searchProviders) searchProviders.RemoveAll(entry => entry.Id == id);
    }

    public static IReadOnlyList<string> ProviderIds
    {
        get
        {
            lock (providers)
            lock (searchProviders)
            {
                return [.. providers.Select(entry => entry.Id), .. searchProviders.Select(entry => entry.Id)];
            }
        }
    }

    /// <summary>Every registered action, in registration order; a provider that throws contributes nothing.</summary>
    internal static List<PaletteAction> Actions(BrowserModel model)
    {
        List<Provider> list;
        lock (providers) list = providers.Select(entry => entry.Provider).ToList();
        var result = new List<PaletteAction>();
        foreach (var provider in list)
        {
            try { result.AddRange(provider(model)); }
            catch (Exception) { /* one area's bug must not empty the palette */ }
        }
        return result;
    }

    internal static List<PaletteAction> Search(BrowserModel model, string query)
    {
        List<SearchProvider> list;
        lock (searchProviders) list = searchProviders.Select(entry => entry.Provider).ToList();
        var result = new List<PaletteAction>();
        foreach (var provider in list)
        {
            try { result.AddRange(provider(model, query)); }
            catch (Exception) { }
        }
        return result;
    }
}
