using Mosaic.Core.Shell;

namespace Mosaic.Core.Browser;

/// <summary>
/// The shell's state and actions on the model: the Mosaics, General/Accessibility settings,
/// which Settings page is open (Settings are shown inside the main window), the sidebar, Home.
/// Port of <c>Sources/Mosaic/Shell/BrowserModel+Shell.swift</c>. Owner: shell.
/// </summary>
/// <remarks>
/// Created lazily on first use (the shell asks while composing the first window): reading the
/// two small preference files is the only work, and it is needed for the first frame.
/// </remarks>
public sealed partial class BrowserModel
{
    private MosaicStore? mosaics;
    private ShellSettings? shellSettings;
    private SettingsCategory? settingsPage;
    private string settingsQuery = "";

    /// <summary>Tests: a folder for the shell's files instead of the data folder; null with <see cref="ShellInMemory"/> keeps them in memory.</summary>
    internal string? ShellStorageFolder { get; set; }
    internal bool ShellInMemory { get; set; }

    private string? ShellFile(string name) =>
        ShellInMemory ? null : Path.Combine(ShellStorageFolder ?? Path.Combine(AppPaths.DataRoot, "Shell"), name);

    /// <summary>The Mosaics (layouts) and which one is active.</summary>
    public MosaicStore Mosaics
    {
        get
        {
            if (mosaics is null)
            {
                mosaics = new MosaicStore(ShellFile("mosaics.json"));
                mosaics.Changed += () => ShellChanged?.Invoke();
            }
            return mosaics;
        }
    }

    /// <summary>General and Accessibility preferences and remembered window state.</summary>
    public ShellSettings ShellSettings
    {
        get
        {
            if (shellSettings is null)
            {
                shellSettings = new ShellSettings(ShellFile("settings.json"));
                shellSettings.PropertyChanged += (_, _) => ShellChanged?.Invoke();
            }
            return shellSettings;
        }
    }

    /// <summary>Anything the shell draws from changed: the layout, a preview, a shell setting.</summary>
    public event Action? ShellChanged;

    /// <summary>The Settings page shown in the window, or null when Settings are closed.</summary>
    public SettingsCategory? SettingsPage
    {
        get => settingsPage;
        private set
        {
            if (Set(ref settingsPage, value)) Notify(nameof(IsSettingsOpen));
        }
    }

    public bool IsSettingsOpen => settingsPage is not null;

    /// <summary>The Settings search field's text.</summary>
    public string SettingsQuery { get => settingsQuery; set => Set(ref settingsQuery, value ?? ""); }

    /// <summary>Opens Settings at the given category, or the one shown last.</summary>
    public void ShowSettings(SettingsCategory? category = null)
    {
        var target = category ?? ShellSettings.LastSettingsCategory;
        ShellSettings.LastSettingsCategory = target;
        SettingsPage = target;
    }

    /// <summary>Esc / Done: back to the page; a Mosaic preview that was not applied ends.</summary>
    public void DismissSettings()
    {
        Mosaics.EndPreview();
        SettingsQuery = "";
        SettingsPage = null;
    }

    public void ToggleSettings()
    {
        if (settingsPage is null) ShowSettings();
        else DismissSettings();
    }

    /// <summary>Applies a preset from a menu, Settings or automation, and says so.</summary>
    public void ApplyMosaic(MosaicPresetID preset)
    {
        Mosaics.Apply(preset);
        Notice = $"Mosaic: {Mosaics.Layout.Name}";
    }

    /// <summary>Shows or hides the sidebar (vertical tabs, or a horizontal layout's sidebar).</summary>
    public void ToggleSidebar()
    {
        var layout = Mosaics.EffectiveLayout;
        if (layout.TabPlacement.IsVertical())
        {
            switch (layout.Sidebar)
            {
                case SidebarBehavior.Collapsible: ShellSettings.SidebarCollapsed = !ShellSettings.SidebarCollapsed; break;
                case SidebarBehavior.AutoHide: ShellSettings.SidebarPeek = !ShellSettings.SidebarPeek; break;
            }
        }
        else if (layout.Contains(ShellComponent.SidebarToggle))
        {
            ShellSettings.SidebarOpen = !ShellSettings.SidebarOpen;
        }
    }

    /// <summary>Whether the sidebar is part of the layout right now (not counting an auto-hide peek).</summary>
    public bool IsSidebarVisible
    {
        get
        {
            var layout = Mosaics.EffectiveLayout;
            if (layout.TabPlacement.IsVertical())
            {
                return layout.Sidebar switch
                {
                    SidebarBehavior.Shown => true,
                    SidebarBehavior.Collapsible => !ShellSettings.SidebarCollapsed,
                    _ => false,
                };
            }
            return layout.Contains(ShellComponent.SidebarToggle) && ShellSettings.SidebarOpen;
        }
    }

    /// <summary>Edge's tab actions: switches the active layout between horizontal and vertical tabs.</summary>
    public void ToggleVerticalTabs()
    {
        var layout = Mosaics.Layout;
        var target = layout.TabPlacement.IsVertical() ? TabPlacement.Top : TabPlacement.SidebarLeading;
        Mosaics.ReplaceActiveLayout(layout.WithTabPlacement(target));
        if (target.IsVertical()) ShellSettings.SidebarCollapsed = false;
    }

    /// <summary>New tab as the user configured it (New Tab page or homepage).</summary>
    public void OpenNewTabFromShell()
    {
        if (ShellSettings.NewTabPage == Shell.NewTabPage.Homepage && ShellSettings.HomepageUrl(SearchEngine) is { } url) NewTab(url);
        else NewTab();
    }

    /// <summary>Home button: the homepage in this tab, or a New Tab page when none is set.</summary>
    public void GoHome()
    {
        if (ShellSettings.HomepageUrl(SearchEngine) is { } url)
        {
            if (SelectedTab is { } tab)
            {
                EndAddressEditing();
                Load(url, tab);
                FocusContent();
            }
            else
            {
                NewTab(url);
            }
        }
        else if (SelectedTab?.IsStartPage != true)
        {
            NewTab();
        }
    }

    /// <summary>The first tab at launch, according to Settings › General › Startup.</summary>
    public void ApplyStartupPage()
    {
        if (ShellSettings.StartupPage != Shell.StartupPage.Homepage || ShellSettings.HomepageUrl(SearchEngine) is not { } url) return;
        if (SelectedTab is { IsStartPage: true } tab && Tabs.Count == 1) Load(url, tab);
    }

    /// <summary>Mosaic's own page the selected tab shows (<c>mosaic://bookmarks</c> → "bookmarks"), or null.</summary>
    public string? ShellInternalPage => ShellInternalHost(SelectedTab?.Url);

    public static string? ShellInternalHost(string? url)
    {
        const string scheme = "mosaic://";
        if (url is null || !url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url[scheme.Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var host = (end >= 0 ? rest[..end] : rest).ToLowerInvariant();
        return host.Length == 0 ? null : host;
    }

    /// <summary>Opens Mosaic's own page <c>mosaic://host</c> in the selected tab if it is a New Tab page, else in a new tab.</summary>
    public void OpenInternalPage(string host)
    {
        var url = $"mosaic://{host}";
        DismissSettings();
        if (SelectedTab is { IsStartPage: true } tab) Load(url, tab);
        else NewTab(url);
    }
}
