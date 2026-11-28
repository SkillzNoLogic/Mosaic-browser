using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Shell;

/// <summary>
/// Automation commands and <c>dump</c> fields for the shell (port of
/// <c>Sources/Mosaic/Shell/ShellAutomation.swift</c>; same command names):
/// <code>
/// mosaic &lt;zenArc|chrome|opera|edge|operaGX&gt;  apply a preset ("safari" applies Edge, as an import would)
/// previewMosaic &lt;preset|off&gt;            preview without applying
/// restoreMosaic &lt;preset&gt;                restore a preset's defaults
/// mosaicSet &lt;key&gt;=&lt;value&gt;               glass|materials=on|off, scheme=system|light|dark,
///                                         density=compact|regular|comfortable,
///                                         tabs=top|sidebarLeading|sidebarTrailing, tabStyle=chrome|opera|safari,
///                                         address=omnibox|compact|unified, sidebar=shown|collapsible|autoHide,
///                                         bookmarksBar=on|off, gx=on|off, inset=&lt;dip&gt;, radius=&lt;dip&gt;,
///                                         pageRadius=&lt;dip&gt;, accent=&lt;#hex|system&gt;, sidebarWidth=&lt;dip&gt;
/// importLayout &lt;path.json&gt; / exportLayout &lt;path.json&gt;   (macOS's file format, byte for byte)
/// settings|openSettings [category id] / closeSettings / settingsSearch &lt;query&gt;
/// sidebar show|hide|toggle / verticalTabs on|off|toggle
/// shellSet &lt;key&gt;=&lt;value&gt;                homepage=&lt;url&gt;, startup=startPage|homepage, newTab=startPage|homepage,
///                                         homeButton=on|off, reduceMotion=system|on|off, reduceTransparency=…,
///                                         textSize=standard|large|larger, focusRings=on|off, tabCloseButtons=on|off
/// home
/// </code>
/// <c>dump</c> keys: <c>mosaic</c>, <c>previewMosaic</c>, <c>layout</c> (the effective layout's JSON),
/// <c>settingsCategory</c>, <c>sidebarVisible</c>, and <c>shell</c> (everything else: presets,
/// customised presets, settings search, plan, shell settings).
/// Owner: shell.
/// </summary>
public static class ShellAutomation
{
    private static int registered;

    /// <summary>Registers the commands and dump fields once (safe to call again).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1) return;
        AutomationRegistry.Register(HandleAsync);
        AutomationRegistry.RegisterState("mosaic", m => m.Mosaics.ActivePreset.Raw());
        AutomationRegistry.RegisterState("previewMosaic", m => m.Mosaics.PreviewedPreset?.Raw() ?? "");
        AutomationRegistry.RegisterState("layout", m => JsonNode.Parse(m.Mosaics.EffectiveLayout.JsonText(pretty: false)));
        AutomationRegistry.RegisterState("settingsCategory", m => m.SettingsPage?.Id() ?? "");
        AutomationRegistry.RegisterState("sidebarVisible", m => m.IsSidebarVisible);
        AutomationRegistry.RegisterState("shell", State);
    }

    /// <summary>Extra UI facts the app adds to <c>dump</c>'s <c>shell</c> object (the WinUI shell sets this).</summary>
    public static Func<BrowserModel, JsonObject?>? UiState { get; set; }

    public static JsonObject State(BrowserModel model)
    {
        var store = model.Mosaics;
        var settings = model.ShellSettings;
        var layout = store.EffectiveLayout;
        var plan = new ShellPlan(layout, settings.ShowsHomeButton, model.IsSidebarVisible, new ShellMetrics(settings.TextSize.Scale(), layout.Appearance.Density));
        var matches = new JsonArray();
        foreach (var match in SettingsSearch.Search(model.SettingsQuery))
        {
            matches.Add(new JsonObject
            {
                ["id"] = match.Category.Id(),
                ["labels"] = new JsonArray([.. match.Labels.Select(l => (JsonNode)JsonValue.Create(l))]),
            });
        }
        var state = new JsonObject
        {
            ["activePreset"] = store.ActivePreset.Raw(),
            ["activeName"] = store.Layout.Name,
            ["presets"] = new JsonArray([.. Enum.GetValues<MosaicPresetID>().Select(p => (JsonNode)JsonValue.Create(p.Raw()))]),
            ["customized"] = new JsonArray([.. Enum.GetValues<MosaicPresetID>().Where(store.IsCustomized).Select(p => (JsonNode)JsonValue.Create(p.Raw()))]),
            ["gx"] = layout.HasGXStyling,
            ["settingsOpen"] = model.IsSettingsOpen,
            ["settingsQuery"] = model.SettingsQuery,
            ["settingsMatches"] = matches,
            ["internalPage"] = model.InternalPageHost ?? "",
            ["parts"] = new JsonArray([.. plan.Parts.Select(p => (JsonNode)JsonValue.Create(p))]),
            ["captionHost"] = plan.CaptionHost.Raw(),
            ["settings"] = settings.ToJson(),
        };
        if (UiState?.Invoke(model) is { } ui)
        {
            foreach (var (key, value) in ui) state[key] = value?.DeepClone();
        }
        return state;
    }

    private static Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output) =>
        Task.FromResult(Handle(model, command, value));

    /// <summary>Runs a shell command; null when <paramref name="command"/> is not one.</summary>
    public static AutomationReply? Handle(BrowserModel model, string command, string value)
    {
        var store = model.Mosaics;
        switch (command)
        {
            case "mosaic":
                if (value == "operaGX" || value == "opera-gx")
                {
                    model.ApplyMosaic(MosaicPresetID.Opera);
                    if (!store.Layout.HasGXStyling) store.ReplaceActiveLayout(store.Layout.WithGXStyling());
                    return AutomationRegistry.Ok();
                }
                if (ParsePreset(value) is not { } preset) return AutomationRegistry.Error($"Unknown Mosaic \"{value}\".");
                model.ApplyMosaic(preset);
                return AutomationRegistry.Ok();
            case "previewMosaic":
                if (ParsePreset(value) is { } previewed) store.Preview(previewed);
                else store.EndPreview();
                return AutomationRegistry.Ok();
            case "restoreMosaic":
                if (ParsePreset(value) is not { } restored) return AutomationRegistry.Error($"Unknown Mosaic \"{value}\".");
                store.RestoreDefaults(restored);
                return AutomationRegistry.Ok();
            case "mosaicSet":
                return SetLayoutValue(value, model);
            case "importLayout":
                try
                {
                    store.Import(MosaicLayout.FromJson(File.ReadAllBytes(value)));
                    return AutomationRegistry.Ok();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    return AutomationRegistry.Error($"The layout could not be imported: {error.Message}");
                }
            case "exportLayout":
                if (value.Length == 0) return AutomationRegistry.Error("exportLayout needs the path to write to.");
                File.WriteAllBytes(value, store.Layout.JsonData());
                return AutomationRegistry.Ok();
            case "settings":
            case "openSettings":
                if (value.Length == 0)
                {
                    model.ShowSettings();
                    return AutomationRegistry.Ok();
                }
                if (SettingsCategories.FromId(value) is not { } category) return AutomationRegistry.Error($"Unknown settings category \"{value}\".");
                model.ShowSettings(category);
                return AutomationRegistry.Ok();
            case "closeSettings":
                model.DismissSettings();
                return AutomationRegistry.Ok();
            case "settingsSearch":
                if (!model.IsSettingsOpen) model.ShowSettings();
                model.SettingsQuery = value;
                return AutomationRegistry.Ok();
            case "sidebar":
                var visible = model.IsSidebarVisible;
                if (value == "toggle" || (value == "show" && !visible) || (value == "hide" && visible)) model.ToggleSidebar();
                return AutomationRegistry.Ok();
            case "verticalTabs":
                var vertical = store.Layout.TabPlacement.IsVertical();
                if (value == "toggle" || (value == "on" && !vertical) || (value == "off" && vertical)) model.ToggleVerticalTabs();
                return AutomationRegistry.Ok();
            case "shellSet":
                return SetShellValue(value, model);
            case "home":
                model.GoHome();
                return AutomationRegistry.Ok();
            default:
                return null;
        }
    }

    private static MosaicPresetID? ParsePreset(string value) =>
        value == "safari" ? MosaicPresetID.Edge : JsonEnum.Parse<MosaicPresetID>(value);

    private static (string Key, string Value)? Split(string assignment)
    {
        var index = assignment.IndexOf('=');
        return index < 0 ? null : (assignment[..index], assignment[(index + 1)..]);
    }

    private static AutomationReply SetLayoutValue(string assignment, BrowserModel model)
    {
        if (Split(assignment) is not var (key, value)) return AutomationRegistry.Error("Expected <key>=<value>.");
        var on = value is "on" or "true";
        var store = model.Mosaics;
        double? number = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        switch (key)
        {
            case "tabs":
                if (JsonEnum.Parse<TabPlacement>(value) is not { } placement) return AutomationRegistry.Error($"Unknown tab placement \"{value}\".");
                store.ReplaceActiveLayout(store.Layout.WithTabPlacement(placement));
                return AutomationRegistry.Ok();
            case "gx":
                store.ReplaceActiveLayout(on ? store.Layout.WithGXStyling() : store.Layout.WithoutGXStyling());
                return AutomationRegistry.Ok();
        }
        string? error = null;
        store.Update(layout =>
        {
            var a = layout.Appearance;
            switch (key)
            {
                case "glass" or "materials" or "material":
                    a.Material = on || value is "liquidGlass" or "mica" or "acrylic" ? ChromeMaterial.LiquidGlass : ChromeMaterial.Solid;
                    break;
                case "scheme": a.ColorScheme = JsonEnum.Parse<ColorSchemePreference>(value) ?? a.ColorScheme; break;
                case "density": a.Density = JsonEnum.Parse<ShellDensity>(value) ?? a.Density; break;
                case "tabStyle": layout.TabStyle = JsonEnum.Parse<TabStyle>(value) ?? layout.TabStyle; break;
                case "address": layout.AddressBar = JsonEnum.Parse<AddressBarStyle>(value) ?? layout.AddressBar; break;
                case "sidebar": layout.Sidebar = JsonEnum.Parse<SidebarBehavior>(value) ?? layout.Sidebar; break;
                case "bookmarksBar": layout.BookmarksBar = on; break;
                case "inset": a.ContentInset = number ?? a.ContentInset; break;
                case "radius": a.CornerRadius = number ?? a.CornerRadius; break;
                case "pageRadius": a.ContentCornerRadius = number ?? a.ContentCornerRadius; break;
                case "sidebarWidth": layout.SidebarWidth = number ?? layout.SidebarWidth; break;
                case "accent": a.Accent = value; break;
                case "glow": a.Glow = on; break;
                default: error = $"Unknown layout setting \"{key}\"."; break;
            }
        });
        return error is null ? AutomationRegistry.Ok() : AutomationRegistry.Error(error);
    }

    private static AutomationReply SetShellValue(string assignment, BrowserModel model)
    {
        if (Split(assignment) is not var (key, value)) return AutomationRegistry.Error("Expected <key>=<value>.");
        var s = model.ShellSettings;
        var on = value is "on" or "true";
        switch (key)
        {
            case "homepage": s.Homepage = value; break;
            case "startup": s.StartupPage = JsonEnum.Parse<StartupPage>(value) ?? s.StartupPage; break;
            case "newTab": s.NewTabPage = JsonEnum.Parse<NewTabPage>(value) ?? s.NewTabPage; break;
            case "homeButton": s.ShowsHomeButton = on; break;
            case "reduceMotion": s.ReduceMotion = JsonEnum.Parse<AccessibilityOverride>(value) ?? s.ReduceMotion; break;
            case "reduceTransparency": s.ReduceTransparency = JsonEnum.Parse<AccessibilityOverride>(value) ?? s.ReduceTransparency; break;
            case "textSize": s.TextSize = JsonEnum.Parse<TextSize>(value) ?? s.TextSize; break;
            case "focusRings": s.ProminentFocusRings = on; break;
            case "tabCloseButtons": s.AlwaysShowTabCloseButtons = on; break;
            default: return AutomationRegistry.Error($"Unknown shell setting \"{key}\".");
        }
        return AutomationRegistry.Ok();
    }
}
