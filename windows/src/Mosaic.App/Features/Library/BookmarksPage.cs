using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Mosaic.App.Features.Library;

/// <summary>
/// The bookmarks manager (<c>mosaic://bookmarks</c>, Ctrl+Shift+O): folders and tags on the
/// left, the folder's contents (or search results, or a tag's bookmarks) on the right. Port of
/// <c>BookmarksPage.swift</c>. Drag rows to reorder them or onto a folder on the left to file them.
/// </summary>
internal sealed class BookmarksPage : Grid
{
    private const string DragPrefix = "mosaic-bookmarks:";

    private readonly BrowserModel model;
    private readonly BrowserLibrary library;
    private readonly TextBlock subtitle = new();
    private readonly ListView sidebar = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(8, 0, 8, 8) };
    private readonly ListView list = new()
    {
        SelectionMode = ListViewSelectionMode.Extended,
        CanDragItems = true,
        AllowDrop = true,
        Padding = new Thickness(16, 0, 16, 16),
    };
    private readonly TextBlock location = LibraryUi.Text("", "BodyStrongTextBlockStyle");
    private readonly TextBlock selectionText = LibraryUi.Text("", secondary: true);
    private readonly Button deleteSelected;
    private readonly Button openAll;
    private readonly Grid listHost = new();
    private readonly TextBox search;

    private string folderId = Bookmark.BarId;
    private string? tag;
    private string query = "";
    private int searchVersion;
    private bool rebuildingSidebar;

    public BookmarksPage(BrowserModel model)
    {
        this.model = model;
        library = model.Library;
        Background = LibraryUi.PageBackground;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        search = LibraryUi.SearchBox("Search bookmarks", text =>
        {
            query = text.Trim();
            _ = ShowListAsync();
        });
        var newFolder = LibraryUi.IconButton("folder.badge.plus", "New Folder", async (_, _) =>
        {
            if (await BookmarkEditor.NewFolderAsync(model, this, tag is null && query.Length == 0 ? folderId : Bookmark.OtherId) is { } folder) Show(folder.Id);
        });
        var importExport = new DropDownButton { Content = LibraryUi.Icon("square.and.arrow.up", 14) };
        AutomationProperties.SetName(importExport, "Import and export");
        ToolTipService.SetToolTip(importExport, "Import or export bookmarks");
        var importMenu = LibraryUi.CoveringMenu("library.bookmarksPage");
        importMenu.Opening += (_, _) =>
        {
            importMenu.Items.Clear();
            foreach (var item in LibraryUi.ImportExportItems(model, this)) importMenu.Items.Add(item);
        };
        importExport.Flyout = importMenu;
        var header = LibraryUi.PageHeader("Bookmarks", subtitle, search, newFolder, importExport);
        Children.Add(header);
        var warning = LibraryUi.StorageWarning(library);
        SetRow(warning, 1);
        Children.Add(warning);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetRow(body, 2);
        Children.Add(body);

        AutomationProperties.SetName(sidebar, "Bookmark folders");
        sidebar.SelectionChanged += (_, _) =>
        {
            if (rebuildingSidebar || sidebar.SelectedItem is not FrameworkElement { Tag: (string kind, string value) }) return;
            if (kind == "folder") Show(value);
            else ShowTag(value);
        };
        body.Children.Add(sidebar);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SetColumn(right, 1);
        body.Children.Add(right);

        var bar = new Grid { Padding = new Thickness(20, 4, 28, 8), ColumnSpacing = 12 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(location);
        SetColumn(selectionText, 1);
        bar.Children.Add(selectionText);
        deleteSelected = new Button { Content = "Delete", Visibility = Visibility.Collapsed };
        deleteSelected.Click += (_, _) => _ = DeleteAsync(SelectedIds());
        SetColumn(deleteSelected, 2);
        bar.Children.Add(deleteSelected);
        openAll = new Button { Content = "Open All" };
        ToolTipService.SetToolTip(openAll, "Open every bookmark here in new tabs");
        openAll.Click += (_, _) => OpenAll(CurrentBookmarks());
        SetColumn(openAll, 3);
        bar.Children.Add(openAll);
        right.Children.Add(bar);

        AutomationProperties.SetName(list, "Bookmarks");
        list.SelectionChanged += (_, _) => UpdateSelectionBar();
        list.KeyDown += OnListKeyDown;
        list.DragItemsStarting += (_, args) =>
        {
            var ids = args.Items.OfType<FrameworkElement>().Select(row => row.Tag as string).OfType<string>().ToList();
            args.Data.SetText(DragPrefix + string.Join(",", ids));
            args.Data.RequestedOperation = DataPackageOperation.Move;
        };
        list.DragItemsCompleted += OnReordered;
        listHost.Children.Add(list);
        SetRow(listHost, 1);
        right.Children.Add(listHost);

        LibraryUi.Observe(this, library, [nameof(BrowserLibrary.Bookmarks), nameof(BrowserLibrary.IsLoaded)], () =>
        {
            RebuildSidebar();
            _ = ShowListAsync();
        });
        Loaded += (_, _) => library.EnsureLoaded();
    }

    // ---- Sidebar ----

    private void RebuildSidebar()
    {
        rebuildingSidebar = true;
        try
        {
            sidebar.Items.Clear();
            sidebar.Items.Add(SectionHeader("Folders"));
            foreach (var (folder, depth) in library.FolderTree())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(depth * 16, 2, 0, 2), Tag = ("folder", folder.Id) };
                row.Children.Add(LibraryUi.Icon(folder.Id == Bookmark.BarId ? "star" : "folder", 14));
                row.Children.Add(LibraryUi.Text(folder.DisplayTitle));
                AutomationProperties.SetName(row, folder.DisplayTitle);
                AcceptBookmarkDrops(row, folder.Id);
                row.ContextFlyout = FolderMenu(folder, row);
                sidebar.Items.Add(row);
                if (tag is null && query.Length == 0 && folder.Id == folderId) sidebar.SelectedItem = row;
            }
            var tags = library.AllTags;
            if (tags.Count > 0)
            {
                sidebar.Items.Add(SectionHeader("Tags"));
                foreach (var name in tags)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(0, 2, 0, 2), Tag = ("tag", name) };
                    row.Children.Add(LibraryUi.Icon("tag", 14));
                    row.Children.Add(LibraryUi.Text(name));
                    AutomationProperties.SetName(row, $"Tag {name}");
                    sidebar.Items.Add(row);
                    if (string.Equals(tag, name, StringComparison.OrdinalIgnoreCase)) sidebar.SelectedItem = row;
                }
            }
        }
        finally
        {
            rebuildingSidebar = false;
        }
    }

    private static TextBlock SectionHeader(string text)
    {
        var header = LibraryUi.Text(text, "CaptionTextBlockStyle", secondary: true);
        header.Margin = new Thickness(0, 12, 0, 4);
        header.IsHitTestVisible = false;
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return header;
    }

    private MenuFlyout FolderMenu(Bookmark folder, FrameworkElement anchor)
    {
        var menu = LibraryUi.CoveringMenu("library.bookmarksPage");
        menu.Items.Add(LibraryUi.MenuItem("Open All in New Tabs", () => OpenAll(library.Children(folder.Id)), "plus.square.on.square"));
        menu.Items.Add(LibraryUi.MenuItem("New Folder Inside…", () => _ = BookmarkEditor.NewFolderAsync(model, this, folder.Id), "folder.badge.plus"));
        if (!folder.IsRoot)
        {
            menu.Items.Add(LibraryUi.MenuItem("Rename…", () => BookmarkEditor.Show(model, folder.Id, null, anchor), "edit"));
            menu.Items.Add(LibraryUi.MenuItem("Delete…", () => _ = DeleteAsync([folder.Id]), "trash"));
        }
        return menu;
    }

    private void AcceptBookmarkDrops(FrameworkElement target, string folder)
    {
        target.AllowDrop = true;
        target.DragOver += (_, args) =>
        {
            if (args.DataView.Contains(StandardDataFormats.Text)) args.AcceptedOperation = DataPackageOperation.Move;
        };
        target.Drop += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var text = await args.DataView.GetTextAsync();
                if (!text.StartsWith(DragPrefix, StringComparison.Ordinal)) return;
                foreach (var id in text[DragPrefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    await library.MoveBookmarkAsync(id, folder);
                }
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    // ---- The list ----

    private void Show(string folder)
    {
        folderId = folder;
        tag = null;
        if (query.Length > 0) search.Text = "";
        query = "";
        RebuildSidebar();
        _ = ShowListAsync();
    }

    private void ShowTag(string name)
    {
        tag = name;
        if (query.Length > 0) search.Text = "";
        query = "";
        _ = ShowListAsync();
    }

    private IReadOnlyList<Bookmark> CurrentBookmarks() =>
        [.. list.Items.OfType<FrameworkElement>().Select(row => row.Tag as string).OfType<string>().Select(library.BookmarkById).OfType<Bookmark>()];

    private async Task ShowListAsync()
    {
        var version = ++searchVersion;
        var count = library.Bookmarks.Count(b => !b.IsFolder);
        subtitle.Text = count == 1 ? "1 bookmark" : $"{count} bookmarks";
        IReadOnlyList<Bookmark> shown;
        if (query.Length > 0)
        {
            location.Text = $"Results for “{query}”";
            shown = await library.SearchBookmarksAsync(query);
            if (version != searchVersion) return;
        }
        else if (tag is not null)
        {
            location.Text = $"Tagged “{tag}”";
            shown = library.BookmarksTagged(tag);
        }
        else
        {
            location.Text = PathOf(folderId);
            shown = library.Children(folderId);
        }
        // Reordering by dragging only makes sense inside one folder.
        list.CanReorderItems = query.Length == 0 && tag is null;
        list.Items.Clear();
        foreach (var bookmark in shown) list.Items.Add(Row(bookmark, showFolder: query.Length > 0 || tag is not null));
        openAll.Visibility = shown.Any(b => !b.IsFolder) ? Visibility.Visible : Visibility.Collapsed;
        listHost.Children.Clear();
        listHost.Children.Add(list);
        if (shown.Count == 0) listHost.Children.Add(EmptyState());
        UpdateSelectionBar();
    }

    private UIElement EmptyState()
    {
        if (!library.IsLoaded) return new ProgressRing { IsActive = true, Width = 32, Height = 32 };
        if (query.Length > 0) return LibraryUi.EmptyState("search", "No Results", $"No bookmark’s name, address or tags contain “{query}”.");
        if (folderId == Bookmark.BarId && tag is null)
        {
            var import = new Button { Content = "Import Bookmarks…" };
            import.Click += (_, _) => _ = LibraryUi.ImportAsync(model, this);
            return LibraryUi.EmptyState("star", "The Bookmarks Bar Is Empty",
                "Press Ctrl+D on any page to bookmark it, or drag bookmarks here from another folder.", import);
        }
        return LibraryUi.EmptyState("folder", "This Folder Is Empty", "Drag bookmarks onto this folder on the left to file them here.");
    }

    private string PathOf(string id)
    {
        var names = new List<string>();
        string? cursor = id;
        while (cursor is not null && library.BookmarkById(cursor) is { } folder)
        {
            names.Insert(0, folder.DisplayTitle);
            cursor = folder.ParentId;
        }
        return string.Join(" › ", names);
    }

    private FrameworkElement Row(Bookmark bookmark, bool showFolder)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6), Tag = bookmark.Id };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(bookmark.IsFolder ? LibraryUi.Icon("folder", 20) : LibraryUi.Monogram(bookmark.Url ?? ""));

        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(LibraryUi.Text(bookmark.DisplayTitle));
        var detail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        detail.Children.Add(LibraryUi.Text(bookmark.Url is { } url ? LibraryUrl.Display(url) : "Folder", secondary: true, size: 12));
        if (showFolder && bookmark.ParentId is { } parent)
        {
            detail.Children.Add(LibraryUi.Text("·", secondary: true, size: 12));
            detail.Children.Add(LibraryUi.Text(PathOf(parent), secondary: true, size: 12));
        }
        foreach (var name in bookmark.Tags) detail.Children.Add(LibraryUi.TagChip(name));
        text.Children.Add(detail);
        SetColumn(text, 1);
        row.Children.Add(text);

        var date = LibraryUi.Text(LibraryUi.ShortDate(bookmark.Created), secondary: true, size: 12);
        SetColumn(date, 2);
        row.Children.Add(date);

        AutomationProperties.SetName(row, bookmark.IsFolder ? $"{bookmark.DisplayTitle}, folder" : $"{bookmark.DisplayTitle}, {bookmark.Url}");
        if (bookmark.Url is { } address) ToolTipService.SetToolTip(row, address);
        row.DoubleTapped += (_, _) => Activate(bookmark);
        if (!bookmark.IsFolder) LibraryUi.OpenOnMiddleClick(row, model, () => bookmark.Url);
        if (bookmark.IsFolder) AcceptBookmarkDrops(row, bookmark.Id);
        var menu = LibraryUi.CoveringMenu("library.bookmarksPage");
        menu.Opening += (_, _) => FillRowMenu(menu, bookmark, row);
        row.ContextFlyout = menu;
        return row;
    }

    private void Activate(Bookmark bookmark)
    {
        if (bookmark.IsFolder) Show(bookmark.Id);
        else if (bookmark.Url is { } url) LibraryUi.Open(model, url);
    }

    private void FillRowMenu(MenuFlyout menu, Bookmark bookmark, FrameworkElement row)
    {
        menu.Items.Clear();
        var selected = SelectedIds();
        if (selected.Count > 1 && selected.Contains(bookmark.Id))
        {
            var chosen = selected.Select(library.BookmarkById).OfType<Bookmark>().ToList();
            var urls = chosen.Where(b => b.Url is not null).ToList();
            if (urls.Count > 0) menu.Items.Add(LibraryUi.MenuItem($"Open {urls.Count} in New Tabs", () => OpenAll(urls), "plus.square.on.square"));
            menu.Items.Add(LibraryUi.MenuItem($"Delete {chosen.Count} Items", () => _ = DeleteAsync(selected), "trash"));
            return;
        }
        if (bookmark.Url is { } url)
        {
            menu.Items.Add(LibraryUi.MenuItem("Open", () => model.OpenFromLibrary(url)));
            menu.Items.Add(LibraryUi.MenuItem("Open in New Tab", () => model.OpenFromLibrary(url, newTab: true), "plus.square.on.square"));
            menu.Items.Add(LibraryUi.MenuItem("Copy Address", () => LibraryUi.Copy(url), "doc.on.doc"));
        }
        else
        {
            menu.Items.Add(LibraryUi.MenuItem("Open Folder", () => Show(bookmark.Id), "folder"));
            menu.Items.Add(LibraryUi.MenuItem("Open All in New Tabs", () => OpenAll(library.Children(bookmark.Id)), "plus.square.on.square"));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(LibraryUi.MenuItem(bookmark.IsFolder ? "Rename…" : "Edit…", () => BookmarkEditor.Show(model, bookmark.Id, null, row), "edit"));
        var move = new MenuFlyoutSubItem { Text = "Move To", Icon = LibraryUi.Icon("folder") };
        foreach (var (folder, depth) in library.FolderTree())
        {
            if (folder.Id == bookmark.Id || folder.Id == bookmark.ParentId) continue;
            move.Items.Add(LibraryUi.MenuItem(new string(' ', depth * 3) + folder.DisplayTitle, () => _ = library.MoveBookmarkAsync(bookmark.Id, folder.Id)));
        }
        menu.Items.Add(move);
        menu.Items.Add(LibraryUi.MenuItem("Delete", () => _ = DeleteAsync([bookmark.Id]), "trash"));
    }

    private List<string> SelectedIds() =>
        [.. list.SelectedItems.OfType<FrameworkElement>().Select(row => row.Tag as string).OfType<string>()];

    private void UpdateSelectionBar()
    {
        var count = list.SelectedItems.Count;
        selectionText.Text = count > 1 ? $"{count} selected" : "";
        deleteSelected.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenAll(IEnumerable<Bookmark> bookmarks)
    {
        foreach (var bookmark in bookmarks)
        {
            if (bookmark.Url is { } url) model.OpenFromLibrary(url, newTab: true, select: false);
        }
    }

    private async Task DeleteAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        var folders = ids.Select(library.BookmarkById).Count(b => b?.IsFolder == true);
        if (folders > 0)
        {
            var title = ids.Count == 1 ? $"Delete “{library.BookmarkById(ids[0])?.DisplayTitle}”?" : $"Delete {ids.Count} items?";
            if (!await LibraryUi.ConfirmAsync(this, title, "Folders are deleted with everything inside them.", "Delete")) return;
        }
        await library.DeleteBookmarksAsync(ids);
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        switch (args.Key)
        {
            case VirtualKey.Enter when list.SelectedItems.Count == 1 && list.SelectedItem is FrameworkElement { Tag: string id }:
                if (library.BookmarkById(id) is { } bookmark) Activate(bookmark);
                args.Handled = true;
                break;
            case VirtualKey.Delete:
                _ = DeleteAsync(SelectedIds());
                args.Handled = true;
                break;
            case VirtualKey.Back when tag is null && query.Length == 0 && library.BookmarkById(folderId)?.ParentId is { } parent:
                Show(parent);
                args.Handled = true;
                break;
        }
    }

    /// <summary>A row was dragged to a new place in the folder: store the new order.</summary>
    private void OnReordered(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != DataPackageOperation.Move || !list.CanReorderItems) return;
        foreach (var row in args.Items.OfType<FrameworkElement>())
        {
            if (row.Tag is not string id) continue;
            var index = list.Items.IndexOf(row);
            if (index >= 0 && library.BookmarkById(id)?.ParentId == folderId) _ = library.MoveBookmarkAsync(id, folderId, index);
        }
    }
}
