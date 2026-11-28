using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;
using Windows.System;

namespace Mosaic.App.Features.Library;

/// <summary>
/// The History page (<c>mosaic://history</c>, Ctrl+H): every visit in both engines, newest first,
/// grouped by day; search; delete a visit, everything from a site, or a time range. Port of
/// <c>HistoryPage.swift</c>. Loads a page of visits at a time ("Show more" continues).
/// </summary>
internal sealed class HistoryPage : Grid
{
    private const int PageSize = 300;

    private readonly BrowserModel model;
    private readonly BrowserLibrary library;
    private readonly TextBlock subtitle = new();
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Extended, Padding = new Thickness(16, 0, 16, 16) };
    private readonly Grid listHost = new();
    private readonly TextBox search;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer reloadTimer;
    private readonly List<HistoryVisit> shown = [];
    private string query = "";
    private int version;
    private bool hasMore;

    public HistoryPage(BrowserModel model)
    {
        this.model = model;
        library = model.Library;
        Background = LibraryUi.PageBackground;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        search = LibraryUi.SearchBox("Search history", text =>
        {
            query = text.Trim();
            _ = ReloadAsync();
        });
        var clear = new DropDownButton { Content = "Clear History" };
        var clearMenu = LibraryUi.CoveringMenu("library.historyPage");
        foreach (var (range, title) in new[] { (LibraryClearRange.LastHour, "the Last Hour"), (LibraryClearRange.Today, "Today"), (LibraryClearRange.All, "All History") })
        {
            clearMenu.Items.Add(LibraryUi.MenuItem($"Clear {title}…", () => _ = ClearAsync(range, title)));
        }
        clear.Flyout = clearMenu;
        Children.Add(LibraryUi.PageHeader("History", subtitle, search, clear));
        var warning = LibraryUi.StorageWarning(library);
        SetRow(warning, 1);
        Children.Add(warning);

        AutomationProperties.SetName(list, "History");
        list.KeyDown += OnListKeyDown;
        listHost.Children.Add(list);
        SetRow(listHost, 2);
        Children.Add(listHost);

        // History changes with every page visited; reload at most about once a second while shown.
        reloadTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        reloadTimer.Interval = TimeSpan.FromSeconds(1);
        reloadTimer.IsRepeating = false;
        reloadTimer.Tick += (_, _) => _ = ReloadAsync();
        LibraryUi.Observe(this, library, [nameof(BrowserLibrary.HistoryRevision), nameof(BrowserLibrary.HistoryCounts), nameof(BrowserLibrary.IsLoaded)], () =>
        {
            UpdateSubtitle();
            if (!reloadTimer.IsRunning) reloadTimer.Start();
        });
        Loaded += (_, _) => _ = ReloadAsync();
        Unloaded += (_, _) => reloadTimer.Stop();
    }

    private void UpdateSubtitle()
    {
        var counts = library.HistoryCounts;
        subtitle.Text = $"{Count(counts.Addresses, "page")} · {Count(counts.Visits, "visit")}";
    }

    private static string Count(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} {noun}s";

    private async Task ReloadAsync()
    {
        var current = ++version;
        var visits = await library.VisitsAsync(query, limit: PageSize);
        if (current != version) return;
        shown.Clear();
        shown.AddRange(visits);
        hasMore = visits.Count == PageSize;
        Render();
    }

    private async Task ShowMoreAsync()
    {
        if (shown.Count == 0) return;
        var current = version;
        var visits = await library.VisitsAsync(query, before: shown[^1].Time, limit: PageSize);
        if (current != version) return;
        shown.AddRange(visits);
        hasMore = visits.Count == PageSize;
        Render();
    }

    private void Render()
    {
        UpdateSubtitle();
        var selected = SelectedIds().ToHashSet();
        list.Items.Clear();
        DateTime? day = null;
        foreach (var visit in shown)
        {
            var local = visit.Time.ToLocalTime().Date;
            if (day != local)
            {
                day = local;
                list.Items.Add(DayHeader(local));
            }
            var row = Row(visit);
            list.Items.Add(row);
            if (selected.Contains(visit.Id)) list.SelectedItems.Add(row);
        }
        if (hasMore)
        {
            var more = new Button { Content = "Show More", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
            more.Click += (_, _) => _ = ShowMoreAsync();
            list.Items.Add(more);
        }
        listHost.Children.Clear();
        listHost.Children.Add(list);
        if (shown.Count == 0)
        {
            listHost.Children.Add(!library.IsLoaded ? new ProgressRing { IsActive = true, Width = 32, Height = 32 }
                : query.Length > 0 ? LibraryUi.EmptyState("search", "No Results", $"No visited page’s address or title contains “{query}”.")
                : LibraryUi.EmptyState("clock", "No History", "Pages you visit in either engine appear here. Private tabs are never recorded."));
        }
    }

    private static UIElement DayHeader(DateTime day)
    {
        var today = DateTime.Today;
        var text = day == today ? "Today"
            : day == today.AddDays(-1) ? "Yesterday"
            : day.Year == today.Year ? day.ToString("dddd d MMMM", CultureInfo.CurrentCulture)
            : day.ToString("D", CultureInfo.CurrentCulture);
        var header = LibraryUi.Text(text, "BodyStrongTextBlockStyle");
        header.Margin = new Thickness(0, 14, 0, 4);
        header.IsHitTestVisible = false;
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return header;
    }

    private FrameworkElement Row(HistoryVisit visit)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 5, 0, 5), Tag = visit.Id };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var time = visit.Time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        row.Children.Add(LibraryUi.Text(time, secondary: true, size: 12));
        var monogram = LibraryUi.Monogram(visit.Url, 20);
        SetColumn((FrameworkElement)monogram, 1);
        row.Children.Add(monogram);

        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        text.Children.Add(LibraryUi.Text(visit.DisplayTitle));
        text.Children.Add(LibraryUi.Text(visit.Host, secondary: true, size: 12));
        SetColumn(text, 2);
        row.Children.Add(text);
        if (visit.Typed)
        {
            var typed = LibraryUi.Icon("keyboard", 12);
            typed.Foreground = LibraryUi.SecondaryText;
            ToolTipService.SetToolTip(typed, "Typed in the address bar");
            AutomationProperties.SetName(typed, "Typed");
            SetColumn(typed, 3);
            row.Children.Add(typed);
        }
        var delete = LibraryUi.IconButton("xmark", "Delete visit", (_, _) => _ = library.DeleteVisitsAsync([visit.Id]), 10);
        SetColumn(delete, 4);
        row.Children.Add(delete);

        AutomationProperties.SetName(row, $"{visit.DisplayTitle}, {visit.Host}, {time}");
        ToolTipService.SetToolTip(row, visit.Url);
        row.DoubleTapped += (_, _) => LibraryUi.Open(model, visit.Url);
        LibraryUi.OpenOnMiddleClick(row, model, () => visit.Url);
        var menu = LibraryUi.CoveringMenu("library.historyPage");
        menu.Opening += (_, _) => FillMenu(menu, visit);
        row.ContextFlyout = menu;
        return row;
    }

    private void FillMenu(MenuFlyout menu, HistoryVisit visit)
    {
        menu.Items.Clear();
        var ids = SelectedIds();
        if (ids.Count > 1 && ids.Contains(visit.Id))
        {
            var chosen = shown.Where(v => ids.Contains(v.Id)).ToList();
            menu.Items.Add(LibraryUi.MenuItem($"Open {chosen.Count} in New Tabs", () =>
            {
                foreach (var item in chosen) model.OpenFromLibrary(item.Url, newTab: true, select: false);
            }, "plus.square.on.square"));
            menu.Items.Add(LibraryUi.MenuItem($"Delete {chosen.Count} Visits", () => _ = library.DeleteVisitsAsync(ids), "trash"));
            return;
        }
        menu.Items.Add(LibraryUi.MenuItem("Open", () => model.OpenFromLibrary(visit.Url)));
        menu.Items.Add(LibraryUi.MenuItem("Open in New Tab", () => model.OpenFromLibrary(visit.Url, newTab: true), "plus.square.on.square"));
        menu.Items.Add(LibraryUi.MenuItem("Copy Address", () => LibraryUi.Copy(visit.Url), "doc.on.doc"));
        var bookmarked = library.IsBookmarked(visit.Url);
        menu.Items.Add(LibraryUi.MenuItem(bookmarked ? "Remove Bookmark" : "Bookmark", () => _ = library.ToggleBookmarkAsync(visit.Url, visit.Title), bookmarked ? "star.fill" : "star"));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (visit.Host.Length > 0) menu.Items.Add(LibraryUi.MenuItem($"More from {visit.Host}", () => search.Text = visit.Host, "search"));
        menu.Items.Add(LibraryUi.MenuItem("Delete", () => _ = library.DeleteVisitsAsync([visit.Id]), "trash"));
        if (visit.Host.Length > 0) menu.Items.Add(LibraryUi.MenuItem($"Delete All from {visit.Host}…", () => _ = DeleteSiteAsync(visit.Host)));
    }

    private List<long> SelectedIds() => [.. list.SelectedItems.OfType<FrameworkElement>().Select(row => row.Tag).OfType<long>()];

    private async Task DeleteSiteAsync(string host)
    {
        if (!await LibraryUi.ConfirmAsync(this, $"Delete all history from {host}?", "Every visit to this site and its subdomains is removed.", "Delete")) return;
        await library.DeleteHistorySiteAsync(host);
    }

    private async Task ClearAsync(LibraryClearRange range, string title)
    {
        if (!await LibraryUi.ConfirmAsync(this, $"Clear {title}?", "Bookmarks and the Reading List are not affected. This cannot be undone.", "Clear")) return;
        await library.ClearHistoryAsync(range);
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        switch (args.Key)
        {
            case VirtualKey.Delete when list.SelectedItems.Count > 0:
                _ = library.DeleteVisitsAsync(SelectedIds());
                args.Handled = true;
                break;
            case VirtualKey.Enter when list.SelectedItem is FrameworkElement { Tag: long id } && shown.FirstOrDefault(v => v.Id == id) is { } visit:
                LibraryUi.Open(model, visit.Url);
                args.Handled = true;
                break;
        }
    }
}
