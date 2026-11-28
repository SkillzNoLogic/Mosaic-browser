using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;
using Windows.System;

namespace Mosaic.App.Features.Library;

/// <summary>
/// Edits a bookmark's name, address, folder and tags (or a folder's name and place), in a
/// flyout. Port of <c>BookmarkEditor.swift</c>. While it is open Gecko's page is hidden
/// (<see cref="FeatureMounts.CoverPage"/>), since a Win32 child window would cover it.
/// </summary>
internal static class BookmarkEditor
{
    private const string CoverId = "library.bookmarkEditor";

    /// <summary>
    /// Shows the editor for <paramref name="bookmarkId"/>, anchored to <paramref name="anchor"/>
    /// (or to the top-right corner of the window when null).
    /// </summary>
    public static void Show(BrowserModel model, string bookmarkId, string? heading, FrameworkElement? anchor)
    {
        var library = model.Library;
        if (library.BookmarkById(bookmarkId) is not { } bookmark) return;
        var target = anchor ?? LibraryUi.MainWindow?.Content as FrameworkElement;
        if (target?.XamlRoot is null) return;

        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        LibraryUi.CoverWhileOpen(flyout, CoverId);
        flyout.Content = Build(model, bookmark, heading, () => flyout.Hide());
        if (anchor is not null)
        {
            flyout.ShowAt(anchor);
        }
        else
        {
            // The star's place in most Mosaics: the top-right corner, under the toolbar.
            flyout.ShowAt(target, new FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(Math.Max(0, target.ActualWidth - 24), 48),
                Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            });
        }
    }

    private static UIElement Build(BrowserModel model, Bookmark bookmark, string? heading, Action close)
    {
        var library = model.Library;
        var panel = new StackPanel { Spacing = 12, Width = 340, Padding = new Thickness(4) };
        var title = LibraryUi.Text(heading ?? (bookmark.IsFolder ? "Edit Folder" : "Edit Bookmark"), "SubtitleTextBlockStyle");
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        panel.Children.Add(title);

        var name = new TextBox { Header = "Name", Text = bookmark.Title };
        panel.Children.Add(name);
        TextBox? address = null;
        TextBox? tags = null;
        if (!bookmark.IsFolder)
        {
            address = new TextBox { Header = "Address", Text = bookmark.Url ?? "", IsSpellCheckEnabled = false };
            panel.Children.Add(address);
        }

        var folders = new ComboBox { Header = "Folder", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (folder, depth) in library.FolderTree())
        {
            // A folder cannot go inside itself or its own subfolders.
            if (bookmark.IsFolder && IsWithin(library, folder.Id, bookmark.Id)) continue;
            var item = new ComboBoxItem { Content = new string('\u2002', depth * 3) + folder.DisplayTitle, Tag = folder.Id };
            folders.Items.Add(item);
            if (folder.Id == bookmark.ParentId) folders.SelectedItem = item;
        }
        panel.Children.Add(folders);

        if (!bookmark.IsFolder)
        {
            tags = new TextBox { Header = "Tags", Text = string.Join(", ", bookmark.Tags), PlaceholderText = "Separate with commas" };
            panel.Children.Add(tags);
        }

        var problem = LibraryUi.Wrapping("", secondary: false);
        problem.Foreground = LibraryUi.Brush("SystemFillColorCriticalBrush", 196, 43, 28);
        problem.Visibility = Visibility.Collapsed;
        panel.Children.Add(problem);

        async void Save()
        {
            var url = address?.Text.Trim();
            if (url is not null && (url.Length == 0 || LibraryUrl.Scheme(url) is null))
            {
                problem.Text = "Enter a complete address, such as https://example.com/.";
                problem.Visibility = Visibility.Visible;
                return;
            }
            var tagList = tags?.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var folderId = (folders.SelectedItem as ComboBoxItem)?.Tag as string;
            close();
            await library.UpdateBookmarkAsync(bookmark.Id, name.Text, url, tagList, folderId);
        }

        async void Remove()
        {
            close();
            await library.DeleteBookmarksAsync([bookmark.Id]);
            if (!bookmark.IsFolder) model.Notice = "Bookmark removed";
        }

        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var remove = new Button { Content = bookmark.IsFolder ? "Delete Folder" : "Remove" };
        remove.Click += (_, _) => Remove();
        buttons.Children.Add(remove);
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => close();
        Grid.SetColumn(cancel, 1);
        buttons.Children.Add(cancel);
        var save = new Button { Content = "Save" };
        if (LibraryUi.Style("AccentButtonStyle") is { } accent) save.Style = accent;
        save.Click += (_, _) => Save();
        Grid.SetColumn(save, 2);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        // Enter saves from any field; Escape closes the flyout by itself.
        panel.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter) return;
            args.Handled = true;
            Save();
        };
        name.Loaded += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            name.SelectAll();
        };
        return panel;
    }

    private static bool IsWithin(BrowserLibrary library, string folderId, string ancestorId)
    {
        string? cursor = folderId;
        while (cursor is not null)
        {
            if (cursor == ancestorId) return true;
            cursor = library.BookmarkById(cursor)?.ParentId;
        }
        return false;
    }

    /// <summary>Asks for a new folder's name (and where it goes); creates it. Returns the folder.</summary>
    public static async Task<Bookmark?> NewFolderAsync(BrowserModel model, UIElement anchor, string parentId)
    {
        var library = model.Library;
        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        var name = new TextBox { Header = "Name", PlaceholderText = "Folder name" };
        panel.Children.Add(name);
        var parents = new ComboBox { Header = "In", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (folder, depth) in library.FolderTree())
        {
            var item = new ComboBoxItem { Content = new string('\u2002', depth * 3) + folder.DisplayTitle, Tag = folder.Id };
            parents.Items.Add(item);
            if (folder.Id == parentId) parents.SelectedItem = item;
        }
        panel.Children.Add(parents);
        var dialog = new ContentDialog
        {
            Title = "New Folder",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
        if (await LibraryUi.ShowDialogAsync(dialog, anchor) != ContentDialogResult.Primary) return null;
        var parent = (parents.SelectedItem as ComboBoxItem)?.Tag as string ?? parentId;
        return await library.AddFolderAsync(name.Text.Trim(), parent);
    }
}
