using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Mosaic.Core.Browser;
using Mosaic.Core.Platform;
using static Mosaic.App.Features.Platform.PlatformUi;

namespace Mosaic.App.Features.Platform;

/// <summary>
/// Settings › About &amp; Updates: version, automatic updates (Mosaic's Ed25519 release signature,
/// same key and rules as macOS), components, the privacy statement of Mosaic's own connections, and
/// licences. Port of <c>AboutSettingsPane.swift</c>.
/// </summary>
internal sealed class AboutSettingsPage : PlatformPage
{
    private readonly BrowserModel model;

    public AboutSettingsPage(BrowserModel model) : base("About & Updates")
    {
        this.model = model;
        var platform = model.Platform;
        Body.Children.Add(Card(Identity()));
        Body.Children.Add(Header("Updates"));
        Body.Children.Add(Section(Updates, platform.Updater, platform.Updates));
        Body.Children.Add(Header("Components"));
        Body.Children.Add(Section(Components, model, model.Blocker, new GeckoStatusSignal(model.Gecko)));
        Body.Children.Add(Header("Privacy"));
        Body.Children.Add(Card(Privacy()));
        Body.Children.Add(Header("Licences"));
        foreach (var licence in Licences()) Body.Children.Add(Card(licence));
    }

    private static Grid Identity()
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Mosaic.png");
        if (File.Exists(iconPath))
        {
            var image = new Image { Source = new BitmapImage(new Uri(iconPath)) { DecodePixelWidth = 64 }, Width = 64, Height = 64 };
            AutomationProperties.SetAccessibilityView(image, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            grid.Children.Add(image);
        }
        var text = Column(3,
            new TextBlock { Text = "Mosaic", Style = Style("SubtitleTextBlockStyle") },
            Text($"Version {MosaicVersion.Current} ({MachineArchitectures.Process.Id()})"),
            Note("A two-engine browser for Windows: Mozilla’s Gecko and Chromium, side by side."));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    // ---- Updates ----

    private void Updates(StackPanel panel)
    {
        var updater = model.Platform.Updater;
        var checker = model.Platform.Updates;
        var card = new StackPanel { Spacing = 8 };

        var automatic = new ToggleSwitch { IsOn = updater.Automatic, IsEnabled = updater.LocationProblem is null };
        automatic.Toggled += (_, _) => updater.Automatic = automatic.IsOn;
        card.Children.Add(Labeled("Download and install updates automatically", automatic,
            "New versions download in the background and install the next time you quit Mosaic. Mosaic installs only downloads signed with its release key; anything else is discarded."));
        if (updater.LocationProblem is { } problem) card.Children.Add(Message(problem.Message, Tone.Warning));

        var frequency = new ComboBox { Items = { "Only when I ask", "Once a day" }, SelectedIndex = checker.Frequency == UpdateCheckFrequency.Daily ? 1 : 0, IsEnabled = !updater.Automatic };
        frequency.SelectionChanged += (_, _) => checker.Frequency = frequency.SelectedIndex == 1 ? UpdateCheckFrequency.Daily : UpdateCheckFrequency.Manual;
        card.Children.Add(Labeled("Check for updates", frequency,
            (updater.Automatic ? "Automatic updates check once a day. " : "") +
            "A check asks GitHub for Mosaic’s latest release and Mozilla for the current Firefox ESR. Nothing else is sent."));

        var check = Button(checker.IsChecking ? "Checking…" : "Check now", () => _ = checker.CheckNowAsync());
        check.IsEnabled = !checker.IsChecking;
        card.Children.Add(Row(10, check, new TextBlock
        {
            Text = checker.LastCheck is { } last ? $"Last checked {Relative(last)}" : "",
            Foreground = Secondary,
            VerticalAlignment = VerticalAlignment.Center,
        }));

        switch (checker.Outcome)
        {
            case ReleaseOutcome.Available available:
                card.Children.Add(AvailableRow(available));
                break;
            case ReleaseOutcome.UpToDate upToDate:
                card.Children.Add(Message(upToDate.Message, Tone.Positive));
                break;
            case { } other:
                card.Children.Add(Message(other.Message, Tone.Warning));
                card.Children.Add(Row(8, Button("Open releases page", () => Open(ReleaseCheck.ReleasesPageUrl.ToString()))));
                break;
        }
        if (updater.Phase is UpdatePhase.Ready ready && checker.Outcome is not ReleaseOutcome.Available) card.Children.Add(ReadyRow(ready.Version));
        panel.Children.Add(Card(card));
    }

    private StackPanel AvailableRow(ReleaseOutcome.Available available)
    {
        var updater = model.Platform.Updater;
        var release = available.Release;
        var row = new StackPanel { Spacing = 8 };
        var heading = Row(10, Message(available.Message, Tone.Neutral));
        if (release.PageUrl is { } page) heading.Children.Add(Button("Release notes", () => Open(page.ToString())));
        row.Children.Add(heading);
        switch (updater.Phase)
        {
            case UpdatePhase.Downloading downloading:
                var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = downloading.Fraction ?? 0, IsIndeterminate = downloading.Fraction is null, Width = 260 };
                AutomationProperties.SetName(bar, "Downloading the update");
                row.Children.Add(Row(10, bar,
                    new TextBlock { Text = downloading.Fraction is { } f ? $"{(int)(f * 100)}%" : "Downloading…", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center },
                    Button("Cancel", updater.Cancel)));
                break;
            case UpdatePhase.Verifying:
                row.Children.Add(Row(8, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Note("Checking the release signature…")));
                break;
            case UpdatePhase.Ready ready:
                row.Children.Add(ReadyRow(ready.Version));
                break;
            case UpdatePhase.Failed failed:
                row.Children.Add(Message(failed.Message, Tone.Negative));
                if (updater.LocationProblem is null) row.Children.Add(Row(8, Button("Download and install", () => updater.Download(release), accent: true)));
                break;
            default:
                if (updater.LocationProblem is null) row.Children.Add(Row(8, Button("Download and install", () => updater.Download(release), accent: true)));
                break;
        }
        return row;
    }

    private StackPanel ReadyRow(string version) => Row(10,
        Message($"Mosaic {version} is ready. It installs when you quit Mosaic.", Tone.Positive),
        Button("Restart to update", model.Platform.Updater.RestartToUpdate, accent: true));

    private void Open(string url) => model.NewTab(url);

    // ---- Components ----

    private void Components(StackPanel panel)
    {
        string chromium;
        try
        {
            chromium = model.ChromiumAvailable ? "WebView2 Runtime " + Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() : "not installed";
        }
        catch (Exception)
        {
            chromium = "not installed";
        }
        var gecko = model.Gecko.Runtime is { } runtime ? $"{runtime.Name} {runtime.Version}" : model.GeckoRuntimeMissing ? "not installed" : "installed (restarts on next use)";
        var blocker = model.Blocker.Status == BlockerStatus.Unavailable ? "not included in this build" : $"uBlock Origin {model.Blocker.EngineVersion ?? "(loading)"} filtering engine";
        panel.Children.Add(Card(Column(8,
            Labeled("Chromium", new TextBlock { Text = chromium, IsTextSelectionEnabled = true }),
            Labeled("Mozilla engine", new TextBlock { Text = gecko, IsTextSelectionEnabled = true }),
            Labeled("Content blocker", new TextBlock { Text = blocker, IsTextSelectionEnabled = true }))));
    }

    // ---- Privacy ----

    internal static readonly (string What, string Detail)[] Connections =
    [
        ("Filter lists (uBlock Origin’s lists, the Public Suffix List)",
         "ublockorigin.github.io, malware-filter.gitlab.io, pgl.yoyo.org and publicsuffix.org: refreshed every two days, or when you press Update Lists."),
        ("Mozilla engine install and update checks",
         "download.mozilla.org (and Mozilla’s CDN) when you install or update the engine; product-details.mozilla.org when you check for an engine update."),
        ("Chrome extensions",
         "clients2.google.com (the Chrome Web Store, which hands the download to Google’s servers) when you install an extension from a link or ID."),
        ("Mosaic update checks",
         "api.github.com, when you press Check Now or once a day if you chose that; github.com and GitHub’s download servers when an update is downloaded. No account or token is used."),
    ];

    private static StackPanel Privacy()
    {
        var column = Column(8,
            Text("Mosaic has no telemetry, no accounts and no analytics.", strong: true),
            Note("Apart from the pages you visit, Mosaic itself connects to these services, and only for these reasons:"));
        foreach (var (what, detail) in Connections)
        {
            var item = Column(1, new TextBlock { Text = what, Style = Style("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap }, Note(detail));
            AutomationProperties.SetName(item, $"{what}: {detail}");
            column.Children.Add(item);
        }
        column.Children.Add(Note(
            "The Mozilla engine is an unmodified Firefox ESR with its automatic updates and default-browser checks switched off; like " +
            "any browser engine it may still contact Mozilla for security services such as certificate revocation checks. The " +
            "Chromium engine is Microsoft’s WebView2 Runtime, which Windows keeps up to date with Microsoft Edge."));
        return column;
    }

    // ---- Licences ----

    private IEnumerable<StackPanel> Licences()
    {
        yield return Licence("Mosaic",
            "Mosaic is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License, version 3 or (at your option) any later version. It comes with no warranty. Source: github.com/SkillzNoLogic/Mosaic-browser",
            Path.Combine(AppContext.BaseDirectory, "LICENSE"), "https://www.gnu.org/licenses/gpl-3.0.txt");
        var chromiumCredits = Button("Show Chromium’s credits", () => model.NewTab("edge://credits/", EngineKind.Chromium));
        chromiumCredits.IsEnabled = model.ChromiumAvailable;
        yield return Licence("Microsoft Edge WebView2 (Chromium engine)",
            "The WebView2 Runtime is Microsoft software under its own licence terms, installed and updated by Windows; it is built on Chromium (BSD licence plus the licences of Chromium’s third-party components). The WebView2 SDK is © Microsoft Corporation under the BSD 3-Clause licence.",
            extra: chromiumCredits);
        var firefoxLicences = Button("Show Firefox’s licences", () => model.NewTab("about:license", EngineKind.Gecko));
        firefoxLicences.IsEnabled = !model.GeckoRuntimeMissing;
        yield return Licence("Firefox ESR (Mozilla engine)",
            "Mozilla Public License 2.0; downloaded unmodified from Mozilla. Firefox is a trademark of the Mozilla Foundation; Mosaic is not affiliated with Mozilla.",
            extra: firefoxLicences);
        yield return Licence($"uBlock Origin {model.Blocker.EngineVersion}".Trim(),
            "Mosaic’s content blocker contains uBlock Origin’s network filtering engine, © Raymond Hill and contributors, under the GNU General Public License v3 or later.",
            Path.Combine(AppContext.BaseDirectory, "blocker", "LICENSE-uBlock-Origin.txt"), "https://www.gnu.org/licenses/gpl-3.0.txt");
        yield return Licence("Readability",
            "Reader view uses Mozilla’s Readability.js, © Arc90 Inc and Mozilla, under the Apache License, version 2.0 (apache.org/licenses/LICENSE-2.0).");
        yield return Licence("Other components",
            "Windows App SDK and WinUI (MIT, © Microsoft Corporation); Microsoft ClearScript (MIT) with V8 (BSD 3-Clause, © the V8 project authors), which runs the content blocker for Chromium tabs; Bouncy Castle (MIT, © The Legion of the Bouncy Castle Inc.), which verifies Mosaic’s release signatures.");
        var lists = Column(3);
        foreach (var (list, terms) in FilterLists) lists.Children.Add(Note($"{list}: {terms}"));
        yield return Licence("Filter lists", "Each list has its own licence:", extra: lists);
    }

    private static readonly (string, string)[] FilterLists =
    [
        ("uBlock filters (Ads, Badware risks, Privacy, Quick fixes, Unbreak)", "GPL v3, github.com/uBlockOrigin/uAssets"),
        ("EasyList, EasyPrivacy", "GPL v3 or CC BY-SA 3.0, at your choice; easylist.to/pages/licence.html"),
        ("Online Malicious URL Blocklist", "see gitlab.com/malware-filter/urlhaus-filter#license"),
        ("Peter Lowe’s Ad and tracking server list", "Free to use under the list’s policy; pgl.yoyo.org/adservers/policy.php"),
        ("Public Suffix List", "Mozilla Public License 2.0; publicsuffix.org"),
    ];

    /// <summary>A licence: what it covers, and its full text on demand (read from disk only when opened).</summary>
    private static StackPanel Licence(string title, string summary, string? file = null, string? fallbackUrl = null, UIElement? extra = null)
    {
        var column = Column(6, Text(title, strong: true), Note(summary));
        if (extra is not null) column.Children.Add(extra);
        if (file is not null)
        {
            var body = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
            var expander = new Expander
            {
                Header = "Licence text",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer { Content = body, MaxHeight = 320 },
            };
            expander.Expanding += (_, _) =>
            {
                if (body.Text.Length > 0) return;
                try
                {
                    body.Text = File.ReadAllText(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    body.Text = $"The licence file is missing from this build ({Path.GetFileName(file)}). The licence is at {fallbackUrl}";
                }
            };
            column.Children.Add(expander);
        }
        return column;
    }
}
