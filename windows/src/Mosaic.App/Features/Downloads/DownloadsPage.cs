using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Downloads;
using Mosaic.Core.Shell;
using Windows.System;

namespace Mosaic.App.Features.Downloads;

/// <summary>
/// Internal page <c>mosaic://downloads</c>: the whole download history, searchable, grouped by
/// day (port of <c>DownloadsPage.swift</c>).
/// </summary>
internal sealed class DownloadsPage : UserControl
{
    private const int PageSize = 200;

    private readonly DownloadManager downloads;
    private readonly TextBlock savingTo = new() { Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox search = new() { PlaceholderText = "Search downloads", Width = 260 };
    private readonly Button clear = new() { Content = "Clear list" };
    private readonly StackPanel list = new() { Spacing = 0 };
    private readonly TextBlock emptyTitle = new() { Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock emptyDetail = new() { Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel empty = new() { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
    private readonly Button more = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12) };
    private readonly Dictionary<Guid, DownloadRowView> rows = [];
    private readonly Dictionary<string, TextBlock> headers = [];
    private int limit = PageSize;
    private bool refreshQueued;

    public DownloadsPage(BrowserModel model)
    {
        downloads = model.Downloads;
        Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header: title, where files go, search, Clear list, folder.
        var header = new Grid { Padding = new Thickness(24, 16, 24, 16), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = "Downloads", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        var where = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        where.Children.Add(savingTo);
        var change = new HyperlinkButton { Content = "Change…", Padding = new Thickness(4, 0, 4, 0) };
        ToolTipService.SetToolTip(change, "Settings › Downloads");
        change.Click += (_, _) => OpenSettings(model);
        where.Children.Add(change);
        titles.Children.Add(where);
        header.Children.Add(titles);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(search, "Search downloads");
        search.Text = downloads.SearchText;
        search.TextChanged += (_, _) => { limit = PageSize; downloads.SearchText = search.Text; };
        tools.Children.Add(search);
        ToolTipService.SetToolTip(clear, "Remove finished downloads from the list (files stay where they are)");
        clear.Click += (_, _) => downloads.ClearFinished();
        tools.Children.Add(clear);
        var folder = new Button { Content = Icons.Make("folder", 16) };
        ToolTipService.SetToolTip(folder, "Open the download folder");
        AutomationProperties.SetName(folder, "Open the download folder");
        folder.Click += (_, _) => downloads.OpenFolder();
        tools.Children.Add(folder);
        Grid.SetColumn(tools, 1);
        header.Children.Add(tools);
        root.Children.Add(header);

        empty.Children.Add(emptyTitle);
        empty.Children.Add(emptyDetail);
        more.Click += (_, _) => { limit += PageSize; Refresh(); };
        var content = new StackPanel { Padding = new Thickness(16, 0, 16, 16) };
        content.Children.Add(list);
        content.Children.Add(more);
        var scroller = new ScrollViewer { Content = content };
        var body = new Grid();
        body.Children.Add(scroller);
        body.Children.Add(empty);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;

        // Delete removes the focused row from the list, as on macOS.
        KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Delete || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused) return;
            for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is DownloadRowView row)
                {
                    downloads.Remove(row.Id);
                    args.Handled = true;
                    return;
                }
            }
        };

        Loaded += (_, _) =>
        {
            downloads.Changed += QueueRefresh;
            downloads.PropertyChanged += OnChanged;
            downloads.Settings.PropertyChanged += OnChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            downloads.Changed -= QueueRefresh;
            downloads.PropertyChanged -= OnChanged;
            downloads.Settings.PropertyChanged -= OnChanged;
        };
        Refresh();
    }

    private static void OpenSettings(BrowserModel model) => _ = DownloadsMounts.OpenSettingsAsync(model);

    private void OnChanged(object? sender, global::System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DownloadManager.SearchText) && search.Text != downloads.SearchText) search.Text = downloads.SearchText;
        QueueRefresh();
    }

    /// <summary>Many changes in one turn (a progress tick for each download) make one refresh.</summary>
    private void QueueRefresh()
    {
        if (refreshQueued) return;
        refreshQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        var settings = downloads.Settings;
        savingTo.Text = "Saving to " + (settings.AskWhereToSave ? "a place you choose each time" : settings.FolderDisplayPath);
        clear.IsEnabled = downloads.Items.Any(record => !record.State.IsActive());

        var records = downloads.FilteredItems;
        if (downloads.Items.Count == 0)
        {
            emptyTitle.Text = "No downloads";
            emptyDetail.Text = "Files you download in Chromium or Mozilla tabs appear here.";
        }
        else if (records.Count == 0)
        {
            emptyTitle.Text = "No results";
            emptyDetail.Text = $"Nothing in your downloads matches “{downloads.SearchText}”.";
        }
        empty.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        more.Visibility = records.Count > limit ? Visibility.Visible : Visibility.Collapsed;
        more.Content = $"Show {Math.Min(PageSize, records.Count - limit)} more";

        var now = DateTimeOffset.Now;
        var children = new List<UIElement>();
        var wanted = new HashSet<Guid>();
        string? section = null;
        foreach (var record in records.Take(limit))
        {
            var title = DownloadFormat.DaySection(record.StartedAt, now);
            if (title != section)
            {
                section = title;
                if (!headers.TryGetValue(title, out var label))
                {
                    headers[title] = label = new TextBlock
                    {
                        Text = title,
                        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                        Margin = new Thickness(8, 16, 0, 4),
                    };
                    AutomationProperties.SetHeadingLevel(label, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
                }
                children.Add(label);
            }
            wanted.Add(record.Id);
            if (rows.TryGetValue(record.Id, out var row)) row.Update(record);
            else rows[record.Id] = row = new DownloadRowView(downloads, record, compact: false);
            children.Add(row);
        }
        foreach (var gone in rows.Keys.Where(id => !wanted.Contains(id)).ToList()) rows.Remove(gone);
        if (!list.Children.SequenceEqual(children))
        {
            list.Children.Clear();
            foreach (var child in children) list.Children.Add(child);
        }
    }
}
