using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Productivity;

/// <summary>
/// The new-tab page's Speed Dial: the user's pinned sites (saved in the profile) followed by the
/// library's most visited sites (<see cref="ProductivityIntegration.TopSites"/>). Port of
/// <c>Sources/Mosaic/Features/Productivity/SpeedDialModel.swift</c>. UI thread only.
/// </summary>
public sealed class SpeedDialModel
{
    /// <summary>Most top-site tiles the grid shows (pinned sites are never cut).</summary>
    public const int TopSiteLimit = 8;

    private readonly BrowserModel model;
    private readonly ProductivityFiles files;
    private SpeedDial dial = new();

    internal SpeedDialModel(BrowserModel model, ProductivityFiles files)
    {
        this.model = model;
        this.files = files;
    }

    /// <summary>The pinned sites and options. Change it through this model's methods.</summary>
    public SpeedDial Dial => dial;

    /// <summary>The last save error, shown on the page.</summary>
    public string? SaveError { get; private set; }

    public event Action? Changed;

    internal async Task LoadAsync()
    {
        var text = await Task.Run(() => files.Read(ProductivityFiles.SpeedDialFile));
        if (text is not null && SpeedDial.FromJson(text) is { } loaded)
        {
            dial = loaded;
            Changed?.Invoke();
        }
    }

    /// <summary>Pinned sites, then top sites from the library.</summary>
    public (IReadOnlyList<SpeedDialSite> Pinned, IReadOnlyList<SpeedDialSite> TopSites) Tiles()
    {
        IReadOnlyList<(string Title, string Url)> candidates = [];
        if (dial.ShowsTopSites && ProductivityIntegration.TopSites is { } topSites)
        {
            try { candidates = topSites(model, TopSiteLimit * 2); }
            catch (Exception) { }
        }
        return (dial.Pinned, dial.VisibleTopSites(candidates, TopSiteLimit));
    }

    /// <exception cref="SpeedDialException">Not a web address, or already pinned.</exception>
    public SpeedDialSite Add(string title, string url, int? index = null)
    {
        var site = dial.Add(title, url, index);
        Save();
        return site;
    }

    public bool TryAdd(string title, string url)
    {
        try
        {
            Add(title, url);
            return true;
        }
        catch (SpeedDialException)
        {
            return false;
        }
    }

    /// <exception cref="SpeedDialException">Not a web address, or another pinned site has it.</exception>
    public void Update(Guid id, string title, string url)
    {
        dial.Update(id, title, url);
        Save();
    }

    public void Remove(Guid id)
    {
        dial.Remove(id);
        Save();
    }

    public void Move(Guid id, int index)
    {
        dial.Move(id, index);
        Save();
    }

    public void Shift(Guid id, int offset)
    {
        dial.Shift(id, offset);
        Save();
    }

    /// <summary>Pins a top site where it is shown, so it stays.</summary>
    public void Pin(SpeedDialSite site) => TryAdd(site.Title, site.Url);

    public void HideTopSite(SpeedDialSite site) => HideTopSite(site.Url);

    public void HideTopSite(string url)
    {
        dial.HideTopSite(url);
        Save();
    }

    public void RestoreHiddenTopSites()
    {
        dial.RestoreHiddenTopSites();
        Save();
    }

    public void SetShowsTopSites(bool shows)
    {
        dial.ShowsTopSites = shows;
        Save();
    }

    public void SetShowsIcons(bool shows)
    {
        dial.ShowsIcons = shows;
        Save();
    }

    private void Save()
    {
        SaveError = null;
        files.Write(ProductivityFiles.SpeedDialFile, dial.ToJson(), error => SaveError = $"Speed Dial could not be saved: {error.Message}");
        Changed?.Invoke();
    }

    /// <summary>Automation's <c>speedDial</c> edits (same JSON as macOS); returns "ok" or why not.</summary>
    public string Edit(string json)
    {
        JsonObject? edit;
        try { edit = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return "bad JSON"; }
        if (edit is null || Text(edit["op"]) is not { } op) return "bad JSON";
        var url = Text(edit["url"]) ?? "";
        var key = SpeedDial.Identity(SpeedDial.NormalizedUrl(url) ?? url);
        var site = dial.Pinned.FirstOrDefault(s => SpeedDial.Identity(s.Url) == key);
        try
        {
            switch (op)
            {
                case "add": Add(Text(edit["title"]) ?? "", url); break;
                case "edit":
                    if (site is null) return "not pinned";
                    Update(site.Id, Text(edit["title"]) ?? site.Title, Text(edit["newURL"]) ?? site.Url);
                    break;
                case "remove":
                    if (site is null) return "not pinned";
                    Remove(site.Id);
                    break;
                case "move":
                    if (site is null || edit["to"] is not JsonValue to || !to.TryGetValue<int>(out var index)) return "not pinned";
                    Move(site.Id, index);
                    break;
                case "hide": HideTopSite(url); break;
                case "restoreHidden": RestoreHiddenTopSites(); break;
                case "showTopSites": SetShowsTopSites(Bool(edit["value"]) ?? true); break;
                case "showIcons": SetShowsIcons(Bool(edit["value"]) ?? true); break;
                default: return $"unknown op {op}";
            }
            return "ok";
        }
        catch (SpeedDialException error)
        {
            return error.Message;
        }
    }

    public JsonObject AutomationState()
    {
        var (_, topSites) = Tiles();
        return new JsonObject
        {
            ["pinned"] = new JsonArray(dial.Pinned.Select(s => (JsonNode)new JsonObject { ["title"] = s.Title, ["url"] = s.Url }).ToArray()),
            ["topSites"] = new JsonArray(topSites.Select(s => (JsonNode)new JsonObject { ["title"] = s.Title, ["url"] = s.Url }).ToArray()),
            ["hiddenTopSites"] = new JsonArray(dial.HiddenTopSites.Order(StringComparer.Ordinal).Select(k => (JsonNode)k).ToArray()),
            ["showsTopSites"] = dial.ShowsTopSites,
            ["showsIcons"] = dial.ShowsIcons,
            ["topSitesSource"] = ProductivityIntegration.TopSites is null ? "none" : "library",
            ["saveError"] = SaveError,
        };
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
