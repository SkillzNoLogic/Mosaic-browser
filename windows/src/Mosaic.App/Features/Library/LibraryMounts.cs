using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;
using Mosaic.Core.Shell;
using Windows.System;

namespace Mosaic.App.Features.Library;

/// <summary>
/// Plugs the library into the app (owner: library): Settings › Search, the internal pages
/// <c>mosaic://bookmarks</c>, <c>mosaic://history</c>, <c>mosaic://reading-list</c>, the layout
/// components "bookmarksBar", "bookmarkStar", "bookmarksPage", "historyPage", "readingListPage",
/// the keyboard shortcuts, the bookmark editor and the automation commands.
/// Registration only stores delegates; nothing here touches the database at start-up.
/// </summary>
internal static class LibraryMounts
{
    /// <summary>
    /// Opens Settings at the Search page. The shell owns Settings; until it offers a way to open
    /// a category, this stays null and "libraryOpen search-settings" does nothing visible.
    /// </summary>
    public static Action<BrowserModel>? OpenSearchSettings { get; set; }

    [ModuleInitializer]
    internal static void Register()
    {
        FeatureMounts.SettingsPages[SettingsCategory.Search] = model => new SearchSettingsPage(model);
        FeatureMounts.InternalPages[LibraryPage.Bookmarks.Host()] = model => new BookmarksPage(model);
        FeatureMounts.InternalPages[LibraryPage.History.Host()] = model => new HistoryPage(model);
        FeatureMounts.InternalPages[LibraryPage.ReadingList.Host()] = model => new ReadingListPage(model);
        FeatureMounts.Components["bookmarksBar"] = model => new BookmarksBarView(model);
        FeatureMounts.Components["bookmarkStar"] = model => new BookmarkStar(model);
        FeatureMounts.Components["bookmarksPage"] = model => PageButton(model, LibraryPage.Bookmarks, "book", "Bookmarks (Ctrl+Shift+O)");
        FeatureMounts.Components["historyPage"] = model => PageButton(model, LibraryPage.History, "clock", "History (Ctrl+H)");
        FeatureMounts.Components["readingListPage"] = model => PageButton(model, LibraryPage.ReadingList, "eyeglasses", "Reading List");
        FeatureMounts.Startup.Add(("library", Start));
        LibraryAutomation.Register();
    }

    private static void Start(BrowserModel model, Window window)
    {
        LibraryUi.MainWindow = window;
        var library = model.Library;  // no I/O: the database opens when something needs it
        library.BookmarkEditorRequested += (id, isNew) =>
            BookmarkEditor.Show(model, id, isNew ? "Bookmark Added" : "Edit Bookmark", BookmarkStar.Visible);
        library.SearchSettingsRequested += () => OpenSearchSettings?.Invoke(model);
        if (window.Content is UIElement content) AddShortcuts(content, library);
        else window.Activated += WhenActivated;

        void WhenActivated(object sender, WindowActivatedEventArgs args)
        {
            if (window.Content is not UIElement root) return;
            window.Activated -= WhenActivated;
            AddShortcuts(root, library);
        }
    }

    /// <summary>Ctrl+D, Ctrl+Shift+O, Ctrl+H, Ctrl+Shift+B while Mosaic's own controls have focus.</summary>
    private static void AddShortcuts(UIElement root, BrowserLibrary library)
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, string command)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) => args.Handled = library.HandleCommand(command);
            root.KeyboardAccelerators.Add(accelerator);
        }
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Add(VirtualKey.D, VirtualKeyModifiers.Control, "bookmarkPage");
        Add(VirtualKey.O, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, "showBookmarks");
        Add(VirtualKey.H, VirtualKeyModifiers.Control, "showHistory");
        Add(VirtualKey.B, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, "toggleBookmarksBar");
    }

    private static Button PageButton(BrowserModel model, LibraryPage page, string icon, string name) =>
        LibraryUi.IconButton(icon, name, (_, _) => model.OpenLibraryPage(page));
}

/// <summary>
/// The bookmark star (layout component "bookmarkStar"): filled when the page in front is
/// bookmarked; click to bookmark it (and edit), right-click for the reading list.
/// </summary>
internal sealed class BookmarkStar : Button
{
    private static WeakReference<BookmarkStar>? visible;

    private readonly BrowserModel model;

    /// <summary>The star currently on screen, if any (the bookmark editor opens under it).</summary>
    public static BookmarkStar? Visible =>
        visible is not null && visible.TryGetTarget(out var star) && star.XamlRoot is not null && star.Visibility == Visibility.Visible ? star : null;

    public BookmarkStar(BrowserModel model)
    {
        this.model = model;
        Padding = new Thickness(8, 6, 8, 6);
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BorderThickness = new Thickness(0);
        VerticalAlignment = VerticalAlignment.Center;
        Click += async (_, _) => await model.Library.BookmarkCurrentPageAsync();
        var menu = LibraryUi.CoveringMenu("library.star");
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add(LibraryUi.MenuItem("Add to Reading List", () => _ = model.Library.AddCurrentPageToReadingListAsync(), "eyeglasses"));
            menu.Items.Add(LibraryUi.MenuItem("Show All Bookmarks", () => model.OpenLibraryPage(LibraryPage.Bookmarks), "book"));
            menu.Items.Add(LibraryUi.MenuItem(model.Library.ShowsBookmarksBar ? "Hide Bookmarks Bar" : "Show Bookmarks Bar",
                () => model.Library.ShowsBookmarksBar = !model.Library.ShowsBookmarksBar));
        };
        ContextFlyout = menu;
        LibraryUi.Observe(this, model.Library, nameof(BrowserLibrary.Bookmarks), Update);
        LibraryUi.Observe(this, model, [nameof(BrowserModel.SelectedTab), nameof(BrowserModel.AddressText), nameof(BrowserModel.CanReload)], Update);
        Loaded += (_, _) => visible = new WeakReference<BookmarkStar>(this);
        Update();
    }

    private void Update()
    {
        var url = model.SelectedTab?.Url ?? "";
        var bookmarked = model.Library.IsBookmarked(url);
        var icon = LibraryUi.Icon(bookmarked ? "star.fill" : "star", 15);
        if (bookmarked) icon.Foreground = LibraryUi.Accent;
        Content = icon;
        IsEnabled = LibraryUrl.IsRecordable(url);
        var name = bookmarked ? "Edit bookmark (Ctrl+D)" : "Bookmark this page (Ctrl+D)";
        AutomationProperties.SetName(this, name);
        ToolTipService.SetToolTip(this, name);
    }
}
