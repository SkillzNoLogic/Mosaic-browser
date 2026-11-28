using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Mosaic.App.Features;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Extensions;
using Windows.Storage.Pickers;
using Windows.System;

namespace Mosaic.App;

/// <summary>
/// The Extensions sheet: install Chrome extensions from a Chrome Web Store link or ID, or a
/// <c>.crx</c> file, and manage them. Shows <see cref="ExtensionManager"/>, whose methods report
/// problems through <see cref="ExtensionManager.ErrorMessage"/> rather than throwing (which is
/// what makes the async handlers here safe). Port of the v0.1 Extensions window, drawn over the
/// page area as a sheet; while it is open the page is covered (Gecko's window hidden).
/// </summary>
/// <remarks>The platform area's Extensions settings page replaces it in the composed shell.</remarks>
internal sealed partial class BasicShell
{
    private const string ExtensionsCover = "winui.extensions";

    private readonly Grid extensionsSheet;
    private readonly List<IDisposable> extensionRowWatches = [];
    private StackPanel? extensionRows;
    private TextBox? installInput;

    private Grid CreateExtensionsSheet()
    {
        var manager = model.Extensions;
        var sheet = new Grid { Background = Ui.Brush("SmokeFillColorDefaultBrush"), Visibility = Visibility.Collapsed };
        var card = new Grid
        {
            MaxWidth = 680,
            MaxHeight = 560,
            Margin = new Thickness(16),
            Padding = new Thickness(20),
            Background = Ui.Brush("SolidBackgroundFillColorBaseBrush"),
            BorderBrush = Ui.Brush("SurfaceStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            RowSpacing = 0,
        };
        for (int i = 0; i < 6; i++) card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Star);
        sheet.Children.Add(card);

        // Heading
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Extensions", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(Ui.Text("Chrome extensions (.crx) are converted and run on the Mozilla engine.", 14, "TextFillColorSecondaryBrush"));
        heading.Children.Add(titles);
        var done = new Button { Content = "Done", MinWidth = 80, VerticalAlignment = VerticalAlignment.Top };
        done.Click += (_, _) => model.ShowExtensions = false;
        Grid.SetColumn(done, 1);
        heading.Children.Add(done);
        card.Children.Add(heading);

        // Install from a link, an ID, or a file
        var install = new Grid { Margin = new Thickness(0, 14, 0, 0), ColumnSpacing = 8 };
        install.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) install.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var input = installInput = new TextBox { PlaceholderText = "Chrome Web Store link or extension ID" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "Chrome Web Store link or extension ID");
        var installButton = new Button { Content = "Install", MinWidth = 80, IsEnabled = false };
        var choose = new Button { Content = "Choose .crx…" };
        var busy = new ProgressRing { Width = 18, Height = 18, IsActive = false };
        install.Children.Add(input);
        Grid.SetColumn(installButton, 1);
        Grid.SetColumn(choose, 2);
        Grid.SetColumn(busy, 3);
        install.Children.Add(installButton);
        install.Children.Add(choose);
        install.Children.Add(busy);
        Grid.SetRow(install, 1);
        card.Children.Add(install);

        void UpdateInstallButton() => installButton.IsEnabled = manager.IsIdle && !string.IsNullOrWhiteSpace(input.Text);
        async Task InstallFromInputAsync()
        {
            var text = input.Text;
            if (string.IsNullOrWhiteSpace(text) || manager.IsBusy) return;
            // Keep what was typed if it did not work, so it can be corrected.
            if (await manager.InstallFromWebStoreAsync(text)) input.Text = "";
        }
        input.TextChanged += (_, _) => UpdateInstallButton();
        input.KeyDown += async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            await InstallFromInputAsync();
        };
        installButton.Click += async (_, _) => await InstallFromInputAsync();
        choose.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(".crx");
            picker.FileTypeFilter.Add(".zip");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Handle);
            if (await picker.PickSingleFileAsync() is { } file) await manager.InstallFileAsync(file.Path);
        };

        // What went wrong last, if anything
        var error = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = Ui.Brush("SystemFillColorCriticalBrush"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(error, 2);
        card.Children.Add(error);

        var rule = new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 14, 0, 12), Fill = Ui.Brush("ControlStrokeColorDefaultBrush") };
        Grid.SetRow(rule, 3);
        card.Children.Add(rule);

        // Nothing installed yet
        var empty = new StackPanel { MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var emptyIcon = Icons.Make("puzzlepiece", 28);
        emptyIcon.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Children.Add(emptyIcon);
        empty.Children.Add(new TextBlock { Text = "No extensions yet", Margin = new Thickness(0, 8, 0, 4), HorizontalAlignment = HorizontalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var emptyText = Ui.Text("Paste a Chrome Web Store link above, or open a listing in a tab and press Add to Mosaic.", 14, "TextFillColorSecondaryBrush");
        emptyText.TextAlignment = TextAlignment.Center;
        empty.Children.Add(emptyText);
        Grid.SetRow(empty, 4);
        card.Children.Add(empty);

        // Installed extensions
        extensionRows = new StackPanel { Spacing = 10 };
        var list = new ScrollViewer { Content = extensionRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(list, 4);
        card.Children.Add(list);

        // Shown only when the Chromium engine can take extensions at all
        var footnote = Ui.Text("Experimental: enabled extensions are also offered, unconverted, to Chromium tabs. Rows show where that worked.", 12, "TextFillColorSecondaryBrush");
        footnote.Margin = new Thickness(0, 10, 0, 0);
        footnote.Visibility = Ui.Visible(model.ChromiumAvailable);
        Grid.SetRow(footnote, 5);
        card.Children.Add(footnote);

        Ui.Watch(manager, () =>
        {
            UpdateInstallButton();
            choose.IsEnabled = manager.IsIdle;
            busy.IsActive = manager.IsBusy;
            busy.Visibility = Ui.Visible(manager.IsBusy);
            error.Text = manager.ErrorMessage ?? "";
            error.Visibility = Ui.Visible(manager.HasError);
            empty.Visibility = Ui.Visible(!manager.HasExtensions);
            list.Visibility = Ui.Visible(manager.HasExtensions);
        }, nameof(ExtensionManager.IsBusy), nameof(ExtensionManager.IsIdle), nameof(ExtensionManager.ErrorMessage),
           nameof(ExtensionManager.HasError), nameof(ExtensionManager.HasExtensions), nameof(ExtensionManager.Extensions));
        manager.Items.CollectionChanged += (_, _) => RebuildExtensionRows();
        RebuildExtensionRows();
        return sheet;
    }

    private void SyncExtensionsSheet()
    {
        bool show = model.ShowExtensions;
        extensionsSheet.Visibility = Ui.Visible(show);
        // Gecko's page is a native window that would draw over the sheet.
        FeatureMounts.CoverPage(ExtensionsCover, show);
        // Not in a scripted run, where the sheet opens without taking focus.
        if (show && model.AllowsFocusChanges && installInput?.XamlRoot is not null) installInput.Focus(FocusState.Programmatic);
    }

    private void RebuildExtensionRows()
    {
        if (extensionRows is null) return;
        foreach (var watch in extensionRowWatches) watch.Dispose();
        extensionRowWatches.Clear();
        extensionRows.Children.Clear();
        foreach (var item in model.Extensions.Items) extensionRows.Children.Add(CreateExtensionRow(item));
    }

    private FrameworkElement CreateExtensionRow(ExtensionItem item)
    {
        var manager = model.Extensions;
        var row = new StackPanel();
        var card = new Border { Padding = new Thickness(12), Background = Ui.Brush("SubtleFillColorSecondaryBrush"), CornerRadius = new CornerRadius(8), Child = row };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(top);

        // Icon, or a puzzle piece when the extension has none Mosaic can show
        var iconBox = new Grid { Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var placeholder = Icons.Make("puzzlepiece", 22);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        var image = new Image { Width = 36, Height = 36 };
        iconBox.Children.Add(placeholder);
        iconBox.Children.Add(image);
        top.Children.Add(iconBox);

        var text = new StackPanel();
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var name = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        var version = Ui.Text("", 11, "TextFillColorSecondaryBrush");
        version.VerticalAlignment = VerticalAlignment.Center;
        var manifest = Badge("");
        var running = Badge("Running", "SystemFillColorSuccessBrush");
        ToolTipService.SetToolTip(running, "Loaded in the Mozilla engine");
        var chromium = Badge("Chromium · experimental");
        ToolTipService.SetToolTip(chromium, "Experimental: the unconverted extension is also loaded in Chromium tabs");
        nameLine.Children.Add(name);
        nameLine.Children.Add(version);
        nameLine.Children.Add(manifest);
        nameLine.Children.Add(running);
        nameLine.Children.Add(chromium);
        text.Children.Add(nameLine);
        var summary = Ui.Text("", 13, "TextFillColorSecondaryBrush");
        summary.Margin = new Thickness(0, 3, 0, 0);
        summary.MaxLines = 2;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(summary);
        Grid.SetColumn(text, 1);
        top.Children.Add(text);

        var enabled = new CheckBox { Content = "Enabled", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, MinWidth = 0 };
        enabled.Click += async (_, _) => await manager.SetEnabledAsync(enabled.IsChecked == true, item.Extension);
        Grid.SetColumn(enabled, 2);
        top.Children.Add(enabled);

        var actions = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var popup = new Button { Content = "Open Popup" };
        popup.Click += async (_, _) => await model.OpenExtensionPageAsync(item.Extension, "popup");
        var options = new Button { Content = "Options" };
        options.Click += async (_, _) => await model.OpenExtensionPageAsync(item.Extension, "options");
        var notesToggle = new ToggleButton();
        ToolTipService.SetToolTip(notesToggle, "Show what was changed or may not work");
        left.Children.Add(popup);
        left.Children.Add(options);
        left.Children.Add(notesToggle);
        actions.Children.Add(left);
        var remove = new Button { Content = "Remove" };
        remove.Click += async (_, _) => await manager.RemoveAsync(item.Extension);
        Grid.SetColumn(remove, 1);
        actions.Children.Add(remove);
        row.Children.Add(actions);

        var chromiumError = Ui.Text("", 12, "TextFillColorSecondaryBrush");
        chromiumError.Margin = new Thickness(0, 8, 0, 0);
        row.Children.Add(chromiumError);

        // Compatibility notes, revealed by the toggle above
        var notes = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Spacing = 4, Visibility = Visibility.Collapsed };
        notesToggle.Click += (_, _) => notes.Visibility = Ui.Visible(notesToggle.IsChecked == true);
        row.Children.Add(notes);

        string? shownIcon = null;
        extensionRowWatches.Add(Ui.Watch(item, () =>
        {
            name.Text = item.Name;
            version.Text = item.Version;
            ((TextBlock)manifest.Child).Text = item.ManifestBadge;
            running.Visibility = Ui.Visible(item.IsRunning);
            chromium.Visibility = Ui.Visible(item.IsInChromium);
            summary.Text = item.Summary;
            summary.Visibility = Ui.Visible(item.HasSummary);
            enabled.IsChecked = item.IsEnabled;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(enabled, item.EnableLabel);
            popup.Visibility = Ui.Visible(item.HasPopup);
            options.Visibility = Ui.Visible(item.HasOptions);
            notesToggle.Content = item.CompatibilityLabel;
            notesToggle.Visibility = Ui.Visible(item.HasNotes);
            chromiumError.Text = $"Not loaded in Chromium (experimental): {item.ChromiumError}";
            chromiumError.Visibility = Ui.Visible(item.HasChromiumError);
            notes.Children.Clear();
            foreach (var note in item.Notes) notes.Children.Add(NoteLine(note));
            if (item.IconFile != shownIcon)
            {
                shownIcon = item.IconFile;
                _ = LoadIconAsync(image, placeholder, item.IconFile);
            }
        }));
        return card;
    }

    private static Border Badge(string text, string? brush = null)
    {
        var label = new TextBlock { Text = text, FontSize = 10, Margin = new Thickness(5, 1, 5, 1) };
        if (brush is not null) label.Foreground = Ui.Brush(brush);
        return new Border
        {
            Child = label,
            BorderBrush = Ui.Brush(brush ?? "ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static FrameworkElement NoteLine(CompatibilityNote note)
    {
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Icons.Make("info", 12);
        (icon.Glyph, var brush) = note.Severity switch
        {
            NoteSeverity.Warning => ("", "SystemFillColorCautionBrush"),
            NoteSeverity.Blocker => ("", "SystemFillColorCriticalBrush"),
            _ => ("", "TextFillColorSecondaryBrush"),
        };
        icon.Foreground = Ui.Brush(brush);
        icon.Margin = new Thickness(0, 2, 7, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        line.Children.Add(icon);
        var message = Ui.Text(note.Message, 12, "TextFillColorSecondaryBrush");
        Grid.SetColumn(message, 1);
        line.Children.Add(message);
        return line;
    }

    /// <summary>
    /// Shows an extension's icon. Read whole off the UI thread, so the file is not kept open
    /// (it would stop the extension's folder from being replaced); an icon that cannot be read
    /// or decoded (extensions ship all sorts) is no icon.
    /// </summary>
    private static async Task LoadIconAsync(Image image, FrameworkElement placeholder, string? path)
    {
        image.Source = null;
        placeholder.Visibility = Visibility.Visible;
        if (path is null) return;
        try
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(path));
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 72 };
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
            placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            // No icon, then.
        }
    }
}
