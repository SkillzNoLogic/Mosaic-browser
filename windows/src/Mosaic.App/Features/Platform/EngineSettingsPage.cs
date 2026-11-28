using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using Mosaic.Core.Platform;
using static Mosaic.App.Features.Platform.PlatformUi;

namespace Mosaic.App.Features.Platform;

/// <summary>
/// Settings › Engine: the two engines, the default engine for new tabs, the private Gecko runtime
/// (install, update, remove) and per-site engine rules. Port of <c>EngineSettingsPane.swift</c>.
/// </summary>
internal sealed class EngineSettingsPage : PlatformPage
{
    private readonly BrowserModel model;

    public EngineSettingsPage(BrowserModel model) : base("Engine")
    {
        this.model = model;
        var platform = model.Platform;
        Body.Children.Add(Note(
            "Mosaic runs both engines side by side, and each tab uses one of them. Switching a tab (Ctrl+Shift+E, or the engine " +
            "switch in the toolbar) needs no restart: the page stays on screen until the other engine has loaded it, and stays " +
            "where it was if the other engine can’t."));

        Body.Children.Add(Header("Engines"));
        Body.Children.Add(Section(ChromiumCard, model));
        Body.Children.Add(Section(GeckoCard, model, new GeckoStatusSignal(model.Gecko), platform.Runtime));

        Body.Children.Add(Header("Default engine"));
        Body.Children.Add(Section(DefaultEngine, model));

        Body.Children.Add(Header("Mozilla engine runtime"));
        Body.Children.Add(Section(RuntimeSection, platform.Runtime, model));

        Body.Children.Add(Header("Site rules"));
        Body.Children.Add(Section(CurrentSiteRule, model));
        Body.Children.Add(Section(RulesList, platform.Rules));
        Body.Children.Add(Card(AddRule()));
        Body.Children.Add(Note(
            "A rule picks the engine when you open the site from the address bar, the start page or as a new tab. It covers the " +
            "site’s subdomains unless you switch that off. Links you follow inside a page stay in the tab’s engine, so a page is " +
            "never reloaded mid-visit."));
    }

    // ---- Engine cards ----

    private static StackPanel CardLayout(EngineKind kind, string title, string detail, UIElement status, params UIElement[] capabilities)
    {
        var icon = Icons.Make(kind == EngineKind.Gecko ? "flame" : "globe", 20);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(icon);
        var text = Column(2, Text(title, strong: true), Note(detail));
        Grid.SetColumn(text, 1);
        heading.Children.Add(text);
        Grid.SetColumn((FrameworkElement)status, 2);
        heading.Children.Add(status);
        var list = Column(4, capabilities);
        list.Margin = new Thickness(32, 4, 0, 0);
        var card = Column(6, heading, list);
        AutomationProperties.SetName(card, title);
        return card;
    }

    private void ChromiumCard(StackPanel panel)
    {
        var version = ChromiumVersion.Value;
        panel.Children.Add(Card(CardLayout(EngineKind.Chromium, "Chromium (Blink)",
            model.ChromiumAvailable ? $"Microsoft Edge WebView2 Runtime {version}" : "The WebView2 Runtime is not installed",
            model.ChromiumAvailable ? Badge("Available", Tone.Positive) : Badge("Unavailable", Tone.Negative),
            CapabilityRow(Capability.Yes, "Runs inside Mosaic’s window, updated by Windows with Microsoft Edge"),
            CapabilityRow(Capability.Yes, "Built-in ad and tracker blocker (network filtering)"),
            CapabilityRow(Capability.Info, "Chrome extensions load here unconverted where WebView2 supports them; most run in Mozilla tabs"),
            CapabilityRow(Capability.Info, "No Microsoft or Google account, sync or SmartScreen services"))));
        if (!model.ChromiumAvailable)
        {
            panel.Children.Add(Note("Get the “Evergreen Bootstrapper” from developer.microsoft.com/microsoft-edge/webview2 (it is part of Windows 11 and most Windows 10 installations), then restart Mosaic."));
        }
    }

    /// <summary>The installed WebView2 Runtime's version (registry lookup, once).</summary>
    private static readonly Lazy<string> ChromiumVersion = new(() =>
    {
        try
        {
            return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "unknown";
        }
        catch (Exception)
        {
            return "not installed";
        }
    });

    private void GeckoCard(StackPanel panel)
    {
        var runtime = model.Gecko.Runtime;
        var installer = model.Platform.Runtime;
        var detail = runtime is null
            ? model.GeckoRuntimeMissing ? "No Gecko runtime installed" : $"Firefox ESR {installer.InstalledRuntime?.Version} · Mosaic’s private copy"
            : $"{runtime.Name} {runtime.Version} · {SourceLabel(runtime, installer)}";
        Border status = model.GeckoRuntimeMissing ? Badge("Not installed", Tone.Warning) : model.Gecko.Status switch
        {
            GeckoStatus.Running => Badge("Running", Tone.Positive),
            GeckoStatus.Starting => Badge("Starting…"),
            GeckoStatus.Failed => Badge("Failed", Tone.Negative),
            _ => Badge("Stopped"),
        };
        var rows = new List<UIElement>();
        if (model.Gecko.Status == GeckoStatus.Failed && model.Gecko.FailureMessage is { } reason) rows.Add(CapabilityRow(Capability.No, $"Last start failed: {reason}"));
        else if (model.Gecko.Status == GeckoStatus.Stopped && !model.GeckoRuntimeMissing) rows.Add(CapabilityRow(Capability.Info, "Starts when a Mozilla tab opens a page"));
        rows.Add(CapabilityRow(Capability.Yes, "Chrome extensions run here, converted for Gecko"));
        rows.Add(CapabilityRow(Capability.Yes, "Built-in ad and tracker blocker (network filtering)"));
        rows.Add(CapabilityRow(Capability.Info, "Runs as a separate Firefox process inside Mosaic’s window, with its own profile; a Firefox you use yourself is never touched"));
        panel.Children.Add(Card(CardLayout(EngineKind.Gecko, "Mozilla (Gecko)", detail, status, [.. rows])));
    }

    internal static string SourceLabel(GeckoRuntime runtime, GeckoRuntimeInstaller installer) =>
        string.Equals(Path.GetFullPath(runtime.ExecutablePath), Path.GetFullPath(installer.ExecutablePath), StringComparison.OrdinalIgnoreCase)
            ? "Mosaic’s private copy"
            : $"borrowed from {Path.GetDirectoryName(runtime.ExecutablePath)}";

    // ---- Default engine ----

    private void DefaultEngine(StackPanel panel)
    {
        var choices = new RadioButtons { Header = "New tabs open in" };
        foreach (var kind in new[] { EngineKind.Gecko, EngineKind.Chromium })
        {
            var button = new RadioButton
            {
                Content = $"{kind.DisplayName()} ({kind.EngineName()})",
                Tag = kind,
                IsEnabled = model.IsEngineUsable(kind) || kind == EngineKind.Gecko,
            };
            choices.Items.Add(button);
        }
        choices.SelectedIndex = model.DefaultEngine == EngineKind.Gecko ? 0 : 1;
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedItem is RadioButton { Tag: EngineKind kind } && kind != model.DefaultEngine) model.SetDefaultEngine(kind);
        };
        panel.Children.Add(Card(Column(6, choices, Note(
            "Mosaic starts with this engine. While you browse, a new tab opens in the engine of the tab you’re on, and switching a " +
            "tab’s engine makes that engine the default. Site rules below take precedence."))));
    }

    // ---- Runtime install / update / remove ----

    private void RuntimeSection(StackPanel panel)
    {
        var installer = model.Platform.Runtime;
        var phase = installer.Phase;
        var content = new StackPanel { Spacing = 8 };
        if (phase.IsActive) content.Children.Add(new InstallProgressView(installer));
        else if (installer.InstalledRuntime is { } installed) content.Children.Add(InstalledView(installer, installed));
        else content.Children.Add(NotInstalledView(installer));

        if (!phase.IsActive && phase is RuntimeInstallPhase.Failed) content.Children.Add(Message(phase.Summary, Tone.Negative));
        else if (!phase.IsActive && phase is not RuntimeInstallPhase.IdlePhase)
        {
            content.Children.Add(Message(phase.Summary, phase is RuntimeInstallPhase.CancelledPhase ? Tone.Neutral : Tone.Positive));
        }
        if (installer.Message is { } message && !phase.IsActive) content.Children.Add(Note(message));
        panel.Children.Add(Card(content));
    }

    private StackPanel InstalledView(GeckoRuntimeInstaller installer, GeckoRuntime installed)
    {
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(Column(2, Text($"Firefox ESR {installed.Version}", strong: true), Note(installer.EngineDirectory)));
        if (installer.LatestVersion is not null)
        {
            var badge = installer.IsUpdateAvailable
                ? Badge($"{installer.LatestVersion!.NumberString} available", Tone.Warning)
                : Badge("Up to date", Tone.Positive);
            Grid.SetColumn(badge, 1);
            heading.Children.Add(badge);
        }

        var actions = Row(8);
        if (installer.IsUpdateAvailable)
        {
            actions.Children.Add(Button($"Update to {installer.LatestVersion?.NumberString}", () => installer.Install(), accent: true));
        }
        var check = Button(installer.IsChecking ? "Checking…" : "Check for updates", () => _ = installer.CheckForUpdateAsync());
        check.IsEnabled = !installer.IsChecking;
        actions.Children.Add(check);
        var more = new DropDownButton { Content = "More" };
        AutomationProperties.SetName(more, "More runtime actions");
        var menu = new MenuFlyout();
        var reinstall = new MenuFlyoutItem { Text = "Reinstall Firefox ESR" };
        reinstall.Click += (_, _) => installer.Install();
        var show = new MenuFlyoutItem { Text = "Show in File Explorer" };
        show.Click += (_, _) => ShowInExplorer(installer.EngineDirectory);
        var remove = new MenuFlyoutItem { Text = "Remove…" };
        remove.Click += async (_, _) =>
        {
            if (await ConfirmAsync(XamlRoot, "Remove the Mozilla engine?",
                "Mozilla tabs will stop working until you install it again. Your Mozilla browsing data and extensions are kept.", "Remove"))
            {
                await installer.RemoveAsync();
            }
        };
        menu.Items.Add(reinstall);
        menu.Items.Add(show);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
        menu.Opening += (_, _) => FeatureMounts.CoverPage("platform.menu", true);
        menu.Closed += (_, _) => FeatureMounts.CoverPage("platform.menu", false);
        more.Flyout = menu;
        actions.Children.Add(more);

        var lastCheck = installer.LastCheck is { } date ? $"Last checked {Relative(date)}." : "Not checked for updates yet.";
        return Column(8, heading, actions,
            Note(lastCheck + " Updating closes Mozilla tabs’ pages for a moment and reopens them; your Mozilla profile is kept."));
    }

    private StackPanel NotInstalledView(GeckoRuntimeInstaller installer)
    {
        if (model.Gecko.Runtime is { } borrowed)
        {
            return Column(8,
                Text($"Using {borrowed.Name} {borrowed.Version}, {SourceLabel(borrowed, installer)}.", strong: true),
                Note("Mosaic can install its own private Firefox ESR, which it is tested with and which never touches another browser’s " +
                     "profile. It is downloaded from Mozilla (about 70 MB) and checked against Mozilla’s signature before use."),
                Row(8, Button("Install Firefox ESR", () => installer.Install())));
        }
        return Column(8,
            Text("The Mozilla engine is not installed.", strong: true),
            Note("Mozilla tabs need Firefox ESR. Mosaic downloads it from Mozilla (about 70 MB), checks that it is signed by Mozilla, " +
                 "and keeps it in Mosaic’s own folder. A Firefox you already have is never touched. Chromium tabs work meanwhile."),
            Row(8, Button("Download and install Firefox ESR", () => installer.Install(), accent: true)));
    }

    internal static void ShowInExplorer(string folder)
    {
        try
        {
            global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception error) when (error is global::System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ---- Site rules ----

    private void CurrentSiteRule(StackPanel panel)
    {
        if (model.CurrentSiteDomain is not { } domain) return;
        var buttons = Row(6);
        foreach (var kind in new[] { EngineKind.Gecko, EngineKind.Chromium })
        {
            var button = Button(kind.DisplayName(), () => model.AddEngineRuleForCurrentSite(kind));
            button.IsEnabled = model.IsEngineUsable(kind);
            buttons.Children.Add(button);
        }
        panel.Children.Add(Card(Labeled($"Always open {domain} in", buttons)));
    }

    private void RulesList(StackPanel panel)
    {
        var rules = model.Platform.Rules;
        if (rules.Rules.Count == 0)
        {
            panel.Children.Add(Card(new TextBlock { Text = "No site rules yet.", Foreground = Secondary }));
            return;
        }
        foreach (var rule in rules.Rules)
        {
            var subdomains = new CheckBox { Content = "Include subdomains", IsChecked = rule.IncludesSubdomains };
            subdomains.Click += (_, _) => rules.Update(rule with { IncludesSubdomains = subdomains.IsChecked == true });
            var engine = new ComboBox { Items = { "Mozilla", "Chromium" }, SelectedIndex = rule.Engine == "chromium" ? 1 : 0, MinWidth = 120 };
            AutomationProperties.SetName(engine, $"Engine for {rule.Domain}");
            engine.SelectionChanged += (_, _) => rules.Update(rule with { Engine = engine.SelectedIndex == 1 ? "chromium" : "gecko" });
            var delete = new Button { Content = Icons.Make("trash", 14) };
            AutomationProperties.SetName(delete, $"Delete rule for {rule.Domain}");
            ToolTipService.SetToolTip(delete, "Delete rule");
            delete.Click += (_, _) => rules.Remove(rule);

            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Column(2, Text(rule.Domain, strong: true), subdomains));
            Grid.SetColumn(engine, 1);
            engine.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(engine);
            Grid.SetColumn(delete, 2);
            delete.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(delete);
            panel.Children.Add(Card(grid));
        }
    }

    /// <summary>Not re-rendered, so what is being typed survives other changes.</summary>
    private StackPanel AddRule()
    {
        var site = new TextBox { PlaceholderText = "example.com", MinWidth = 240 };
        AutomationProperties.SetName(site, "Site for a new rule");
        var engine = new ComboBox { Items = { "Mozilla", "Chromium" }, SelectedIndex = 1 };
        AutomationProperties.SetName(engine, "Engine for the new rule");
        var error = new TextBlock { Foreground = Critical, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        void Add()
        {
            if (site.Text.Trim().Length == 0) return;
            if (model.Platform.Rules.Set(site.Text, engine.SelectedIndex == 1 ? EngineKind.Chromium : EngineKind.Gecko))
            {
                site.Text = "";
                error.Visibility = Visibility.Collapsed;
            }
            else
            {
                error.Text = $"“{site.Text}” isn’t a site address. Type a domain such as example.com.";
                error.Visibility = Visibility.Visible;
            }
        }
        site.KeyDown += (_, args) =>
        {
            if (args.Key == global::Windows.System.VirtualKey.Enter) Add();
        };
        return Column(6, Row(8, site, engine, Button("Add rule", Add)), error);
    }
}

/// <summary>Progress bar, step and Cancel button while the Mozilla engine installs.</summary>
internal sealed class InstallProgressView : StackPanel
{
    public InstallProgressView(GeckoRuntimeInstaller installer)
    {
        Spacing = 6;
        var phase = installer.Phase;
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = phase.Fraction is null, Value = phase.Fraction ?? 0 };
        AutomationProperties.SetName(bar, phase.Summary);
        var cancel = PlatformUi.Button("Cancel", installer.Cancel,
            help: phase.IsCancellable ? "Stop and discard the download" : "Finishing; this step can’t be interrupted");
        cancel.IsEnabled = phase.IsCancellable;
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var summary = PlatformUi.Note(phase.Summary);
        AutomationProperties.SetLiveSetting(summary, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        line.Children.Add(summary);
        Grid.SetColumn(cancel, 1);
        line.Children.Add(cancel);
        Children.Add(bar);
        Children.Add(line);
    }
}
