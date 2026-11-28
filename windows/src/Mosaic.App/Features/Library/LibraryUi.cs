using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Mosaic.App.Features.Library;

/// <summary>Shared pieces of the library's views (bar, pages, editor, settings). Owner: library.</summary>
internal static class LibraryUi
{
    /// <summary>Mosaic's window, for file pickers (set by the library's start-up hook).</summary>
    public static Window? MainWindow { get; set; }

    /// <summary>
    /// Icons the library uses that the system area's table may not have yet. Each is added only
    /// when <see cref="Icons"/> does not already know the name (the system area's glyph wins).
    /// </summary>
    private static readonly (string Name, string Glyph)[] FallbackGlyphs =
    [
        ("folder", ""), ("folder.badge.plus", ""), ("clock", ""), ("book", ""),
        ("eyeglasses", ""), ("tag", ""), ("trash", ""), ("plus.square.on.square", ""),
        ("doc.on.doc", ""), ("pencil", ""), ("checkmark", ""), ("checkmark.circle", ""),
        ("circle", ""), ("square.and.arrow.down", ""), ("square.and.arrow.up", ""),
        ("globe", ""), ("chevron.down", ""), ("keyboard", ""), ("chevron.right.2", ""),
    ];

    private static bool iconsAdded;

    public static void AddFallbackIcons()
    {
        if (iconsAdded) return;
        iconsAdded = true;
        var unknown = Icons.Glyph("library.no-such-icon");
        foreach (var (name, glyph) in FallbackGlyphs)
        {
            if (Icons.Glyph(name) == unknown) Icons.Add(name, glyph);
        }
    }

    public static FontIcon Icon(string name, double size = 16)
    {
        AddFallbackIcons();
        return Icons.Make(name, size);
    }

    // ---- Theme ----

    public static Brush Brush(string key, byte r = 128, byte g = 128, byte b = 128) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));

    public static Style? Style(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    public static Brush SecondaryText => Brush("TextFillColorSecondaryBrush", 110, 110, 110);
    public static Brush PageBackground => Brush("SolidBackgroundFillColorBaseBrush", 243, 243, 243);
    public static Brush CardBackground => Brush("CardBackgroundFillColorDefaultBrush", 251, 251, 251);
    public static Brush CardStroke => Brush("CardStrokeColorDefaultBrush", 229, 229, 229);
    public static Brush Accent => Brush("AccentTextFillColorPrimaryBrush", 0, 95, 184);

    public static TextBlock Text(string text, string? style = null, bool secondary = false, double? size = null)
    {
        var block = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (style is not null && Style(style) is { } found) block.Style = found;
        if (secondary) block.Foreground = SecondaryText;
        if (size is { } fontSize) block.FontSize = fontSize;
        return block;
    }

    public static TextBlock Wrapping(string text, bool secondary = true)
    {
        var block = Text(text, secondary: secondary);
        block.TextWrapping = TextWrapping.WrapWholeWords;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    /// <summary>A button with an icon and an accessible name (and tooltip).</summary>
    public static Button IconButton(string icon, string name, RoutedEventHandler click, double size = 14)
    {
        var button = new Button
        {
            Content = Icon(icon, size),
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += click;
        return button;
    }

    /// <summary>A button with an icon and a label.</summary>
    public static Button LabelButton(string icon, string label, RoutedEventHandler click, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(Icon(icon, 14));
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, VerticalAlignment = VerticalAlignment.Center };
        if (accent && Style("AccentButtonStyle") is { } style) button.Style = style;
        AutomationProperties.SetName(button, label);
        button.Click += click;
        return button;
    }

    public static MenuFlyoutItem MenuItem(string text, Action action, string? icon = null, KeyboardAccelerator? accelerator = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (icon is not null) item.Icon = Icon(icon);
        if (accelerator is not null) item.KeyboardAccelerators.Add(accelerator);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A menu that hides Gecko's page (a Win32 child window) while it is open, so it is not drawn under it.</summary>
    public static MenuFlyout CoveringMenu(string coverId)
    {
        var menu = new MenuFlyout();
        CoverWhileOpen(menu, coverId);
        return menu;
    }

    public static void CoverWhileOpen(FlyoutBase flyout, string coverId)
    {
        flyout.Opening += (_, _) => FeatureMounts.CoverPage(coverId, true);
        flyout.Closed += (_, _) => FeatureMounts.CoverPage(coverId, false);
    }

    /// <summary>Shows a dialog over the window, hiding Gecko while it is up.</summary>
    public static async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, UIElement anchor)
    {
        dialog.XamlRoot = anchor.XamlRoot;
        if (Style("DefaultContentDialogStyle") is { } style) dialog.Style = style;
        FeatureMounts.CoverPage("library.dialog", true);
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            FeatureMounts.CoverPage("library.dialog", false);
        }
    }

    public static async Task<bool> ConfirmAsync(UIElement anchor, string title, string message, string confirm)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = Wrapping(message, secondary: false),
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowDialogAsync(dialog, anchor) == ContentDialogResult.Primary;
    }

    // ---- Opening pages ----

    /// <summary>Ctrl (or a middle click) opens in a new background tab; Ctrl+Shift brings it forward.</summary>
    public static void Open(BrowserModel model, string url, bool newTab = false)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        if (newTab) model.OpenFromLibrary(url, newTab: true);
        else if (ctrl) model.OpenFromLibrary(url, newTab: true, select: shift);
        else model.OpenFromLibrary(url);
    }

    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Middle-clicking an element opens <paramref name="url"/> in a background tab.</summary>
    public static void OpenOnMiddleClick(UIElement element, BrowserModel model, Func<string?> url)
    {
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((sender, args) =>
        {
            if (args.GetCurrentPoint(element).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) return;
            if (url() is { } address) model.OpenFromLibrary(address, newTab: true, select: false);
            args.Handled = true;
        }), handledEventsToo: true);
    }

    public static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // ---- Small views ----

    /// <summary>
    /// A small letter tile standing in for a site icon (the library does not fetch favicons),
    /// coloured consistently per site.
    /// </summary>
    public static UIElement Monogram(string url, double size = 24)
    {
        var name = SiteName(url);
        var letter = name.Length > 0 ? name[..1].ToUpper(CultureInfo.CurrentCulture) : "•";
        var tile = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.28),
            Background = new SolidColorBrush(ColorFor(name)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letter,
                FontSize = size * 0.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        AutomationProperties.SetAccessibilityView(tile, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return tile;
    }

    /// <summary>The name part of the host: "wikipedia" for en.wikipedia.org, "bbc" for bbc.co.uk.</summary>
    public static string SiteName(string url)
    {
        var host = LibraryUrl.Host(url);
        var labels = host.Split('.');
        if (labels.Length < 2 || labels.All(label => label.All(char.IsAsciiDigit))) return host;
        string[] generic = ["co", "com", "org", "net", "ac", "gov", "edu"];
        if (labels.Length >= 3 && generic.Contains(labels[^2])) return labels[^3];
        return labels[^2];
    }

    private static readonly Windows.UI.Color[] Palette =
    [
        ColorHelper.FromArgb(255, 92, 133, 242), ColorHelper.FromArgb(255, 237, 115, 69),
        ColorHelper.FromArgb(255, 74, 176, 130), ColorHelper.FromArgb(255, 158, 107, 219),
        ColorHelper.FromArgb(255, 219, 92, 133), ColorHelper.FromArgb(255, 51, 161, 191),
        ColorHelper.FromArgb(255, 199, 148, 51), ColorHelper.FromArgb(255, 120, 135, 153),
    ];

    private static Windows.UI.Color ColorFor(string host)
    {
        // A stable hash (string.GetHashCode changes every run); the same as macOS's.
        uint value = 5381;
        foreach (var rune in host.EnumerateRunes()) value = unchecked(value * 33 + (uint)rune.Value);
        return Palette[value % (uint)Palette.Length];
    }

    public static UIElement TagChip(string tag) => new Border
    {
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(7, 1, 7, 2),
        Background = Brush("SubtleFillColorSecondaryBrush", 236, 236, 236),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = tag, FontSize = 11, Foreground = Accent },
    };

    /// <summary>The title row of a library page.</summary>
    public static Grid PageHeader(string title, TextBlock subtitle, params UIElement[] trailing)
    {
        var grid = new Grid { Padding = new Thickness(28, 22, 28, 14), ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        var heading = Text(title, "TitleTextBlockStyle");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        titles.Children.Add(heading);
        subtitle.Foreground = SecondaryText;
        titles.Children.Add(subtitle);
        grid.Children.Add(titles);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var element in trailing) actions.Children.Add(element);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }

    public static TextBox SearchBox(string placeholder, Action<string> changed, double width = 260)
    {
        var box = new TextBox { PlaceholderText = placeholder, Width = width, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, placeholder);
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(220);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => changed(box.Text);
        box.TextChanged += (_, _) =>
        {
            timer.Stop();
            timer.Start();
        };
        return box;
    }

    /// <summary>A centred message for an empty list.</summary>
    public static UIElement EmptyState(string icon, string title, string message, UIElement? action = null)
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 420,
            Padding = new Thickness(24, 60, 24, 24),
        };
        var symbol = Icon(icon, 36);
        symbol.Foreground = SecondaryText;
        symbol.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(symbol);
        var heading = Text(title, "SubtitleTextBlockStyle");
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(heading);
        var body = Wrapping(message);
        body.TextAlignment = TextAlignment.Center;
        panel.Children.Add(body);
        if (action is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
            element.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(element);
        }
        return panel;
    }

    /// <summary>The banner shown on library pages when the database could not be opened.</summary>
    public static InfoBar StorageWarning(BrowserLibrary library)
    {
        var bar = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            Margin = new Thickness(28, 0, 28, 8),
        };
        void Update()
        {
            bar.Message = library.StorageProblem ?? "";
            bar.IsOpen = library.StorageProblem is not null;
        }
        Update();
        Observe(bar, library, nameof(BrowserLibrary.StorageProblem), Update);
        return bar;
    }

    /// <summary>
    /// Calls <paramref name="changed"/> when one of the library's properties changes, while
    /// <paramref name="owner"/> is in the visual tree (so a closed page does not stay subscribed).
    /// </summary>
    public static void Observe(FrameworkElement owner, BrowserLibrary library, string property, Action changed) =>
        Observe(owner, library, [property], changed);

    public static void Observe(FrameworkElement owner, global::System.ComponentModel.INotifyPropertyChanged source, string[] properties, Action changed)
    {
        void Handler(object? sender, global::System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName is null || properties.Contains(args.PropertyName)) changed();
        }
        var subscribed = false;
        owner.Loaded += (_, _) =>
        {
            if (subscribed) return;
            source.PropertyChanged += Handler;
            subscribed = true;
            changed();
        };
        owner.Unloaded += (_, _) =>
        {
            if (!subscribed) return;
            source.PropertyChanged -= Handler;
            subscribed = false;
        };
    }

    /// <summary>"14:05" today, "Yesterday", "Mon 3 Nov", "3 Nov 2024".</summary>
    public static string ShortDate(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        if (local.Year == today.Year) return local.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    // ---- Files ----

    private static nint WindowHandle(UIElement anchor)
    {
        if (MainWindow is { } window) return WinRT.Interop.WindowNative.GetWindowHandle(window);
        var id = anchor.XamlRoot?.ContentIslandEnvironment?.AppWindowId ?? default;
        return Win32Interop.GetWindowFromWindowId(id);
    }

    /// <summary>Asks for a bookmark file to import (HTML from any browser, or a Chrome/Edge Bookmarks file).</summary>
    public static async Task<string?> PickImportFileAsync(UIElement anchor)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
        };
        foreach (var type in (string[])[".html", ".htm", ".json", "*"]) picker.FileTypeFilter.Add(type);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle(anchor));
        FeatureMounts.CoverPage("library.picker", true);
        try
        {
            return (await picker.PickSingleFileAsync())?.Path;
        }
        finally
        {
            FeatureMounts.CoverPage("library.picker", false);
        }
    }

    public static async Task<(string Path, BookmarkFileFormat Format)?> PickExportFileAsync(UIElement anchor)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "Mosaic Bookmarks",
        };
        picker.FileTypeChoices.Add("Bookmarks file (HTML)", [".html"]);
        picker.FileTypeChoices.Add("Chrome bookmarks (JSON)", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle(anchor));
        FeatureMounts.CoverPage("library.picker", true);
        try
        {
            var file = await picker.PickSaveFileAsync();
            if (file is null) return null;
            var format = file.FileType.Equals(".json", StringComparison.OrdinalIgnoreCase) ? BookmarkFileFormat.ChromeJson : BookmarkFileFormat.NetscapeHtml;
            return (file.Path, format);
        }
        finally
        {
            FeatureMounts.CoverPage("library.picker", false);
        }
    }

    public static async Task ImportAsync(BrowserModel model, UIElement anchor, string? path = null)
    {
        path ??= await PickImportFileAsync(anchor);
        if (path is null) return;
        try
        {
            var count = await model.Library.ImportBookmarksAsync(path);
            model.Notice = count == 0 ? "No new bookmarks to import" : $"Imported {count} bookmark{(count == 1 ? "" : "s")}";
        }
        catch (LibraryException error)
        {
            model.Notice = $"Could not import bookmarks: {error.Message}";
        }
    }

    public static async Task ExportAsync(BrowserModel model, UIElement anchor)
    {
        if (await PickExportFileAsync(anchor) is not { } target) return;
        try
        {
            await model.Library.ExportBookmarksAsync(target.Path, target.Format);
            model.Notice = "Bookmarks exported";
        }
        catch (LibraryException error)
        {
            model.Notice = $"Could not export bookmarks: {error.Message}";
        }
    }

    /// <summary>Import and Export items, including "Import from Microsoft Edge/Google Chrome" for browsers found on this PC.</summary>
    public static IEnumerable<MenuFlyoutItemBase> ImportExportItems(BrowserModel model, UIElement anchor)
    {
        yield return MenuItem("Import Bookmarks…", () => _ = ImportAsync(model, anchor), "square.and.arrow.down");
        foreach (var (browser, path) in ChromeBookmarks.KnownFiles())
        {
            yield return MenuItem($"Import from {browser}", () => _ = ImportAsync(model, anchor, path));
        }
        yield return MenuItem("Export Bookmarks…", () => _ = ExportAsync(model, anchor), "square.and.arrow.up");
    }
}
