using System.Text.Json;
using Mosaic.Core.Browser;
using Mosaic.Core.Search;
using Mosaic.Core.Shell;

namespace Mosaic.Core.Productivity;

/// <summary>
/// The command palette (Ctrl+K): search over registered actions (<see cref="PaletteRegistry"/>),
/// open tabs, settings categories, the library's bookmarks and history, plus "open this address"
/// and "search the web" fallbacks. Port of <c>Sources/Mosaic/Features/Productivity/CommandPalette.swift</c>.
/// </summary>
/// <remarks>The app draws it as an overlay bound to these properties. UI thread only.</remarks>
public sealed class CommandPalette : ObservableObject
{
    public const int ResultLimit = 60;

    private readonly BrowserModel model;
    private readonly ProductivityFiles files;
    private PaletteRecents recents = new();
    private bool isPresented;
    private string query = "";
    private IReadOnlyList<PaletteAction> results = [];
    private int selection;

    internal CommandPalette(BrowserModel model, ProductivityFiles files)
    {
        this.model = model;
        this.files = files;
    }

    public bool IsPresented { get => isPresented; private set => Set(ref isPresented, value); }

    public string Query
    {
        get => query;
        set
        {
            if (!Set(ref query, value ?? "")) return;
            Selection = 0;
            Refresh();
        }
    }

    public IReadOnlyList<PaletteAction> Results { get => results; private set => Set(ref results, value); }

    /// <summary>Index into <see cref="Results"/> of the highlighted row.</summary>
    public int Selection
    {
        get => selection;
        set => Set(ref selection, results.Count == 0 ? 0 : Math.Clamp(value, 0, results.Count - 1));
    }

    public IReadOnlyList<string> RecentIds => recents.Ids;

    internal async Task LoadAsync()
    {
        var text = await Task.Run(() => files.Read(ProductivityFiles.PaletteRecentsFile));
        if (text is null) return;
        try
        {
            if (JsonSerializer.Deserialize<string[]>(text) is { } ids) recents = new PaletteRecents(ids);
        }
        catch (JsonException) { }
    }

    // ---- Presenting ----

    public void Present()
    {
        query = "";
        Notify(nameof(Query));
        selection = 0;
        Refresh();
        IsPresented = true;
        // Keyboard input must come to Mosaic, also when a Gecko page (another process) has focus.
        model.RequestWindowActivationForProductivity();
    }

    /// <param name="restoringFocus">Hand the keyboard back to the page (closed without choosing).</param>
    public void Dismiss(bool restoringFocus = true)
    {
        if (!isPresented) return;
        IsPresented = false;
        if (restoringFocus) model.FocusContent();
    }

    public void Toggle()
    {
        if (isPresented) Dismiss();
        else Present();
    }

    public void MoveSelection(int offset)
    {
        if (results.Count == 0) return;
        Selection = ((selection + offset) % results.Count + results.Count) % results.Count;
    }

    public void PerformSelection()
    {
        if (selection >= 0 && selection < results.Count) Perform(results[selection]);
    }

    public void Perform(PaletteAction action)
    {
        if (action.Kind is PaletteKind.Action or PaletteKind.Setting or PaletteKind.Page)
        {
            recents.Note(action.Id);
            files.Write(ProductivityFiles.PaletteRecentsFile, JsonSerializer.Serialize(recents.Ids));
        }
        Dismiss(restoringFocus: false);
        action.Perform();
    }

    /// <summary>Runs the action with <paramref name="id"/> among the current candidates for the query (automation).</summary>
    public bool Perform(string id)
    {
        var action = Candidates(query.Trim()).FirstOrDefault(a => a.Id == id) ?? results.FirstOrDefault(a => a.Id == id)
            ?? WebFallbacks(query.Trim()).FirstOrDefault(a => a.Id == id);
        if (action is null) return false;
        Perform(action);
        return true;
    }

    // ---- Results ----

    public void Refresh()
    {
        Results = ComputeResults(query);
        if (selection >= results.Count) Selection = results.Count - 1;
        if (selection < 0) Selection = 0;
    }

    public IReadOnlyList<PaletteAction> ComputeResults(string text)
    {
        var trimmed = text.Trim();
        var all = Candidates(trimmed);
        var byId = new Dictionary<string, PaletteAction>();
        foreach (var action in all) byId.TryAdd(action.Id, action);
        var ranked = PaletteRanker.Rank(
            all.Select(a => new PaletteCandidate(a.Id, a.Title, [.. a.Keywords, a.Subtitle ?? ""], a.Boost)).ToList(),
            trimmed, recents.Ids, ResultLimit);
        var list = ranked.Select(candidate => byId[candidate.Id]).ToList();
        if (trimmed.Length > 0) list.AddRange(WebFallbacks(trimmed));
        return list;
    }

    /// <summary>Registered actions, other open tabs, settings categories and (once something is typed) library results.</summary>
    private List<PaletteAction> Candidates(string text)
    {
        var items = PaletteRegistry.Actions(model);
        // Other open tabs (switching to the current one would do nothing).
        foreach (var tab in model.Tabs)
        {
            if (ReferenceEquals(tab, model.SelectedTab)) continue;
            var keywords = new List<string> { "tab", tab.Engine.DisplayName() };
            if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) && uri.Host.Length > 0) keywords.Add(uri.Host);
            var target = tab;
            items.Add(new PaletteAction($"tab:{tab.Id}", tab.DisplayTitle, () =>
            {
                if (model.Tabs.Contains(target)) model.Select(target);
            })
            {
                Subtitle = tab.IsStartPage ? null : tab.Url,
                Keywords = keywords,
                Icon = "square.on.square",
                Kind = PaletteKind.Tab,
                Boost = 6,
            });
        }
        if (ProductivityIntegration.OpenSettings is { } openSettings)
        {
            foreach (var category in Enum.GetValues<SettingsCategory>())
            {
                var chosen = category;
                items.Add(new PaletteAction($"settings.{category.Id()}", $"{category.Title()} Settings", () => openSettings(model, chosen))
                {
                    Keywords = ["settings", "preferences", "options"],
                    Icon = "gearshape",
                    Kind = PaletteKind.Setting,
                    Boost = 2,
                });
            }
        }
        if (text.Length > 0)
        {
            var openUrls = model.Tabs.Select(tab => tab.Url).ToHashSet();
            foreach (var result in PaletteRegistry.Search(model, text))
            {
                // A page already open in a tab is offered as that tab.
                if (result.Kind is PaletteKind.Bookmark or PaletteKind.History && result.Subtitle is { } url && openUrls.Contains(url)) continue;
                items.Add(result);
            }
        }
        return items;
    }

    /// <summary>Typing an address offers to open it; anything typed offers a web search.</summary>
    private List<PaletteAction> WebFallbacks(string text)
    {
        var items = new List<PaletteAction>();
        if (text.Length == 0) return items;
        var engine = model.SearchEngine;
        if (AddressResolver.IsAddress(text) && AddressResolver.Resolve(text, engine) is { } url)
        {
            items.Add(new PaletteAction("web.open", $"Open {text}", () => model.OpenFromProductivity(url))
            {
                Subtitle = url,
                Icon = "globe",
                Kind = PaletteKind.Web,
            });
        }
        if (engine.SearchUrl(text) is { } search)
        {
            items.Add(new PaletteAction("web.search", $"Search {engine.Name} for “{text}”", () => model.OpenFromProductivity(search))
            {
                Icon = "magnifyingglass",
                Kind = PaletteKind.Web,
            });
        }
        return items;
    }
}
