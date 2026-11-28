using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Shell;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// Settings › General (port of macOS's <c>GeneralSettingsPane</c>): startup, homepage, the Home
/// button and new tabs. The default engine for new tabs lives in Settings › Engine (platform area).
/// </summary>
internal sealed class GeneralSettingsPage : Grid
{
    private readonly ShellRoot shell;
    private TextBlock? warning;
    private TextBlock? startupNote;

    public GeneralSettingsPage(ShellRoot shell)
    {
        this.shell = shell;
        Build();
    }

    private void Build()
    {
        var model = shell.Model;
        var settings = model.ShellSettings;
        var kit = new SettingsKit(shell.Theme);

        kit.Section("Startup");
        kit.Card("Mosaic opens with", null, SettingsKit.Combo(Enum.GetValues<StartupPage>().Select(p => (p, p.Title())), settings.StartupPage,
            p => { settings.StartupPage = p; Validate(); }, "Mosaic opens with"));
        startupNote = kit.Note("Set a homepage below; until then Mosaic opens with the New Tab page.");

        kit.Section("Homepage");
        var homepage = new TextBox { Text = settings.Homepage, PlaceholderText = "New Tab page", Width = 320, IsSpellCheckEnabled = false };
        AutomationProperties.SetName(homepage, "Homepage address");
        homepage.TextChanged += (_, _) =>
        {
            settings.Homepage = homepage.Text;
            Validate();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var current = SettingsKit.Button("Use Current Page", () =>
        {
            if (model.SelectedTab is { IsStartPage: false, Failure: null } tab && BrowserModelInternal(tab.Url)) homepage.Text = tab.Url;
        });
        current.IsEnabled = model.SelectedTab is { IsStartPage: false } t && BrowserModelInternal(t.Url);
        buttons.Children.Add(current);
        buttons.Children.Add(SettingsKit.Button("Use New Tab Page", () => homepage.Text = ""));
        warning = new TextBlock { Foreground = shell.Theme.AccentText, FontSize = 12, Text = "That doesn’t look like a web address." };
        var below = new StackPanel { Spacing = 8 };
        below.Children.Add(buttons);
        below.Children.Add(warning);
        kit.Card("Homepage", "Home (Alt+Home) opens it in the current tab, or a New Tab page if none is set.", homepage, below);
        kit.Card("Show Home button", "The button appears where the Mosaic places it.",
            SettingsKit.Toggle(settings.ShowsHomeButton, on => settings.ShowsHomeButton = on, "Show Home button"));

        kit.Section("New Tabs");
        kit.Card("New tabs open with", null, SettingsKit.Combo(Enum.GetValues<NewTabPage>().Select(p => (p, p.Title())), settings.NewTabPage,
            p => settings.NewTabPage = p, "New tabs open with"));
        kit.Card("Engine for new tabs", "Mozilla (Gecko) or Chromium (WebView2).",
            SettingsKit.Button("Engine Settings…", () => model.ShowSettings(SettingsCategory.Engine)));

        Children.Clear();
        Children.Add(kit.Scroller());
        Validate();
    }

    private static bool BrowserModelInternal(string url) => Mosaic.Core.Browser.BrowserModel.InternalHost(url) is null;

    private void Validate()
    {
        var settings = shell.Model.ShellSettings;
        var valid = settings.HomepageUrl(shell.Model.SearchEngine) is not null;
        if (warning is not null) warning.Visibility = settings.Homepage.Length > 0 && !valid ? Visibility.Visible : Visibility.Collapsed;
        if (startupNote is not null) startupNote.Visibility = settings.StartupPage == StartupPage.Homepage && !valid ? Visibility.Visible : Visibility.Collapsed;
    }
}
