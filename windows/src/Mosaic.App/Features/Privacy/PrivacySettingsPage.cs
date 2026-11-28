using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Blocker;
using Mosaic.Core.Browser;
using Mosaic.Core.Privacy;
using static Mosaic.App.Features.Privacy.PrivacyUi;

namespace Mosaic.App.Features.Privacy;

/// <summary>
/// Settings › Privacy &amp; Security: the content blocker (lists, My filters, sites it is off for),
/// tracking protection, HTTPS-only mode, DNS over HTTPS, cookies and site data, clearing browsing
/// data, private tabs and site permissions. Port of <c>PrivacySettingsPane.swift</c>.
/// </summary>
internal sealed class PrivacySettingsPage : UserControl
{
    /// <summary>Id the page uses with <see cref="FeatureMounts.CoverPage"/> while a dialog is open over it.</summary>
    internal const string DialogCover = "privacy.dialog";

    private readonly BrowserModel model;
    private readonly ContentBlocker blocker;
    private readonly PrivacyController privacy;
    private readonly List<IDisposable> watches = [];
    private readonly StackPanel page = new() { Spacing = 0, Padding = new Thickness(24, 8, 24, 32), MaxWidth = 860 };
    private bool dialogOpen;

    public PrivacySettingsPage(BrowserModel model)
    {
        this.model = model;
        blocker = model.Blocker;
        privacy = model.Privacy;
        AutomationProperties.SetName(this, "Privacy & Security settings");

        page.Children.Add(new TextBlock { Text = "Privacy & Security", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        BuildBlocker();
        BuildFilterLists();
        BuildMyFilters();
        BuildAllowlist();
        BuildTracking();
        BuildHttps();
        BuildDns();
        BuildSiteData();
        BuildClearData();
        BuildPrivateBrowsing();
        SitePermissionsSection.Build(page, model);

        Content = new ScrollViewer { Content = page, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) =>
        {
            Watch(model, () => { if (model.ShowClearBrowsingData && !dialogOpen) _ = ShowClearDialogAsync(); }, nameof(BrowserModel.ShowClearBrowsingData));
        };
        Unloaded += (_, _) =>
        {
            foreach (var watch in watches) watch.Dispose();
            watches.Clear();
        };
    }

    private void Watch(global::System.ComponentModel.INotifyPropertyChanged source, Action update, params string[] properties) =>
        watches.Add(PrivacyUi.Watch(source, update, properties));

    // ---- Content blocker ----

    private void BuildBlocker()
    {
        var (_, rows) = Section(page, "Content Blocker");
        if (!blocker.IsAvailable)
        {
            var missing = Text("The content blocker is not included in this build. Run scripts\\bootstrap.ps1 and rebuild.", foreground: Caution);
            rows.Children.Add(missing);
            return;
        }
        var enabled = Toggle(rows, "Block ads and trackers",
            "uBlock Origin’s filtering engine and filter lists, built into Mosaic. Works in both engines.",
            blocker.IsEnabled, on => model.SetBlockerEnabled(on));
        var cosmetic = Toggle(rows, "Hide leftover ad elements",
            "Applies the lists’ element-hiding (cosmetic) filters to pages as they load. Scriptlets and procedural filters (such as :has-text) are not supported.",
            blocker.CosmeticEnabled, on => blocker.SetCosmeticEnabled(on));
        var status = Caption("");
        rows.Children.Add(Row(Text("Status"), status));
        Loaded += (_, _) => Watch(blocker, () =>
        {
            enabled.IsOn = blocker.IsEnabled;
            cosmetic.IsOn = blocker.CosmeticEnabled;
            cosmetic.IsEnabled = blocker.IsEnabled;
            status.Text = StatusText();
        });
    }

    private string StatusText()
    {
        if (blocker.Status != BlockerStatus.Ready) return blocker.StatusLine;
        var stats = blocker.CosmeticStats;
        var version = blocker.EngineVersion is { } v ? $" · uBO {v}" : "";
        return string.Format(CultureInfo.CurrentCulture, "{0:N0} network filters · {1:N0} element-hiding rules{2}",
            blocker.FilterCount, stats.Generic + stats.Specific, version);
    }

    private void BuildFilterLists()
    {
        var (card, rows) = Section(page, "Filter Lists",
            "Lists marked as uBlock Origin’s defaults ship with Mosaic. Others are downloaded when you turn them on, and refreshed every two days.");
        card.IsHitTestVisible = blocker.IsAvailable;
        card.Opacity = blocker.IsAvailable ? 1 : 0.5;
        List<(FilterList List, ToggleSwitch Toggle)> toggles = [];
        List<(string Group, TextBlock Count)> counts = [];
        foreach (var (groupId, groupTitle) in FilterListCatalog.Groups)
        {
            var lists = FilterListCatalog.All.Where(list => list.Group == groupId).ToList();
            if (lists.Count == 0) continue;
            var body = new StackPanel { Spacing = 8 };
            foreach (var list in lists)
            {
                var detail = FilterListCatalog.IsDefault(list) ? "Default" : list.Summary;
                var toggle = Toggle(body, list.Title, detail, blocker.EnabledListIds.Contains(list.Id), on => blocker.SetList(list, on));
                toggles.Add((list, toggle));
            }
            var count = Caption("");
            counts.Add((groupId, count));
            var expander = new Expander
            {
                Header = Row(Text(groupTitle), count),
                Content = body,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(expander, groupTitle);
            rows.Children.Add(expander);
        }

        var updated = Text("", 13);
        var message = Caption("");
        var progress = new ProgressRing { IsActive = false, Width = 16, Height = 16 };
        var update = Button("Update Now", () => _ = blocker.UpdateListsAsync(), "Update the filter lists now");
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(updated);
        text.Children.Add(message);
        rows.Children.Add(Row(text, progress, update));

        Loaded += (_, _) => Watch(blocker, () =>
        {
            foreach (var (list, toggle) in toggles) toggle.IsOn = blocker.EnabledListIds.Contains(list.Id);
            foreach (var (group, count) in counts)
            {
                var lists = FilterListCatalog.All.Where(list => list.Group == group).ToList();
                count.Text = $"{lists.Count(list => blocker.EnabledListIds.Contains(list.Id))} of {lists.Count} on";
            }
            blocker.RefreshListsLine();
            updated.Text = blocker.ListsDate is null ? "Some lists have not been downloaded yet" : blocker.ListsLine.Replace("Lists updated", "Updated", StringComparison.Ordinal);
            message.Text = blocker.LastUpdateMessage ?? "";
            message.Visibility = blocker.LastUpdateMessage is null ? Visibility.Collapsed : Visibility.Visible;
            progress.IsActive = blocker.IsUpdating;
            update.IsEnabled = !blocker.IsUpdating && blocker.IsAvailable;
        });
    }

    private void BuildMyFilters()
    {
        var (card, rows) = Section(page, "My Filters",
            "uBlock Origin syntax, one per line: ||ads.example.com^ blocks a server, example.com##.banner hides an element on one site, ##.banner hides it everywhere, @@ and #@# make exceptions. Lines starting with ! are comments.");
        card.IsHitTestVisible = blocker.IsAvailable;
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            MinHeight = 110,
            MaxHeight = 220,
            Text = blocker.UserFilters,
            IsSpellCheckEnabled = false,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(editor, "My filters");
        AutomationProperties.SetHelpText(editor, "One filter per line, in uBlock Origin syntax");
        rows.Children.Add(editor);
        var problems = new StackPanel { Spacing = 4 };
        rows.Children.Add(problems);
        var summary = Caption("");
        var error = Text("", 12, Critical);
        error.Visibility = Visibility.Collapsed;
        IReadOnlyList<FilterCheck> checks = [];

        Button? apply = null;
        var revert = Button("Revert", () => editor.Text = blocker.UserFilters, "Revert my filters");
        apply = Button("Apply", () =>
        {
            try
            {
                blocker.SaveUserFilters(editor.Text);
                error.Visibility = Visibility.Collapsed;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                error.Text = failure.Message;
                error.Visibility = Visibility.Visible;
            }
            Refresh();
        }, "Apply my filters", accent: true);
        rows.Children.Add(Row(summary, revert, apply));
        rows.Children.Add(error);
        editor.KeyDown += (_, e) =>
        {
            // Ctrl+S applies, as ⌘S does on macOS.
            if (e.Key == Windows.System.VirtualKey.S && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
                && apply.IsEnabled)
            {
                e.Handled = true;
                blocker.SaveUserFilters(editor.Text);
                Refresh();
            }
        };

        void Refresh()
        {
            checks = blocker.Validate(editor.Text);
            problems.Children.Clear();
            var shown = checks.Where(check => !check.IsEffective && check.Kind != FilterCheckKind.Comment)
                .Concat(checks.Where(check => check.IsEffective && check.Message.Length > 0));
            foreach (var check in shown)
            {
                var isError = check.Kind == FilterCheckKind.Error;
                var icon = Icon(isError ? "" : "", 14);
                icon.Foreground = isError ? Critical : Caution;
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                line.Children.Add(icon);
                line.Children.Add(new TextBlock { Text = $"Line {check.Line}: {check.Message}", FontSize = 13, TextWrapping = TextWrapping.Wrap });
                AutomationProperties.SetName(line, $"{(isError ? "Error" : "Note")}, line {check.Line}: {check.Message}");
                problems.Children.Add(line);
            }
            summary.Text = blocker.CanValidate
                ? $"{checks.Count(check => check.Kind == FilterCheckKind.Network)} network, {checks.Count(check => check.Kind == FilterCheckKind.Cosmetic)} element-hiding"
                : "Filters are checked once the blocker has loaded.";
            var changed = editor.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') != blocker.UserFilters;
            revert.IsEnabled = changed;
            apply.IsEnabled = changed && !checks.Any(check => check.Kind == FilterCheckKind.Error);
        }

        editor.TextChanged += (_, _) => Refresh();
        Loaded += (_, _) => Watch(blocker, Refresh, nameof(ContentBlocker.Status), nameof(ContentBlocker.UserFilters));
    }

    private void BuildAllowlist()
    {
        var (_, rows) = Section(page, "Sites Where the Blocker Is Off",
            "A site covers its subdomains. You can also switch the blocker off for the current site from the shield in the toolbar.");
        var list = new StackPanel { Spacing = 6 };
        rows.Children.Add(list);
        var input = new TextBox { PlaceholderText = "example.com" };
        AutomationProperties.SetName(input, "Add a site where the blocker is off");
        void Add()
        {
            if (BlockerSettings.Normalize(input.Text) is null) return;
            blocker.SetAllowlisted(true, input.Text);
            input.Text = "";
        }
        var add = Button("Add", Add, "Add the site");
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) Add();
        };
        input.TextChanged += (_, _) => add.IsEnabled = BlockerSettings.Normalize(input.Text) is not null;
        add.IsEnabled = false;
        rows.Children.Add(Row(input, add));
        Loaded += (_, _) => Watch(blocker, () =>
        {
            list.Children.Clear();
            var sites = blocker.Settings.Allowlist.Order(StringComparer.Ordinal).ToList();
            if (sites.Count == 0) list.Children.Add(Caption("The blocker is on for every site."));
            foreach (var site in sites)
            {
                list.Children.Add(Row(Text(site), Button("Turn Blocker Back On", () => blocker.SetAllowlisted(false, site), $"Turn the blocker back on for {site}")));
            }
        }, nameof(ContentBlocker.Settings));
    }

    // ---- Tracking, HTTPS, DNS ----

    private void BuildTracking()
    {
        var (_, rows) = Section(page, "Tracking");
        var strip = Toggle(rows, "Remove tracking parameters from links",
            "Strips fbclid, gclid, utm_source and similar from addresses before they load (Firefox’s query-stripping list plus campaign parameters).",
            privacy.Settings.StripTrackingParameters, on => privacy.Update(settings => settings with { StripTrackingParameters = on }));
        var cookies = Toggle(rows, "Block third-party cookies",
            "Embedded content from other sites cannot set or read cookies. A few sign-in and payment flows may need an exception: turn this off to allow them.",
            privacy.Settings.BlockThirdPartyCookies, on => privacy.Update(settings => settings with { BlockThirdPartyCookies = on }));
        Loaded += (_, _) => Watch(privacy, () =>
        {
            strip.IsOn = privacy.Settings.StripTrackingParameters;
            cookies.IsOn = privacy.Settings.BlockThirdPartyCookies;
        }, nameof(PrivacyController.Settings));
    }

    private void BuildHttps()
    {
        var (_, rows) = Section(page, "Secure Connections");
        var httpsOnly = Toggle(rows, "HTTPS-Only Mode",
            "Opens every site over a secure connection. If a site has no secure version, Mosaic asks before opening it over HTTP. Local network addresses are left alone.",
            privacy.Settings.HttpsOnly, on => privacy.Update(settings => settings with { HttpsOnly = on }));
        var exceptions = new StackPanel { Spacing = 6 };
        rows.Children.Add(exceptions);
        var input = new TextBox { PlaceholderText = "example.com" };
        AutomationProperties.SetName(input, "Allow a site over HTTP");
        void Add()
        {
            if (BlockerSettings.Normalize(input.Text) is null) return;
            var site = input.Text;
            privacy.Update(settings => settings.WithHttpException(true, site));
            input.Text = "";
        }
        var add = Button("Add", Add, "Allow the site over HTTP");
        add.IsEnabled = false;
        input.TextChanged += (_, _) => add.IsEnabled = BlockerSettings.Normalize(input.Text) is not null;
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) Add();
        };
        var adder = Row(input, add);
        rows.Children.Add(adder);
        Loaded += (_, _) => Watch(privacy, () =>
        {
            var settings = privacy.Settings;
            httpsOnly.IsOn = settings.HttpsOnly;
            exceptions.Visibility = adder.Visibility = settings.HttpsOnly ? Visibility.Visible : Visibility.Collapsed;
            exceptions.Children.Clear();
            foreach (var site in settings.HttpsExceptions)
            {
                exceptions.Children.Add(Row(Text(site), Button("Remove", () => privacy.Update(current => current.WithHttpException(false, site)), $"Remove the HTTP exception for {site}")));
            }
            foreach (var site in privacy.SessionHttpExceptions.Order(StringComparer.Ordinal))
            {
                exceptions.Children.Add(Row(Text(site), Caption("Allowed until Mosaic quits")));
            }
        }, nameof(PrivacyController.Settings), nameof(PrivacyController.SessionHttpExceptions));
    }

    private void BuildDns()
    {
        var (_, rows) = Section(page, "DNS over HTTPS",
            "Encrypts the lookups that turn site names into addresses, so the network cannot see or alter them. Strict mode fails to open sites when the resolver is unreachable.");
        (DnsMode Mode, string Title)[] modes =
        [
            (DnsMode.Off, "Off (use the network’s DNS)"),
            (DnsMode.Automatic, "On, fall back to the network’s DNS"),
            (DnsMode.Strict, "On, never fall back (strict)"),
        ];
        var mode = new ComboBox { ItemsSource = modes.Select(item => item.Title).ToList(), MinWidth = 280 };
        AutomationProperties.SetName(mode, "DNS over HTTPS");
        rows.Children.Add(Row(Text("DNS over HTTPS"), mode));

        var providerTitles = DnsOverHttps.Providers.Select(provider => provider.Name).Append("Custom…").ToList();
        var provider = new ComboBox { ItemsSource = providerTitles, MinWidth = 280 };
        AutomationProperties.SetName(provider, "DNS provider");
        var providerRow = Row(Text("Provider"), provider);
        rows.Children.Add(providerRow);
        var note = Caption("");
        rows.Children.Add(note);

        var custom = new TextBox { PlaceholderText = "https://dns.example/dns-query", Text = privacy.Settings.DnsOverHttps.CustomTemplate };
        AutomationProperties.SetName(custom, "Resolver URL");
        void UseCustom()
        {
            if (!DnsOverHttps.IsValidTemplate(custom.Text)) return;
            var template = custom.Text;
            privacy.Update(settings => settings with { DnsOverHttps = settings.DnsOverHttps with { CustomTemplate = template } });
        }
        var use = Button("Use", UseCustom, "Use this resolver");
        var customRow = Row(custom, use);
        rows.Children.Add(customRow);
        var customError = Text("Enter an https:// address.", 12, Critical);
        rows.Children.Add(customError);
        custom.TextChanged += (_, _) =>
        {
            use.IsEnabled = DnsOverHttps.IsValidTemplate(custom.Text);
            customError.Visibility = custom.Text.Length > 0 && !use.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        };
        custom.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) UseCustom();
        };
        var restart = Text("Chromium tabs use the new setting after Mosaic restarts; Mozilla tabs use it now.", 12, Caution);
        rows.Children.Add(restart);

        var updating = false;
        mode.SelectionChanged += (_, _) =>
        {
            if (updating || mode.SelectedIndex < 0) return;
            var chosen = modes[mode.SelectedIndex].Mode;
            privacy.Update(settings => settings with { DnsOverHttps = settings.DnsOverHttps with { Mode = chosen } });
        };
        provider.SelectionChanged += (_, _) =>
        {
            if (updating || provider.SelectedIndex < 0) return;
            var id = provider.SelectedIndex < DnsOverHttps.Providers.Count ? DnsOverHttps.Providers[provider.SelectedIndex].Id : DnsOverHttps.CustomProviderId;
            privacy.Update(settings => settings with { DnsOverHttps = settings.DnsOverHttps with { ProviderId = id } });
        };
        Loaded += (_, _) => Watch(privacy, () =>
        {
            updating = true;
            var setting = privacy.Settings.DnsOverHttps;
            mode.SelectedIndex = Array.FindIndex(modes, item => item.Mode == setting.Mode);
            var index = DnsOverHttps.Providers.ToList().FindIndex(item => item.Id == setting.ProviderId);
            provider.SelectedIndex = index >= 0 ? index : providerTitles.Count - 1;
            var on = setting.Mode != DnsMode.Off;
            var isCustom = setting.ProviderId == DnsOverHttps.CustomProviderId;
            providerRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            customRow.Visibility = on && isCustom ? Visibility.Visible : Visibility.Collapsed;
            customError.Visibility = Visibility.Collapsed;
            use.IsEnabled = DnsOverHttps.IsValidTemplate(custom.Text);
            note.Text = DnsOverHttps.Providers.FirstOrDefault(item => item.Id == setting.ProviderId)?.Note ?? "";
            note.Visibility = on && !isCustom ? Visibility.Visible : Visibility.Collapsed;
            restart.Visibility = privacy.ChromiumNeedsRestart ? Visibility.Visible : Visibility.Collapsed;
            updating = false;
        }, nameof(PrivacyController.Settings), nameof(PrivacyController.ChromiumNeedsRestart));
    }

    // ---- Site data ----

    private void BuildSiteData()
    {
        var (_, rows) = Section(page, "Cookies and Site Data");
        var onExit = Toggle(rows, "Delete cookies and site data when Mosaic quits",
            "You will be signed out of sites each time. History and passwords are kept.",
            privacy.Settings.ClearCookiesOnExit, on => privacy.Update(settings => settings with { ClearCookiesOnExit = on }));

        var body = new StackPanel { Spacing = 8 };
        var search = new AutoSuggestBox { PlaceholderText = "Search sites", QueryIcon = new SymbolIcon(Symbol.Find) };
        AutomationProperties.SetName(search, "Search sites");
        var progress = new ProgressRing { IsActive = false, Width = 16, Height = 16 };
        var refresh = Button("Refresh", () => _ = privacy.LoadSiteDataAsync(), "Refresh the list of site data");
        var deleteAll = Button("Delete All…", () => _ = ConfirmDeleteAllAsync(), "Delete all cookies and site data");
        body.Children.Add(Row(search, progress, refresh, deleteAll));
        var geckoNote = Caption("Mozilla’s data is listed once a Mozilla tab has been opened in this session.");
        body.Children.Add(geckoNote);
        var list = new StackPanel { Spacing = 6 };
        body.Children.Add(list);
        var count = Caption("");
        var expander = new Expander
        {
            Header = Row(Text("Stored site data"), count),
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(expander, "Stored site data");
        expander.Expanding += (_, _) => _ = privacy.LoadSiteDataAsync();
        rows.Children.Add(expander);

        void Fill()
        {
            onExit.IsOn = privacy.Settings.ClearCookiesOnExit;
            progress.IsActive = privacy.IsLoadingSiteData;
            geckoNote.Visibility = privacy.SiteDataIncludesGecko ? Visibility.Collapsed : Visibility.Visible;
            count.Text = privacy.SiteData.Count == 0 ? "" : $"{privacy.SiteData.Count} sites";
            deleteAll.IsEnabled = privacy.SiteData.Count > 0;
            list.Children.Clear();
            var query = search.Text.Trim();
            var shown = privacy.SiteData.Where(entry => query.Length == 0 || entry.Site.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(500).ToList();
            if (shown.Count == 0 && !privacy.IsLoadingSiteData)
            {
                list.Children.Add(Caption(query.Length == 0 ? "No site has stored any data." : "No matching sites."));
            }
            foreach (var entry in shown)
            {
                var text = new StackPanel { Spacing = 1 };
                text.Children.Add(Text(entry.Site));
                text.Children.Add(Caption(entry.Description));
                list.Children.Add(Row(text, Button("Delete", () => _ = privacy.ClearSiteDataAsync(entry), $"Delete data stored by {entry.Site}")));
            }
        }
        search.TextChanged += (_, _) => Fill();
        Loaded += (_, _) => Watch(privacy, Fill, nameof(PrivacyController.Settings), nameof(PrivacyController.SiteData),
            nameof(PrivacyController.IsLoadingSiteData), nameof(PrivacyController.SiteDataIncludesGecko));
    }

    private async Task ConfirmDeleteAllAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Delete all cookies and site data?",
            Content = "You will be signed out of all sites, in both engines.",
            PrimaryButtonText = "Delete All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await ShowCoveredAsync(dialog) == ContentDialogResult.Primary) await privacy.ClearAllSiteDataAsync();
    }

    private void BuildClearData()
    {
        var (_, rows) = Section(page, "Clear Browsing Data");
        var summary = Caption("");
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Text("Clear browsing data"));
        text.Children.Add(summary);
        var button = Button("Clear Browsing Data…", () => model.ShowClearBrowsingData = true, "Clear browsing data");
        ToolTipService.SetToolTip(button, "Clear Browsing Data (Ctrl+Shift+Delete)");
        rows.Children.Add(Row(text, button));
        Loaded += (_, _) => Watch(privacy, () =>
            summary.Text = privacy.LastClearSummary ?? "History, cookies, cache, download history and form entries, by time range.",
            nameof(PrivacyController.LastClearSummary));
    }

    private async Task ShowClearDialogAsync()
    {
        if (XamlRoot is null) return;
        var dialog = new ClearBrowsingDataDialog(privacy) { XamlRoot = XamlRoot };
        await ShowCoveredAsync(dialog);
        model.ShowClearBrowsingData = false;
    }

    /// <summary>Shows a dialog with Gecko's page hidden under it (a Win32 child window would cover it).</summary>
    private async Task<ContentDialogResult> ShowCoveredAsync(ContentDialog dialog)
    {
        dialogOpen = true;
        FeatureMounts.CoverPage(DialogCover, true);
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            FeatureMounts.CoverPage(DialogCover, false);
            dialogOpen = false;
        }
    }

    private void BuildPrivateBrowsing()
    {
        var (_, rows) = Section(page, "Private Browsing");
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(Text("Private tabs keep cookies, storage and cache in memory only, never record history, and forget everything when the last private tab closes."));
        text.Children.Add(Caption("Private tabs run on the Chromium engine. Mozilla private windows are not supported in Mosaic yet, so a private tab cannot be switched to Mozilla."));
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = PrivateBadge();
        badge.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(badge);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var button = Button("New Private Tab", () =>
        {
            ShellBridge.DismissSettings(model);
            model.NewPrivateTab();
        }, "New private tab");
        ToolTipService.SetToolTip(button, "New Private Tab (Ctrl+Shift+N)");
        button.IsEnabled = model.ChromiumAvailable;
        button.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(button, 2);
        row.Children.Add(button);
        rows.Children.Add(row);
    }
}
