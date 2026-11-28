using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Downloads;

namespace Mosaic.App.Features.Downloads;

/// <summary>
/// Layout component "downloads": a toolbar button with the overall progress, opening the
/// downloads flyout (port of <c>DownloadsToolbarButton.swift</c>). Like Safari's, it shows only
/// once there is something in the download list, so it never sits in the toolbar unused.
/// </summary>
internal sealed class DownloadsToolbarButton : UserControl
{
    private readonly DownloadManager downloads;
    private readonly Button button = new()
    {
        Width = 36,
        Height = 32,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        BorderThickness = new Thickness(0),
    };
    private readonly ProgressRing ring = new() { Width = 24, Height = 24, IsActive = false, Minimum = 0, Maximum = 100 };
    private readonly FontIcon glyph = Icons.Make("arrow.down.circle", 16);
    private readonly Border dot = new()
    {
        Width = 7,
        Height = 7,
        CornerRadius = new CornerRadius(3.5),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 3, 4, 0),
    };
    private readonly Flyout flyout;

    public DownloadsToolbarButton(DownloadManager downloads)
    {
        this.downloads = downloads;
        dot.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var face = new Grid();
        face.Children.Add(ring);
        face.Children.Add(glyph);
        face.Children.Add(dot);
        button.Content = face;
        AutomationProperties.SetName(button, "Downloads");

        flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight, Content = new DownloadsFlyoutContent(downloads) };
        flyout.Opened += (_, _) =>
        {
            FeatureMounts.CoverPage("downloads.flyout", true);
            downloads.IsPopoverShown = true;
        };
        flyout.Closed += (_, _) =>
        {
            FeatureMounts.CoverPage("downloads.flyout", false);
            downloads.IsPopoverShown = false;
        };
        button.Flyout = flyout;
        Content = button;

        // Listens only while it is on screen, so a closed layout leaves nothing behind.
        Loaded += (_, _) =>
        {
            downloads.Changed += Refresh;
            downloads.PropertyChanged += OnManagerChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            downloads.Changed -= Refresh;
            downloads.PropertyChanged -= OnManagerChanged;
        };
        Refresh();
    }

    private void OnManagerChanged(object? sender, global::System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DownloadManager.IsPopoverShown))
        {
            // Automation (downloadsPopover) and "Show all" open and close it too.
            if (downloads.IsPopoverShown && !flyout.IsOpen && IsLoaded && XamlRoot is not null) flyout.ShowAt(button);
            else if (!downloads.IsPopoverShown && flyout.IsOpen) flyout.Hide();
        }
        Refresh();
    }

    private void Refresh()
    {
        Visibility = downloads.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var active = downloads.ActiveCount;
        var progress = downloads.OverallProgress;
        ring.IsActive = active > 0;
        ring.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        ring.IsIndeterminate = progress is null;
        if (progress is { } value) ring.Value = Math.Max(2, value * 100);
        glyph.Glyph = Icons.Glyph(active > 0 ? "arrow.down" : "arrow.down.circle");
        glyph.FontSize = active > 0 ? 11 : 16;
        dot.Visibility = downloads.HasUnseenCompletion && active == 0 ? Visibility.Visible : Visibility.Collapsed;
        var help = HelpText(active, progress);
        ToolTipService.SetToolTip(button, help);
        AutomationProperties.SetHelpText(button, help);
    }

    private string HelpText(int active, double? progress)
    {
        if (active == 0) return downloads.HasUnseenCompletion ? "Downloads (finished)" : "Downloads (Ctrl+J for all)";
        var percent = progress is { } value ? $", {(int)(value * 100)}%" : "";
        return active == 1 ? $"1 download in progress{percent}" : $"{active} downloads in progress{percent}";
    }
}

/// <summary>The compact list of recent downloads in the toolbar button's flyout.</summary>
internal sealed class DownloadsFlyoutContent : UserControl
{
    private const int Shown = 12;

    private readonly DownloadManager downloads;
    private readonly StackPanel list = new();
    private readonly TextBlock empty = new()
    {
        Text = "No downloads",
        Opacity = 0.6,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(24),
    };
    private readonly Button clear = new() { Content = "Clear" };
    private readonly HyperlinkButton folder = new();
    private readonly Dictionary<Guid, DownloadRowView> rows = [];

    public DownloadsFlyoutContent(DownloadManager downloads)
    {
        this.downloads = downloads;
        Width = 360;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Padding = new Thickness(4, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = "Downloads", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        var showAll = new HyperlinkButton { Content = "Show all", HorizontalAlignment = HorizontalAlignment.Right };
        ToolTipService.SetToolTip(showAll, "Open the Downloads page (Ctrl+J)");
        showAll.Click += (_, _) => downloads.OpenDownloadsPage();
        header.Children.Add(showAll);
        root.Children.Add(header);

        var scroller = new ScrollViewer { MaxHeight = 380, Content = list };
        var middle = new Grid();
        middle.Children.Add(scroller);
        middle.Children.Add(empty);
        Grid.SetRow(middle, 1);
        root.Children.Add(middle);

        var footer = new Grid { Padding = new Thickness(0, 8, 0, 0), ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ToolTipService.SetToolTip(clear, "Remove finished downloads from the list (files stay)");
        clear.Click += (_, _) => downloads.ClearFinished();
        footer.Children.Add(clear);
        folder.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTipService.SetToolTip(folder, "Open the download folder");
        folder.Click += (_, _) => downloads.OpenFolder();
        Grid.SetColumn(folder, 1);
        footer.Children.Add(folder);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        downloads.Changed += Refresh;
        downloads.Settings.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        var items = downloads.Items.Take(Shown).ToList();
        empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        clear.IsEnabled = downloads.Items.Any(record => !record.State.IsActive());
        var folderText = new TextBlock { Text = downloads.Settings.FolderDisplayPath, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 };
        folder.Content = folderText;

        var wanted = items.Select(record => record.Id).ToList();
        foreach (var gone in rows.Keys.Except(wanted).ToList()) rows.Remove(gone);
        var children = new List<UIElement>();
        foreach (var record in items)
        {
            if (rows.TryGetValue(record.Id, out var row)) row.Update(record);
            else rows[record.Id] = row = new DownloadRowView(downloads, record, compact: true);
            children.Add(row);
        }
        if (!list.Children.SequenceEqual(children))
        {
            list.Children.Clear();
            foreach (var child in children) list.Children.Add(child);
        }
    }
}
