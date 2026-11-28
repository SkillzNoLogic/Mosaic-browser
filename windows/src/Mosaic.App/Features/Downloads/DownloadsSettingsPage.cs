using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Downloads;
using Windows.Storage.Pickers;

namespace Mosaic.App.Features.Downloads;

/// <summary>Settings › Downloads (port of <c>DownloadsSettingsPane.swift</c>).</summary>
internal sealed class DownloadsSettingsPage : UserControl
{
    /// <summary>Size thresholds offered for Free Download Manager, in megabytes.</summary>
    private static readonly int[] Thresholds = [0, 10, 50, 100, 500, 1000];

    private readonly DownloadManager downloads;
    private readonly DownloadSettings settings;
    private readonly StackPanel root = new() { Spacing = 4, Padding = new Thickness(24, 16, 24, 24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
    private bool updating;

    public DownloadsSettingsPage(BrowserModel model)
    {
        downloads = model.Downloads;
        settings = downloads.Settings;
        Content = new ScrollViewer { Content = root };
        Loaded += (_, _) =>
        {
            // FDM may have been installed or removed since the page was last shown.
            downloads.Fdm.Refresh();
            settings.PropertyChanged += Rebuild;
            Build();
        };
        Unloaded += (_, _) => settings.PropertyChanged -= Rebuild;
        Build();
    }

    private void Rebuild(object? sender, global::System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (!updating) Build();
    }

    /// <summary>Changes a setting without rebuilding the page under the control being used.</summary>
    private void Change(Action apply, bool rebuild = false)
    {
        updating = true;
        try { apply(); }
        finally { updating = false; }
        if (rebuild) Build();
    }

    private void Build()
    {
        root.Children.Clear();
        root.Children.Add(new TextBlock { Text = "Downloads", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], Margin = new Thickness(0, 0, 0, 8) });

        // ---- Saving ----
        Section("Saving");
        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(new TextBlock { Text = "Download folder", VerticalAlignment = VerticalAlignment.Center });
        var path = new TextBlock
        {
            Text = settings.FolderDisplayPath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = settings.AskWhereToSave ? 0.6 : 1,
        };
        ToolTipService.SetToolTip(path, settings.FolderDisplayPath);
        Grid.SetColumn(path, 1);
        folderRow.Children.Add(path);
        var change = new Button { Content = "Change…" };
        AutomationProperties.SetName(change, "Change download folder");
        change.Click += async (_, _) => await ChooseFolderAsync();
        Grid.SetColumn(change, 2);
        folderRow.Children.Add(change);
        root.Children.Add(Card(folderRow));

        root.Children.Add(Card(Toggle("Ask where to save each file", null, settings.AskWhereToSave,
            on => Change(() => settings.AskWhereToSave = on, rebuild: true))));
        if (settings.AskWhereToSave)
        {
            root.Children.Add(Note("Chromium tabs save straight to the place you choose. Mozilla tabs start the download in the download folder and move the file there when it finishes."));
        }
        root.Children.Add(Card(Toggle("Open safe files after downloading",
            "Documents, pictures, audio and video. Never programs, installers, disk images, scripts or archives.",
            settings.OpenSafeFiles, on => Change(() => settings.OpenSafeFiles = on))));
        root.Children.Add(Card(Toggle("Notify when a download finishes",
            "Shown in Mosaic while you are using it, as a Windows notification otherwise.",
            settings.NotifyWhenFinished, on => Change(() => settings.NotifyWhenFinished = on))));

        // ---- Organisation ----
        Section("Organisation");
        root.Children.Add(Card(Toggle("Sort downloads into folders by type", null, settings.Rules.IsEnabled,
            on => Change(() => settings.Rules = settings.Rules with { IsEnabled = on }, rebuild: true))));
        if (settings.Rules.IsEnabled)
        {
            foreach (var category in DownloadFileCategories.All) root.Children.Add(Card(CategoryRow(category)));
        }
        root.Children.Add(Note("Folders are created inside the download folder when first needed. Other kinds of files, and files you place yourself with “Ask where to save”, are left where they are saved."));

        // ---- Safety ----
        Section("Safety");
        root.Children.Add(Card(Explained("checkmark.shield",
            "Every download is marked as coming from the internet (the Mark of the Web, with its address), so Windows SmartScreen and Office's Protected View check it when it is opened.")));
        root.Children.Add(Card(Explained("exclamationmark.triangle",
            "Mosaic warns before you open programs, installers, disk images, scripts and shortcuts, files whose names disguise their type (such as “invoice.pdf.exe”), and files you have already downloaded. These checks happen on your PC; nothing about your files is sent anywhere.")));

        // ---- Free Download Manager ----
        Section("Free Download Manager" + (downloads.Fdm.Version is { } version ? $"  {version}" : ""));
        if (!downloads.Fdm.IsInstalled)
        {
            root.Children.Add(Card(Explained("arrow.down.app",
                "Free Download Manager is not installed. When it is, Mosaic can hand downloads to it: always, or by asking each time. Mosaic checks again whenever this page opens.")));
            return;
        }
        var mode = new ComboBox { MinWidth = 260 };
        AutomationProperties.SetName(mode, "Use Free Download Manager");
        foreach (var option in Enum.GetValues<FdmMode>()) mode.Items.Add(option.Title());
        mode.SelectedIndex = (int)settings.FdmMode;
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex >= 0) Change(() => settings.FdmMode = (FdmMode)mode.SelectedIndex, rebuild: true);
        };
        root.Children.Add(Card(Labeled("Use Free Download Manager", mode)));
        if (settings.FdmMode != FdmMode.Off)
        {
            var threshold = new ComboBox { MinWidth = 260 };
            AutomationProperties.SetName(threshold, "For downloads");
            foreach (var megabytes in Thresholds)
            {
                threshold.Items.Add(megabytes == 0 ? "Of any size" : $"Larger than {DownloadFormat.Bytes(megabytes * 1_000_000L)}");
            }
            threshold.SelectedIndex = Math.Max(0, Array.IndexOf(Thresholds, settings.FdmMinimumMB));
            threshold.SelectionChanged += (_, _) =>
            {
                if (threshold.SelectedIndex >= 0) Change(() => settings.FdmMinimumMB = Thresholds[threshold.SelectedIndex]);
            };
            root.Children.Add(Card(Labeled("For downloads", threshold)));
            root.Children.Add(Note("Downloads whose size the server does not give count as large."));
        }
        root.Children.Add(Card(Toggle("“Download linked file with Free Download Manager” in link menus", null,
            settings.FdmContextMenu, on => Change(() => settings.FdmContextMenu = on))));
        root.Children.Add(Card(Toggle("Pass the site’s cookies",
            "Lets Free Download Manager download files from sites you are signed in to. The cookies for that address are given to Free Download Manager on this PC.",
            settings.FdmPassCookies, on => Change(() => settings.FdmPassCookies = on, rebuild: true))));
        root.Children.Add(Note(
            "Free Download Manager receives the address, the page it came from and the browser’s identity" +
            (settings.FdmPassCookies ? ", plus the site’s cookies" : "") +
            ", and asks you to confirm in its own window. It cannot receive form submissions (POST), sign-ins that use a password prompt or certificate, or files a page creates itself (blob: and data: links); those always download in Mosaic." +
            (settings.FdmPassCookies ? "" : " Without cookies, downloads from sites that need you to be signed in usually fail in Free Download Manager.")));
    }

    private UIElement CategoryRow(DownloadFileCategory category)
    {
        var enabled = settings.Rules.Folders.ContainsKey(category);
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var check = new CheckBox { IsChecked = enabled, MinWidth = 0 };
        AutomationProperties.SetName(check, $"Sort {category.Title()}");
        row.Children.Add(check);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(Icons.Make(category.Symbol(), 14));
        title.Children.Add(new TextBlock { Text = category.Title() });
        labels.Children.Add(title);
        labels.Children.Add(new TextBlock { Text = category.Examples(), FontSize = 12, Opacity = 0.6 });
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        var name = new TextBox
        {
            Text = settings.Rules.Folders.TryGetValue(category, out var folder) ? folder : category.DefaultFolder(),
            Width = 160,
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(name, $"{category.Title()} folder name");
        name.LostFocus += (_, _) =>
        {
            if (name.IsEnabled) Change(() => settings.Rules = settings.Rules.WithFolder(category, name.Text));
        };
        check.Click += (_, _) =>
        {
            var on = check.IsChecked == true;
            name.IsEnabled = on;
            Change(() => settings.Rules = settings.Rules.WithFolder(category, on ? name.Text : null));
        };
        Grid.SetColumn(name, 2);
        row.Children.Add(name);
        return row;
    }

    private async Task ChooseFolderAsync()
    {
        if (DownloadsMounts.WindowHandle == 0) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads, CommitButtonText = "Choose" };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, DownloadsMounts.WindowHandle);
        FeatureMounts.CoverPage("downloads.picker", true);
        try
        {
            if (await picker.PickSingleFolderAsync() is { } chosen) settings.FolderPath = chosen.Path;
        }
        catch (Exception)
        {
        }
        finally
        {
            FeatureMounts.CoverPage("downloads.picker", false);
        }
    }

    // ---- Building blocks ----

    private void Section(string title) =>
        root.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 16, 0, 4),
        });

    private static Border Card(UIElement content) => new()
    {
        Child = content,
        Padding = new Thickness(16, 10, 16, 10),
        CornerRadius = new CornerRadius(6),
        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(4, 2, 4, 6),
    };

    private static UIElement Toggle(string title, string? detail, bool value, Action<bool> changed)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (detail is not null) labels.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(labels);
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(toggle, title);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    private static UIElement Labeled(string title, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static UIElement Explained(string symbol, string text)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Icons.Make(symbol, 16));
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        return grid;
    }
}
