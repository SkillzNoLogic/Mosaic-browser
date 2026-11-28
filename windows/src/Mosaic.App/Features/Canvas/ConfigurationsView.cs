using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Canvas;
using Mosaic.Core.Canvas.Interim;
using Windows.Storage.Pickers;
using Windows.System;

namespace Mosaic.App.Features.Canvas;

/// <summary>
/// Configurations (macOS ConfigurationsPane): named layouts (layout + appearance) to switch
/// between, rename, update, delete, export to .json and import (validated, every repair listed),
/// and preset defaults. Files are the macOS app's format, so they move between the platforms.
/// </summary>
internal sealed class ConfigurationsView : UserControl, ICanvasView
{
    private readonly BrowserModel model;
    private readonly CanvasController canvas;
    private readonly TextBox newName = new() { PlaceholderText = "Name", MinWidth = 260 };
    private readonly StackPanel list = new() { Spacing = 8 };
    private readonly ContentControl reportHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock status = CanvasUi.Caption("");
    private readonly Button restoreActive, restoreAll;
    private string? renaming;
    private string shownKey = "";

    public ConfigurationsView(BrowserModel model, CanvasController canvas)
    {
        this.model = model;
        this.canvas = canvas;
        AutomationProperties.SetName(newName, "New configuration name");
        newName.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Save(); e.Handled = true; } };
        var save = CanvasUi.Accented(CanvasUi.Button("Save Current Layout", "square.and.arrow.down", Save));
        var import = CanvasUi.Button("Import…", "square.and.arrow.down", () => _ = ImportAsync());
        var export = CanvasUi.Button("Export Current Layout…", "square.and.arrow.up", () => _ = ExportAsync(null, canvas.Layout.Name));
        restoreActive = CanvasUi.Button("Restore Defaults", "arrow.counterclockwise", canvas.ResetToPreset);
        restoreAll = CanvasUi.Button("Restore All Presets…", null, () => _ = ConfirmRestoreAllAsync());

        var column = new StackPanel { Spacing = 18, Padding = new Thickness(20), MaxWidth = 900 };
        column.Children.Add(CanvasUi.Title("Configurations"));
        column.Children.Add(Section("Save", CanvasUi.Row(10, newName, save),
            "Saves the window’s layout and its appearance (colours, background, New Tab and workspace backgrounds) as they are now."));
        column.Children.Add(Section("Saved Configurations", list,
            "Switching applies the configuration’s preset with its layout; pages keep running. The preset keeps that layout until you restore its defaults."));
        column.Children.Add(Section("Import & Export", CanvasUi.Column(10, CanvasUi.Row(10, import, export), reportHost),
            "Layouts are small .json files, the same as Mosaic for macOS writes. Imported files are checked: unknown items are dropped, values brought into range, and the address field and a way to Settings kept."));
        column.Children.Add(Section("Preset Defaults", CanvasUi.Row(10, restoreActive, restoreAll),
            "Returns presets to their factory layouts and looks. Saved configurations are kept."));
        column.Children.Add(status);
        Content = new ScrollViewer { Content = column };
    }

    private static UIElement Section(string title, UIElement body, string note) =>
        CanvasUi.Column(8, CanvasUi.Heading(title), CanvasUi.Card(body), CanvasUi.Caption(note));

    public void Refresh()
    {
        newName.PlaceholderText = canvas.Layout.Name;
        restoreActive.Content = CanvasUi.Row(6, Icons.Make("arrow.counterclockwise", 14), new TextBlock { Text = $"Restore {canvas.ActivePreset.Title()} Defaults" });
        restoreActive.IsEnabled = canvas.Host.IsCustomized(canvas.ActivePreset);
        restoreAll.IsEnabled = Enum.GetValues<MosaicPresetID>().Any(canvas.Host.IsCustomized);
        status.Text = canvas.Message?.Text ?? "";
        status.Foreground = canvas.Message?.IsError == true ? CanvasUi.Critical : CanvasUi.Secondary;
        var active = canvas.ActiveConfiguration?.Id;
        var key = string.Join("|", canvas.Library.Configurations.Select(c => $"{c.Id}:{c.Name}:{c.Modified.Ticks}")) + $"|{active}|{renaming}|{canvas.LastImport}";
        if (key == shownKey) return;
        shownKey = key;
        list.Children.Clear();
        if (canvas.Library.Configurations.Count == 0)
            list.Children.Add(CanvasUi.Text("No saved configurations yet. Save the current layout above, or import a file.", 14, CanvasUi.Secondary));
        foreach (var configuration in canvas.Library.Configurations) list.Children.Add(Row(configuration, configuration.Id == active));
        reportHost.Content = canvas.LastImport is { } outcome ? Report(outcome) : null;
    }

    private void Save()
    {
        var name = string.IsNullOrWhiteSpace(newName.Text) ? canvas.Layout.Name : newName.Text;
        if (canvas.SaveCurrent(name) is not null) newName.Text = "";
    }

    private UIElement Row(LayoutConfiguration configuration, bool isActive)
    {
        var preview = new Border
        {
            Width = 128,
            Height = 80,
            CornerRadius = new CornerRadius(7),
            BorderBrush = isActive ? CanvasUi.Accent : CanvasUi.CardStroke,
            BorderThickness = new Thickness(isActive ? 2 : 1),
            Child = new GradientBackground(configuration.Layout.Appearance.Background, GradientRaster.ThumbnailWidth),
        };
        UIElement name;
        if (renaming == configuration.Id)
        {
            var box = new TextBox { Text = configuration.Name, MinWidth = 200 };
            AutomationProperties.SetName(box, "New name");
            box.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter) { Rename(configuration.Id, box.Text); e.Handled = true; }
                else if (e.Key == VirtualKey.Escape) { renaming = null; Refresh(); e.Handled = true; }
            };
            box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
            name = box;
        }
        else
        {
            var title = CanvasUi.Row(6, CanvasUi.Heading(configuration.Name));
            if (isActive) title.Children.Add(CanvasUi.Text("In Use", 12, CanvasUi.Accent, bold: true));
            name = title;
        }
        var detail = CanvasUi.Caption($"{configuration.Layout.Preset.Title()} · {(configuration.Layout.TabPlacement.IsVertical() ? "vertical tabs" : "tabs on top")} · saved {configuration.Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}");

        Button primary;
        if (renaming == configuration.Id) primary = CanvasUi.Button("Done", null, () => Rename(configuration.Id, ((TextBox)name).Text));
        else if (isActive) { primary = CanvasUi.Button("Applied", null, () => { }); primary.IsEnabled = false; }
        else primary = CanvasUi.Accented(CanvasUi.Button("Apply", null, () => canvas.ApplyConfiguration(configuration.Id)));
        AutomationProperties.SetName(primary, $"Apply {configuration.Name}");

        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        Item("Update with Current Layout", () => canvas.UpdateConfiguration(configuration.Id), !isActive);
        Item("Rename…", () => { renaming = configuration.Id; Refresh(); });
        Item("Export…", () => _ = ExportAsync(configuration.Id, configuration.Name));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Delete…", () => _ = ConfirmDeleteAsync(configuration));
        var more = new Button { Content = Icons.Make("ellipsis", 14), Flyout = menu };
        AutomationProperties.SetName(more, $"{configuration.Name} options");

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(preview);
        var text = CanvasUi.Column(3, name, detail);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var buttons = CanvasUi.Row(6, primary, more);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        grid.ContextFlyout = menu;
        AutomationProperties.SetName(grid, configuration.Name + (isActive ? ", in use" : ""));
        return grid;
    }

    private void Rename(string id, string name)
    {
        renaming = null;
        canvas.RenameConfiguration(id, name);
        Refresh();
    }

    private UIElement Report(CanvasController.ImportOutcome outcome)
    {
        var column = new StackPanel { Spacing = 6 };
        if (outcome.Error is { } error)
        {
            column.Children.Add(CanvasUi.Row(6, Icons.Make("exclamationmark.triangle.fill", 14), CanvasUi.Text($"Couldn’t import {outcome.File}", 14, bold: true)));
            column.Children.Add(CanvasUi.Text(error, 13, CanvasUi.Secondary));
        }
        else
        {
            column.Children.Add(CanvasUi.Row(6, Icons.Make("checkmark", 14), CanvasUi.Text($"Imported “{outcome.Name}”", 14, bold: true)));
            column.Children.Add(CanvasUi.Caption(outcome.Messages.Count == 0 ? "The file was clean: nothing needed repairing." : "Repairs made:"));
            foreach (var message in outcome.Messages) column.Children.Add(CanvasUi.Text("• " + message, 13));
        }
        var dismiss = CanvasUi.Button("Dismiss", null, canvas.DismissImportReport);
        dismiss.HorizontalAlignment = HorizontalAlignment.Right;
        column.Children.Add(dismiss);
        var border = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = outcome.Error is null ? CanvasUi.Subtle : CanvasUi.CriticalBackground,
            Child = column,
        };
        AutomationProperties.SetName(border, "Import report");
        AutomationProperties.SetLiveSetting(border, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        return border;
    }

    // MARK: Files and confirmations

    private static T WithWindow<T>(T picker) where T : class
    {
        if (CanvasMounts.Window is { } window) WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return picker;
    }

    private async Task ImportAsync()
    {
        var picker = WithWindow(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary });
        picker.FileTypeFilter.Add(".json");
        if (await picker.PickSingleFileAsync() is { } file) await canvas.ImportFileAsync(file.Path);
    }

    private async Task ExportAsync(string? id, string name)
    {
        var picker = WithWindow(new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = name });
        picker.FileTypeChoices.Add("Mosaic layout", [".json"]);
        if (await picker.PickSaveFileAsync() is { } file) await canvas.ExportAsync(id, file.Path);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        // A dialog draws over the page: Gecko's window would cover it.
        FeatureMounts.CoverPage("canvas-dialog", true);
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { FeatureMounts.CoverPage("canvas-dialog", false); }
    }

    private async Task ConfirmDeleteAsync(LayoutConfiguration configuration)
    {
        if (await ConfirmAsync($"Delete “{configuration.Name}”?", "The window keeps its current layout.", "Delete"))
            canvas.DeleteConfiguration(configuration.Id);
    }

    private async Task ConfirmRestoreAllAsync()
    {
        if (await ConfirmAsync("Restore every preset to its defaults?",
                "Your changes to all presets are replaced by their factory layouts. Saved configurations are kept.", "Restore All"))
            canvas.RestoreAllPresetDefaults();
    }
}
