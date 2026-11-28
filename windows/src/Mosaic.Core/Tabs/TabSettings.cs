using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Tabs;

/// <summary>What a launch shows.</summary>
public enum TabStartup
{
    /// <summary>Every tab, group and workspace from the last run (tabs stay unloaded until selected).</summary>
    PreviousSession,
    /// <summary>One new tab. Workspaces are always kept; pinned tabs too if <see cref="TabSettings.KeepPinnedTabs"/>.</summary>
    StartPage,
}

/// <summary>Sleeping tabs: unloading background tabs to free memory.</summary>
public sealed record SleepSettings
{
    public static readonly int[] MinuteChoices = [5, 15, 30, 60, 120, 240, 480];

    public bool UnloadInactiveTabs { get; init; } = true;

    private readonly int inactiveMinutes = 60;
    /// <summary>A background tab is unloaded after this many minutes without being selected (1…1440).</summary>
    public int InactiveMinutes { get => inactiveMinutes; init => inactiveMinutes = Math.Clamp(value, 1, 1440); }

    public bool NeverUnloadPinned { get; init; } = true;

    /// <summary>When Windows reports low memory, unload sooner (warning) or every eligible background tab (critical).</summary>
    public bool RespondToMemoryPressure { get; init; } = true;
}

/// <summary>
/// The user's choices on the Tabs &amp; Workspaces and Performance settings pages. Port of
/// <c>Sources/MosaicCore/Tabs/TabSettings.swift</c>, stored as the same JSON (in
/// <c>tab-settings.json</c> on Windows). Decoding is tolerant: a key that is missing or out of
/// range falls back to its default, so a new setting never resets the others.
/// </summary>
public sealed record TabSettings
{
    public static readonly (int Min, int Max) RecentlyClosedRange = (5, 100);

    public TabStartup Startup { get; init; } = TabStartup.PreviousSession;
    /// <summary>With <see cref="TabStartup.StartPage"/>, still bring back pinned tabs.</summary>
    public bool KeepPinnedTabs { get; init; } = true;
    /// <summary>Ctrl+W on a pinned tab only closes it when pressed twice in a row.</summary>
    public bool ProtectPinnedTabs { get; init; } = true;
    /// <summary>When an address is already open in another tab, offer to switch to it.</summary>
    public bool OfferSwitchToOpenTab { get; init; } = true;

    private readonly int recentlyClosedLimit = 25;
    /// <summary>How many closed tabs (or closed groups/workspaces) Ctrl+Shift+T can bring back.</summary>
    public int RecentlyClosedLimit
    {
        get => recentlyClosedLimit;
        init => recentlyClosedLimit = Math.Clamp(value, RecentlyClosedRange.Min, RecentlyClosedRange.Max);
    }

    public SleepSettings Sleep { get; init; } = new();

    public static string StartupId(TabStartup startup) => startup == TabStartup.StartPage ? "startPage" : "previousSession";

    public static TabStartup? StartupFromId(string? id) => id switch
    {
        "previousSession" => TabStartup.PreviousSession,
        "startPage" => TabStartup.StartPage,
        _ => null,
    };

    /// <summary>Settings from stored JSON; defaults for anything missing or unreadable.</summary>
    public static TabSettings Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return new();
        }
        if (o is null) return new();
        var defaults = new TabSettings();
        var sleep = o["sleep"] as JsonObject;
        var sleepDefaults = new SleepSettings();
        return new TabSettings
        {
            Startup = StartupFromId(SessionJson.Text(o["startup"])) ?? defaults.Startup,
            KeepPinnedTabs = SessionJson.Bool(o["keepPinnedTabs"]) ?? defaults.KeepPinnedTabs,
            ProtectPinnedTabs = SessionJson.Bool(o["protectPinnedTabs"]) ?? defaults.ProtectPinnedTabs,
            OfferSwitchToOpenTab = SessionJson.Bool(o["offerSwitchToOpenTab"]) ?? defaults.OfferSwitchToOpenTab,
            RecentlyClosedLimit = SessionJson.Int(o["recentlyClosedLimit"]) ?? defaults.RecentlyClosedLimit,
            Sleep = sleep is null ? sleepDefaults : new SleepSettings
            {
                UnloadInactiveTabs = SessionJson.Bool(sleep["unloadInactiveTabs"]) ?? sleepDefaults.UnloadInactiveTabs,
                InactiveMinutes = SessionJson.Int(sleep["inactiveMinutes"]) ?? sleepDefaults.InactiveMinutes,
                NeverUnloadPinned = SessionJson.Bool(sleep["neverUnloadPinned"]) ?? sleepDefaults.NeverUnloadPinned,
                RespondToMemoryPressure = SessionJson.Bool(sleep["respondToMemoryPressure"]) ?? sleepDefaults.RespondToMemoryPressure,
            },
        };
    }

    public JsonObject ToJson() => new()
    {
        ["keepPinnedTabs"] = KeepPinnedTabs,
        ["offerSwitchToOpenTab"] = OfferSwitchToOpenTab,
        ["protectPinnedTabs"] = ProtectPinnedTabs,
        ["recentlyClosedLimit"] = RecentlyClosedLimit,
        ["sleep"] = new JsonObject
        {
            ["inactiveMinutes"] = Sleep.InactiveMinutes,
            ["neverUnloadPinned"] = Sleep.NeverUnloadPinned,
            ["respondToMemoryPressure"] = Sleep.RespondToMemoryPressure,
            ["unloadInactiveTabs"] = Sleep.UnloadInactiveTabs,
        },
        ["startup"] = StartupId(Startup),
    };

    public string Encode() => ToJson().ToJsonString();

    /// <summary>
    /// Applies one <c>key=value</c> assignment (the automation command <c>tabSetting</c> and the
    /// macOS key names). Returns null when the key or value is not understood.
    /// </summary>
    public TabSettings? With(string key, string value)
    {
        var flag = value == "true";
        int? number = int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        return key switch
        {
            "startup" => StartupFromId(value) is { } startup ? this with { Startup = startup } : null,
            "keepPinnedTabs" => this with { KeepPinnedTabs = flag },
            "protectPinnedTabs" => this with { ProtectPinnedTabs = flag },
            "offerSwitchToOpenTab" => this with { OfferSwitchToOpenTab = flag },
            "recentlyClosedLimit" => number is { } limit ? this with { RecentlyClosedLimit = limit } : null,
            "unloadInactiveTabs" => this with { Sleep = Sleep with { UnloadInactiveTabs = flag } },
            "inactiveMinutes" => number is { } minutes ? this with { Sleep = Sleep with { InactiveMinutes = minutes } } : null,
            "neverUnloadPinned" => this with { Sleep = Sleep with { NeverUnloadPinned = flag } },
            "respondToMemoryPressure" => this with { Sleep = Sleep with { RespondToMemoryPressure = flag } },
            _ => null,
        };
    }
}

/// <summary>Where <see cref="TabSettings"/> live: <c>tab-settings.json</c> next to <c>settings.json</c>.</summary>
public sealed class TabSettingsStore(string path)
{
    public static string DefaultPath => System.IO.Path.Combine(AppPaths.DataRoot, "tab-settings.json");

    public string Path { get; } = path;

    /// <summary>The stored settings; defaults when the file is missing or damaged.</summary>
    public TabSettings Load()
    {
        try
        {
            return TabSettings.Decode(File.ReadAllText(Path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Writes the settings; a failure means the choice lasts for this run only.</summary>
    public void Save(TabSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, settings.Encode());
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
