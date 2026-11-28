using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;
using Windows.System;

namespace Mosaic.App.Features.Library;

/// <summary>
/// The Reading List (<c>mosaic://reading-list</c>): pages saved to read later, unread first.
/// Opening one marks it read. Port of <c>ReadingListPage.swift</c>.
/// </summary>
internal sealed class ReadingListPage : Grid
{
    private readonly BrowserModel model;
    private readonly BrowserLibrary library;
    private readonly TextBlock subtitle = new();
    private readonly ComboBox filter = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Extended, Padding = new Thickness(16, 0, 16, 16) };
    private readonly Grid listHost = new();

    public ReadingListPage(BrowserModel model)
    {
        this.model = model;
        library = model.Library;
        Background = LibraryUi.PageBackground;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        filter.Items.Add("All");
        filter.Items.Add("Unread");
        filter.SelectedIndex = 0;
        AutomationProperties.SetName(filter, "Show");
        filter.SelectionChanged += (_, _) => Render();
        Children.Add(LibraryUi.PageHeader("Reading List", subtitle, filter));
        var warning = LibraryUi.StorageWarning(library);
        SetRow(warning, 1);
        Children.Add(warning);

        AutomationProperties.SetName(list, "Reading List");
        list.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Delete)
            {
                foreach (var item in Selected()) _ = library.RemoveFromReadingListAsync(item);
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.Enter && Selected() is [var only])
            {
                Open(only, newTab: false);
                args.Handled = true;
            }
        };
        listHost.Children.Add(list);
        SetRow(listHost, 2);
        Children.Add(listHost);

        LibraryUi.Observe(this, library, [nameof(BrowserLibrary.ReadingList), nameof(BrowserLibrary.IsLoaded)], Render);
        Loaded += (_, _) => library.EnsureLoaded();
    }

    private bool UnreadOnly => filter.SelectedIndex == 1;

    private void Render()
    {
        var all = library.ReadingList;
        var unread = all.Count(item => !item.IsRead);
        subtitle.Text = all.Count == 0 ? "Nothing saved" : $"{unread} unread · {all.Count} in all";
        list.Items.Clear();
        foreach (var item in all.Where(item => !UnreadOnly || !item.IsRead)) list.Items.Add(Row(item));
        listHost.Children.Clear();
        listHost.Children.Add(list);
        if (list.Items.Count > 0) return;
        if (!library.IsLoaded)
        {
            listHost.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
        }
        else if (all.Count > 0)
        {
            var showAll = new Button { Content = "Show All" };
            showAll.Click += (_, _) => filter.SelectedIndex = 0;
            listHost.Children.Add(LibraryUi.EmptyState("checkmark.circle", "All Caught Up", "You have read everything on your list.", showAll));
        }
        else
        {
            listHost.Children.Add(LibraryUi.EmptyState("eyeglasses", "Your Reading List Is Empty",
                "To save the page you are on for later, right-click the bookmark star or the bookmarks bar and choose Add to Reading List."));
        }
    }

    private FrameworkElement Row(ReadingListItem item)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Tag = item.Id };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var toggle = LibraryUi.IconButton(item.IsRead ? "checkmark.circle" : "circle", item.IsRead ? "Mark as Unread" : "Mark as Read",
            (_, _) => _ = library.SetReadAsync(item, !item.IsRead));
        row.Children.Add(toggle);
        var monogram = (FrameworkElement)LibraryUi.Monogram(item.Url, 28);
        SetColumn(monogram, 1);
        row.Children.Add(monogram);

        var text = new StackPanel { Spacing = 1 };
        var title = LibraryUi.Text(item.DisplayTitle, item.IsRead ? null : "BodyStrongTextBlockStyle");
        if (item.IsRead) title.Foreground = LibraryUi.SecondaryText;
        text.Children.Add(title);
        text.Children.Add(LibraryUi.Text(LibraryUrl.Host(item.Url), secondary: true, size: 12));
        SetColumn(text, 2);
        row.Children.Add(text);

        var date = LibraryUi.Text(LibraryUi.ShortDate(item.Added), secondary: true, size: 12);
        SetColumn(date, 3);
        row.Children.Add(date);
        var remove = LibraryUi.IconButton("xmark", "Remove from Reading List", (_, _) => _ = library.RemoveFromReadingListAsync(item), 10);
        SetColumn(remove, 4);
        row.Children.Add(remove);

        AutomationProperties.SetName(row, $"{item.DisplayTitle}, {LibraryUrl.Host(item.Url)}{(item.IsRead ? ", read" : "")}");
        ToolTipService.SetToolTip(row, item.Url);
        row.DoubleTapped += (_, _) => Open(item, newTab: false);
        LibraryUi.OpenOnMiddleClick(row, model, () =>
        {
            _ = library.SetReadAsync(item, true);
            return item.Url;
        });
        var menu = LibraryUi.CoveringMenu("library.readingList");
        menu.Opening += (_, _) => FillMenu(menu, item);
        row.ContextFlyout = menu;
        return row;
    }

    private void FillMenu(MenuFlyout menu, ReadingListItem item)
    {
        menu.Items.Clear();
        var chosen = Selected();
        if (chosen.Count > 1 && chosen.Any(c => c.Id == item.Id))
        {
            menu.Items.Add(LibraryUi.MenuItem($"Mark {chosen.Count} as Read", () => { foreach (var c in chosen) _ = library.SetReadAsync(c, true); }, "checkmark.circle"));
            menu.Items.Add(LibraryUi.MenuItem($"Mark {chosen.Count} as Unread", () => { foreach (var c in chosen) _ = library.SetReadAsync(c, false); }, "circle"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(LibraryUi.MenuItem($"Remove {chosen.Count} Items", () => { foreach (var c in chosen) _ = library.RemoveFromReadingListAsync(c); }, "trash"));
            return;
        }
        menu.Items.Add(LibraryUi.MenuItem("Open", () => Open(item, newTab: false)));
        menu.Items.Add(LibraryUi.MenuItem("Open in New Tab", () => Open(item, newTab: true), "plus.square.on.square"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(LibraryUi.MenuItem(item.IsRead ? "Mark as Unread" : "Mark as Read", () => _ = library.SetReadAsync(item, !item.IsRead), item.IsRead ? "circle" : "checkmark.circle"));
        menu.Items.Add(LibraryUi.MenuItem("Copy Address", () => LibraryUi.Copy(item.Url), "doc.on.doc"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(LibraryUi.MenuItem("Remove", () => _ = library.RemoveFromReadingListAsync(item), "trash"));
    }

    private List<ReadingListItem> Selected()
    {
        var ids = list.SelectedItems.OfType<FrameworkElement>().Select(row => row.Tag as string).ToHashSet();
        return [.. library.ReadingList.Where(item => ids.Contains(item.Id))];
    }

    private void Open(ReadingListItem item, bool newTab)
    {
        if (!item.IsRead) _ = library.SetReadAsync(item, true);
        LibraryUi.Open(model, item.Url, newTab);
    }
}
