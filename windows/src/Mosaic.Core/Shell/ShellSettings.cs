using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Search;

namespace Mosaic.Core.Shell;

public enum StartupPage { StartPage, Homepage }

public enum NewTabPage { StartPage, Homepage }

/// <summary>An override of a system accessibility setting.</summary>
public enum AccessibilityOverride { System, On, Off }

public enum TextSize { Standard, Large, Larger }

public static class ShellSettingsInfo
{
    public static string Title(this StartupPage page) => page == StartupPage.StartPage ? "New Tab page" : "Homepage";
    public static string Title(this NewTabPage page) => page == NewTabPage.StartPage ? "New Tab page" : "Homepage";

    public static string Title(this AccessibilityOverride value) => value switch
    {
        AccessibilityOverride.System => "Use System Setting",
        AccessibilityOverride.On => "On",
        _ => "Off",
    };

    public static bool Resolve(this AccessibilityOverride value, bool system) => value switch
    {
        AccessibilityOverride.System => system,
        AccessibilityOverride.On => true,
        _ => false,
    };

    public static string Title(this TextSize size) => size.ToString();

    /// <summary>Multiplier for the shell's text and control sizes.</summary>
    public static double Scale(this TextSize size) => size switch
    {
        TextSize.Standard => 1,
        TextSize.Large => 1.12,
        _ => 1.25,
    };
}

/// <summary>
/// General and Accessibility preferences, plus window state the shell remembers. Port of
/// <c>Sources/Mosaic/Shell/ShellSettings.swift</c>; stored in <c>Shell\settings.json</c> under the
/// data folder (macOS keeps them in its defaults, under the same names with a "shell." prefix).
/// </summary>
/// <remarks>Single-threaded (UI thread); writes go to a background task. Owner: shell.</remarks>
public sealed class ShellSettings : INotifyPropertyChanged
{
    private readonly string? filePath;
    private readonly object saveLock = new();
    private Task saving = Task.CompletedTask;
    private string? pendingText;

    private string homepage = "";
    private bool showsHomeButton = true;
    private StartupPage startupPage;
    private NewTabPage newTabPage;
    private AccessibilityOverride reduceMotion;
    private AccessibilityOverride reduceTransparency;
    private TextSize textSize;
    private bool prominentFocusRings;
    private bool alwaysShowTabCloseButtons;
    private bool sidebarCollapsed;
    private bool sidebarOpen;
    private bool sidebarPeek;
    private SettingsCategory lastSettingsCategory = SettingsCategory.Mosaics;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string DefaultPath => Path.Combine(AppPaths.DataRoot, "Shell", "settings.json");

    public ShellSettings(string? filePath)
    {
        this.filePath = filePath;
        if (filePath is null) return;
        try
        {
            if (File.Exists(filePath) && JsonNode.Parse(File.ReadAllText(filePath, Encoding.UTF8)) is JsonObject o) Read(o);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private void Read(JsonObject o)
    {
        homepage = SwiftDecode.String(o, "homepage", "");
        showsHomeButton = SwiftDecode.Bool(o, "showsHomeButton", true);
        startupPage = SwiftDecode.Enum(o, "startupPage", StartupPage.StartPage);
        newTabPage = SwiftDecode.Enum(o, "newTabPage", NewTabPage.StartPage);
        reduceMotion = SwiftDecode.Enum(o, "reduceMotion", AccessibilityOverride.System);
        reduceTransparency = SwiftDecode.Enum(o, "reduceTransparency", AccessibilityOverride.System);
        textSize = SwiftDecode.Enum(o, "textSize", TextSize.Standard);
        prominentFocusRings = SwiftDecode.Bool(o, "prominentFocusRings", false);
        alwaysShowTabCloseButtons = SwiftDecode.Bool(o, "alwaysShowTabCloseButtons", false);
        sidebarCollapsed = SwiftDecode.Bool(o, "sidebarCollapsed", false);
        sidebarOpen = SwiftDecode.Bool(o, "sidebarOpen", false);
        lastSettingsCategory = SettingsCategories.FromId(SwiftDecode.String(o, "lastSettingsCategory", "")) ?? SettingsCategory.Mosaics;
    }

    // MARK: General

    /// <summary>Empty means the New Tab page.</summary>
    public string Homepage { get => homepage; set => Set(ref homepage, value ?? ""); }
    public bool ShowsHomeButton { get => showsHomeButton; set => Set(ref showsHomeButton, value); }
    public StartupPage StartupPage { get => startupPage; set => Set(ref startupPage, value); }
    public NewTabPage NewTabPage { get => newTabPage; set => Set(ref newTabPage, value); }

    // MARK: Accessibility

    public AccessibilityOverride ReduceMotion { get => reduceMotion; set => Set(ref reduceMotion, value); }
    /// <summary>On: solid surfaces instead of Mica and Acrylic.</summary>
    public AccessibilityOverride ReduceTransparency { get => reduceTransparency; set => Set(ref reduceTransparency, value); }
    public TextSize TextSize { get => textSize; set => Set(ref textSize, value); }
    /// <summary>Draws a thick accent ring around the keyboard-focused control.</summary>
    public bool ProminentFocusRings { get => prominentFocusRings; set => Set(ref prominentFocusRings, value); }
    /// <summary>Tabs always show their close button (not only on hover and when selected).</summary>
    public bool AlwaysShowTabCloseButtons { get => alwaysShowTabCloseButtons; set => Set(ref alwaysShowTabCloseButtons, value); }

    // MARK: Window state

    /// <summary>The user hid the vertical-tabs sidebar (when it is collapsible).</summary>
    public bool SidebarCollapsed { get => sidebarCollapsed; set => Set(ref sidebarCollapsed, value); }
    /// <summary>The user opened the sidebar of a horizontal-tabs layout.</summary>
    public bool SidebarOpen { get => sidebarOpen; set => Set(ref sidebarOpen, value); }
    /// <summary>An auto-hiding sidebar is showing over the page. Not saved.</summary>
    public bool SidebarPeek { get => sidebarPeek; set => Set(ref sidebarPeek, value, save: false); }
    /// <summary>Settings category to reopen with Ctrl+,.</summary>
    public SettingsCategory LastSettingsCategory { get => lastSettingsCategory; set => Set(ref lastSettingsCategory, value); }

    /// <summary>The homepage as a loadable address, or null when it is the New Tab page or not valid.</summary>
    public string? HomepageUrl(SearchEngine searchEngine)
    {
        var trimmed = homepage.Trim();
        if (trimmed.Length == 0 || !AddressResolver.IsAddress(trimmed)) return null;
        return AddressResolver.Resolve(trimmed, searchEngine);
    }

    public JsonObject ToJson() => new()
    {
        ["homepage"] = homepage,
        ["showsHomeButton"] = showsHomeButton,
        ["startupPage"] = startupPage.Raw(),
        ["newTabPage"] = newTabPage.Raw(),
        ["reduceMotion"] = reduceMotion.Raw(),
        ["reduceTransparency"] = reduceTransparency.Raw(),
        ["textSize"] = textSize.Raw(),
        ["prominentFocusRings"] = prominentFocusRings,
        ["alwaysShowTabCloseButtons"] = alwaysShowTabCloseButtons,
        ["sidebarCollapsed"] = sidebarCollapsed,
        ["sidebarOpen"] = sidebarOpen,
        ["lastSettingsCategory"] = lastSettingsCategory.Id(),
    };

    private void Set<T>(ref T field, T value, bool save = true, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (save) Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Save()
    {
        if (filePath is null) return;
        var text = SwiftJson.Write(ToJson());
        lock (saveLock)
        {
            var first = pendingText is null;
            pendingText = text;
            if (first) saving = saving.ContinueWith(_ => WritePending(), TaskScheduler.Default);
        }
    }

    private void WritePending()
    {
        string? text;
        lock (saveLock)
        {
            text = pendingText;
            pendingText = null;
        }
        if (text is null || filePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temporary = filePath + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, filePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public Task FlushAsync()
    {
        lock (saveLock) return saving;
    }
}
