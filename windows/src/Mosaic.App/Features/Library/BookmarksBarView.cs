using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Mosaic.App.Features.Library;

/// <summary>
/// The bookmarks bar (layout component "bookmarksBar"): the bar folder's bookmarks and folders
/// as buttons, folders as menus, a » menu for what does not fit. Port of <c>BookmarksBarView.swift</c>.
/// Ctrl+click or middle-click opens in a background tab; bookmarks can be dragged to reorder,
/// onto a folder to file them, and links dropped on the bar are bookmarked there.
/// Draws nothing while <see cref="BrowserLibrary.ShowsBookmarksBar"/> is off.
/// </summary>
internal sealed class BookmarksBarView : Grid
{
    private const string DragPrefix = "mosaic-bookmark:";
    private const string CoverId = "library.bookmarksBar";
    private const double Spacing = 2;

    private readonly BrowserModel model;
    private readonly BrowserLibrary library;
    private readonly StackPanel items = new() { Orientation = Orientation.Horizontal, Spacing = Spacing };
    private readonly Button overflow;
    private readonly List<(Bookmark Bookmark, Button Button)> buttons = [];

    public BookmarksBarView(BrowserModel model)
    {
        this.model = model;
        library = model.Library;
        Height = 34;
        Padding = new Thickness(8, 1, 8, 3);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(this, "Bookmarks bar");
        AutomationProperties.SetLandmarkType(this, AutomationLandmarkType.Navigation);

        var clip = new Grid();
        clip.Children.Add(items);
        clip.SizeChanged += (_, args) =>
        {
            clip.Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
            LayOut();
        };
        Children.Add(clip);

        overflow = LibraryUi.IconButton("chevron.right.2", "More bookmarks", (_, _) => ShowOverflow(), 12);
        overflow.Visibility = Visibility.Collapsed;
        SetColumn(overflow, 1);
        Children.Add(overflow);

        ContextFlyout = BarMenu();
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;

        LibraryUi.Observe(this, library, [nameof(BrowserLibrary.Bookmarks), nameof(BrowserLibrary.ShowsBookmarksBar)], Rebuild);
        Loaded += (_, _) =>
        {
            // The bar needs the database; open it once the window has been drawn, not before.
            DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, library.EnsureLoaded);
        };
    }

    private void Rebuild()
    {
        Visibility = library.ShowsBookmarksBar ? Visibility.Visible : Visibility.Collapsed;
        items.Children.Clear();
        buttons.Clear();
        var bar = library.BookmarksBar;
        if (bar.Count == 0)
        {
            overflow.Visibility = Visibility.Collapsed;
            if (!library.IsLoaded) return;
            items.Children.Add(new TextBlock
            {
                Text = "For quick access, add bookmarks here with Ctrl+D.",
                Foreground = LibraryUi.SecondaryText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0),
            });
            var import = new HyperlinkButton { Content = "Import bookmarks…", Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
            import.Click += (_, _) => _ = LibraryUi.ImportAsync(model, this);
            items.Children.Add(import);
            return;
        }
        foreach (var bookmark in bar)
        {
            var button = MakeButton(bookmark);
            buttons.Add((bookmark, button));
            items.Children.Add(button);
        }
        LayOut();
    }

    private Button MakeButton(Bookmark bookmark)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(bookmark.IsFolder ? LibraryUi.Icon("folder", 14) : LibraryUi.Monogram(bookmark.Url ?? "", 16));
        content.Children.Add(new TextBlock
        {
            Text = bookmark.DisplayTitle,
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        });
        if (bookmark.IsFolder) content.Children.Add(LibraryUi.Icon("chevron.down", 9));
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(8, 3, 8, 3),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            CanDrag = true,
            Tag = bookmark.Id,
        };
        AutomationProperties.SetName(button, bookmark.IsFolder ? $"{bookmark.DisplayTitle} folder" : bookmark.DisplayTitle);
        ToolTipService.SetToolTip(button, bookmark.IsFolder ? bookmark.DisplayTitle : $"{bookmark.DisplayTitle}\n{bookmark.Url}");
        button.Click += (_, _) =>
        {
            if (bookmark.IsFolder) FolderMenu(bookmark.Id).ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
            else if (bookmark.Url is { } url) LibraryUi.Open(model, url);
        };
        if (!bookmark.IsFolder) LibraryUi.OpenOnMiddleClick(button, model, () => bookmark.Url);
        button.ContextFlyout = ItemMenu(bookmark, button);
        button.DragStarting += (_, args) =>
        {
            args.Data.SetText(DragPrefix + bookmark.Id);
            args.Data.RequestedOperation = DataPackageOperation.Move;
        };
        if (bookmark.IsFolder)
        {
            // Dropping a bookmark on a folder files it there.
            button.AllowDrop = true;
            button.DragOver += OnDragOver;
            button.Drop += async (_, args) =>
            {
                args.Handled = true;
                var deferral = args.GetDeferral();
                try
                {
                    if (await DraggedAsync(args.DataView) is { } dropped) await DropAsync(dropped, bookmark.Id, null);
                }
                finally
                {
                    deferral.Complete();
                }
            };
        }
        return button;
    }

    /// <summary>Hides the buttons that do not fit and offers them in the » menu.</summary>
    private void LayOut()
    {
        if (buttons.Count == 0) return;
        var available = Math.Max(0, ActualWidth - Padding.Left - Padding.Right - 36);
        var used = 0.0;
        var hidden = false;
        foreach (var (_, button) in buttons)
        {
            button.Visibility = Visibility.Visible;
            button.Measure(new Size(double.PositiveInfinity, Height));
            used += button.DesiredSize.Width + Spacing;
            if (hidden || used > available)
            {
                hidden = true;
                button.Visibility = Visibility.Collapsed;
            }
        }
        overflow.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOverflow()
    {
        var menu = LibraryUi.CoveringMenu(CoverId);
        foreach (var (bookmark, button) in buttons)
        {
            if (button.Visibility == Visibility.Collapsed) menu.Items.Add(MenuEntry(bookmark));
        }
        menu.ShowAt(overflow, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    /// <summary>A folder's contents as a menu (subfolders as submenus, built when opened).</summary>
    private MenuFlyout FolderMenu(string folderId)
    {
        var menu = LibraryUi.CoveringMenu(CoverId);
        FillFolder(menu.Items, folderId);
        return menu;
    }

    private void FillFolder(IList<MenuFlyoutItemBase> target, string folderId)
    {
        var children = library.Children(folderId);
        if (children.Count == 0) target.Add(new MenuFlyoutItem { Text = "Empty", IsEnabled = false });
        foreach (var child in children) target.Add(MenuEntry(child));
        var urls = children.Where(child => !child.IsFolder && child.Url is not null).Select(child => child.Url!).ToList();
        if (urls.Count > 1)
        {
            target.Add(new MenuFlyoutSeparator());
            target.Add(LibraryUi.MenuItem($"Open All ({urls.Count}) in New Tabs", () =>
            {
                foreach (var url in urls) model.OpenFromLibrary(url, newTab: true, select: false);
            }, "plus.square.on.square"));
        }
    }

    private MenuFlyoutItemBase MenuEntry(Bookmark bookmark)
    {
        if (bookmark.IsFolder)
        {
            var sub = new MenuFlyoutSubItem { Text = bookmark.DisplayTitle, Icon = LibraryUi.Icon("folder") };
            FillFolder(sub.Items, bookmark.Id);
            return sub;
        }
        var title = bookmark.DisplayTitle.Length > 60 ? bookmark.DisplayTitle[..59] + "…" : bookmark.DisplayTitle;
        var item = LibraryUi.MenuItem(title, () => LibraryUi.Open(model, bookmark.Url ?? ""));
        ToolTipService.SetToolTip(item, bookmark.Url);
        return item;
    }

    private MenuFlyout ItemMenu(Bookmark bookmark, FrameworkElement anchor)
    {
        var menu = LibraryUi.CoveringMenu(CoverId);
        if (bookmark.Url is { } url)
        {
            menu.Items.Add(LibraryUi.MenuItem("Open", () => model.OpenFromLibrary(url)));
            menu.Items.Add(LibraryUi.MenuItem("Open in New Tab", () => model.OpenFromLibrary(url, newTab: true), "plus.square.on.square"));
            menu.Items.Add(LibraryUi.MenuItem("Copy Address", () => LibraryUi.Copy(url), "doc.on.doc"));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        else
        {
            menu.Items.Add(LibraryUi.MenuItem("Open All in New Tabs", () =>
            {
                foreach (var child in library.Children(bookmark.Id).Where(c => c.Url is not null)) model.OpenFromLibrary(child.Url!, newTab: true, select: false);
            }, "plus.square.on.square"));
        }
        menu.Items.Add(LibraryUi.MenuItem(bookmark.IsFolder ? "Rename…" : "Edit…", () => BookmarkEditor.Show(model, bookmark.Id, null, anchor), "pencil"));
        menu.Items.Add(LibraryUi.MenuItem("Delete", () => _ = library.DeleteBookmarksAsync([bookmark.Id]), "trash"));
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var item in BarMenuItems()) menu.Items.Add(item);
        return menu;
    }

    private MenuFlyout BarMenu()
    {
        var menu = LibraryUi.CoveringMenu(CoverId);
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var item in BarMenuItems()) menu.Items.Add(item);
        };
        return menu;
    }

    private IEnumerable<MenuFlyoutItemBase> BarMenuItems()
    {
        if (model.SelectedTab is { } tab && LibraryUrl.IsRecordable(tab.Url) && !library.IsBookmarked(tab.Url))
        {
            yield return LibraryUi.MenuItem("Add This Page to the Bar", () => _ = library.AddBookmarkAsync(tab.Url, tab.DisplayTitle, Bookmark.BarId), "star");
        }
        if (model.SelectedTab is { } page && LibraryUrl.IsRecordable(page.Url) && !library.IsInReadingList(page.Url))
        {
            yield return LibraryUi.MenuItem("Add This Page to Reading List", () => _ = library.AddCurrentPageToReadingListAsync(), "eyeglasses");
        }
        yield return LibraryUi.MenuItem("New Folder…", () => _ = BookmarkEditor.NewFolderAsync(model, this, Bookmark.BarId), "folder.badge.plus");
        yield return LibraryUi.MenuItem("Show All Bookmarks", () => model.OpenLibraryPage(LibraryPage.Bookmarks), "book");
        yield return LibraryUi.MenuItem("Hide Bookmarks Bar", () => library.ShowsBookmarksBar = false);
    }

    // ---- Drag and drop ----

    private sealed record Dropped(string? BookmarkId, string? Url, string Title);

    private static void OnDragOver(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(StandardDataFormats.Text) || args.DataView.Contains(StandardDataFormats.WebLink))
        {
            args.AcceptedOperation = DataPackageOperation.Move | DataPackageOperation.Copy | DataPackageOperation.Link;
        }
    }

    private static async Task<Dropped?> DraggedAsync(DataPackageView data)
    {
        if (data.Contains(StandardDataFormats.WebLink))
        {
            var link = await data.GetWebLinkAsync();
            return new Dropped(null, link.AbsoluteUri, "");
        }
        if (!data.Contains(StandardDataFormats.Text)) return null;
        var text = (await data.GetTextAsync()).Trim();
        if (text.StartsWith(DragPrefix, StringComparison.Ordinal)) return new Dropped(text[DragPrefix.Length..], null, "");
        return LibraryUrl.IsRecordable(text) ? new Dropped(null, text, "") : null;
    }

    private async void OnDrop(object sender, DragEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (await DraggedAsync(args.DataView) is not { } dropped) return;
            // The slot under the pointer: before the first button whose middle is to its right.
            var x = args.GetPosition(items).X;
            var index = 0;
            foreach (var (_, button) in buttons)
            {
                if (button.Visibility == Visibility.Collapsed) break;
                var left = button.TransformToVisual(items).TransformPoint(new Point(0, 0)).X;
                if (left + button.ActualWidth / 2 > x) break;
                index++;
            }
            await DropAsync(dropped, Bookmark.BarId, index);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task DropAsync(Dropped dropped, string folderId, int? index)
    {
        if (dropped.BookmarkId is { } id)
        {
            if (id == folderId) return;
            var current = library.BookmarkById(id);
            // Positions count the other bookmarks only: moving right past itself is one fewer.
            if (index is { } slot && current?.ParentId == folderId && current.Position < slot) index = slot - 1;
            await library.MoveBookmarkAsync(id, folderId, index);
        }
        else if (dropped.Url is { } url)
        {
            var title = model.Tabs.FirstOrDefault(tab => tab.Url == url)?.DisplayTitle ?? "";
            if (await library.AddBookmarkAsync(url, title, folderId) is { } added && index is { } slot)
            {
                await library.MoveBookmarkAsync(added.Id, folderId, slot);
            }
        }
    }
}
