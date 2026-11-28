using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Extensions;
using Mosaic.Core.Gecko;
using static Mosaic.App.Features.Platform.PlatformUi;

namespace Mosaic.App.Features.Platform;

/// <summary>
/// Settings › Extensions: install Chrome extensions (Web Store link or ID, or a <c>.crx</c>/<c>.zip</c>
/// file), and manage them: enable, popup, options, permissions, compatibility, remove. Port of
/// <c>ExtensionsSettingsPane.swift</c>; works on the existing <see cref="ExtensionManager"/>.
/// Popups open as a Mozilla tab, not a bubble under the toolbar, as on macOS.
/// </summary>
internal sealed class ExtensionsSettingsPage : PlatformPage
{
    private readonly BrowserModel model;
    private readonly ExtensionManager manager;
    private readonly HashSet<string> expanded = [];
    private readonly Signal toggled = new();

    public ExtensionsSettingsPage(BrowserModel model) : base("Extensions")
    {
        this.model = model;
        manager = model.Extensions;
        Body.Children.Add(Section(Intro, model));
        Body.Children.Add(Header("Add an extension"));
        Body.Children.Add(Card(Installer()));
        Body.Children.Add(Section(InstallState, manager));
        Body.Children.Add(Section(Installed, manager, (INotifyPropertyChanged)manager.Items, new GeckoStatusSignal(model.Gecko), toggled));
        Body.Children.Add(Note("Popups open in a Mozilla tab, not a bubble under the toolbar. Extensions don’t update themselves: install again to update (settings are kept)."));
    }

    private void Intro(StackPanel panel)
    {
        panel.Children.Add(Note(
            "Mosaic converts Chrome extensions and runs them in Mozilla tabs. Chromium tabs get the unconverted extension where " +
            "WebView2 supports it. Most extensions work; anything an extension relies on that Gecko lacks is listed next to it."));
        if (model.GeckoRuntimeMissing)
        {
            panel.Children.Add(Card(Row(10,
                Message("Extensions need the Mozilla engine, which is not installed.", Tone.Warning),
                Button("Install…", () => model.Platform.Runtime.Install()))));
        }
    }

    /// <summary>Not re-rendered: keeps what is being typed.</summary>
    private StackPanel Installer()
    {
        var input = new TextBox { PlaceholderText = "https://chromewebstore.google.com/detail/…", MinWidth = 320 };
        AutomationProperties.SetName(input, "Chrome Web Store link or extension ID");
        var install = Button("Install", () => _ = InstallAsync());
        async Task InstallAsync()
        {
            if (input.Text.Trim().Length == 0 || manager.IsBusy) return;
            if (await manager.InstallFromWebStoreAsync(input.Text)) input.Text = "";
        }
        input.KeyDown += (_, args) =>
        {
            if (args.Key == global::Windows.System.VirtualKey.Enter) _ = InstallAsync();
        };
        var file = Button("Choose file…", () => _ = ChooseFileAsync(), help: "Install a .crx or zipped extension from disk");
        return Column(8, Row(8, input, install, file),
            Note("Or open an extension’s page on the Chrome Web Store in any tab and press Add to Mosaic in the toolbar."));
    }

    private async Task ChooseFileAsync()
    {
        if (manager.IsBusy || PlatformMounts.Window is not { } window) return;
        var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".crx");
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        FeatureMounts.CoverPage("platform.picker", true);
        try
        {
            if (await picker.PickSingleFileAsync() is { } chosen) await manager.InstallFileAsync(chosen.Path);
        }
        finally
        {
            FeatureMounts.CoverPage("platform.picker", false);
        }
    }

    private void InstallState(StackPanel panel)
    {
        if (manager.IsBusy)
        {
            var ring = new ProgressRing { IsActive = true, Width = 16, Height = 16 };
            AutomationProperties.SetName(ring, "Installing");
            panel.Children.Add(Row(8, ring, Note("Installing…")));
        }
        if (manager.ErrorMessage is { } error) panel.Children.Add(Message(error, Tone.Negative));
    }

    private void Installed(StackPanel panel)
    {
        var items = manager.Items;
        panel.Children.Add(Header(items.Count == 0 ? "Installed" : $"Installed ({items.Count})"));
        if (items.Count == 0)
        {
            var icon = Icons.Make("puzzlepiece.extension", 22);
            icon.Foreground = Secondary;
            panel.Children.Add(Card(Row(10, icon, new TextBlock { Text = "No extensions installed yet.", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center })));
            return;
        }
        foreach (var item in items) panel.Children.Add(Card(ExtensionRow(item)));
    }

    private StackPanel ExtensionRow(ExtensionItem item)
    {
        var extension = item.Extension;
        UIElement icon;
        if (item.IconFile is { } path && File.Exists(path))
        {
            icon = new Image { Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 36 }, Width = 36, Height = 36 };
        }
        else
        {
            var glyph = Icons.Make("puzzlepiece.extension.fill", 22);
            glyph.Foreground = Secondary;
            glyph.Width = 36;
            icon = glyph;
        }
        AutomationProperties.SetAccessibilityView((FrameworkElement)icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        ((FrameworkElement)icon).VerticalAlignment = VerticalAlignment.Top;

        var title = Row(6, Text(item.Name, strong: true), new TextBlock { Text = item.Version, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center },
            Badge(item.ManifestBadge), StateBadge(item));
        var text = Column(3, title);
        if (item.HasSummary) text.Children.Add(Note(item.Summary));

        var toggle = new ToggleSwitch { IsOn = item.IsEnabled, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(toggle, item.EnableLabel);
        toggle.Toggled += (_, _) =>
        {
            if (toggle.IsOn != extension.Enabled) _ = manager.SetEnabledAsync(toggle.IsOn, extension);
        };

        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(icon);
        Grid.SetColumn(text, 1);
        heading.Children.Add(text);
        Grid.SetColumn(toggle, 2);
        heading.Children.Add(toggle);

        var actions = Row(8);
        if (item.HasPopup)
        {
            var popup = Button("Open popup", () => _ = model.OpenExtensionPageAsync(extension, "popup"), help: "Opens in a Mozilla tab");
            popup.IsEnabled = item.IsEnabled;
            actions.Children.Add(popup);
        }
        if (item.HasOptions)
        {
            var options = Button("Options", () => _ = model.OpenExtensionPageAsync(extension, "options"));
            options.IsEnabled = item.IsEnabled;
            actions.Children.Add(options);
        }
        var isOpen = expanded.Contains(extension.Id);
        var details = Button((isOpen ? "Hide " : "") + DetailsLabel(item), () =>
        {
            if (!expanded.Remove(extension.Id)) expanded.Add(extension.Id);
            toggled.Raise();
        });
        actions.Children.Add(details);
        actions.Children.Add(Button("Remove…", () => _ = RemoveAsync(extension)));

        var row = Column(8, heading, actions);
        if (isOpen) row.Children.Add(Details(item));
        AutomationProperties.SetName(row, $"{item.Name} {item.Version}");
        return row;
    }

    private Border StateBadge(ExtensionItem item) =>
        !item.IsEnabled ? Badge("Off")
        : item.IsRunning ? Badge("Running", Tone.Positive)
        : model.Gecko.Status == GeckoStatus.Running ? Badge("Not loaded", Tone.Negative)
        : Badge("Starts with Mozilla engine");

    private static string DetailsLabel(ExtensionItem item) => item.WorstSeverity switch
    {
        NoteSeverity.Blocker => "May not work on Gecko",
        NoteSeverity.Warning => "Partly compatible",
        _ => "Permissions & compatibility",
    };

    private Border Details(ExtensionItem item)
    {
        var permissions = model.Platform.PermissionsOf(item.Extension);
        var column = Column(4, Text("Permissions", strong: true));
        foreach (var line in permissions.Summary) column.Children.Add(CapabilityRow(Capability.Info, line));
        column.Children.Add(new TextBlock { Text = "Compatibility with Gecko", Style = Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 6, 0, 0) });
        if (item.Notes.Count == 0) column.Children.Add(CapabilityRow(Capability.Yes, "Converted without changes that affect it"));
        foreach (var note in item.Notes)
        {
            column.Children.Add(Message(note.Message, note.Severity switch
            {
                NoteSeverity.Blocker => Tone.Negative,
                NoteSeverity.Warning => Tone.Warning,
                _ => Tone.Neutral,
            }));
        }
        if (item.IsInChromium) column.Children.Add(CapabilityRow(Capability.Yes, "Also loaded in Chromium tabs"));
        if (item.ChromiumError is { } chromiumError) column.Children.Add(CapabilityRow(Capability.Info, $"Not in Chromium tabs: {chromiumError}"));
        return new Border
        {
            Child = column,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = Brush("SubtleFillColorSecondaryBrush", Windows.UI.Color.FromArgb(0x0A, 0x80, 0x80, 0x80)),
        };
    }

    private async Task RemoveAsync(InstalledExtension extension)
    {
        if (await ConfirmAsync(XamlRoot, $"Remove {extension.Name}?", "Its settings and data are deleted.", "Remove"))
        {
            await manager.RemoveAsync(extension);
        }
    }
}

