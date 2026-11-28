using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;
using Windows.UI;

using XamlGradientStop = Microsoft.UI.Xaml.Media.GradientStop;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// Settings › Mosaics (port of macOS's <c>MosaicsSettingsPane</c>): preset cards with a preview,
/// Apply, Preview and Restore Defaults; the active layout's options; appearance (light/dark,
/// accent, density, corner radius, page inset, the materials toggle Mica/Acrylic ↔ solid, glow);
/// the background; and the way into the canvas area's editor (<c>mosaic://customize</c>).
/// </summary>
internal sealed class MosaicsSettingsPage : Grid
{
    private readonly ShellRoot shell;
    private readonly Border body = new();
    private bool applying;
    private bool rebuildScheduled;
    private Grid? cards;

    private BrowserModel Model => shell.Model;
    private MosaicStore Store => shell.Model.Mosaics;
    private ShellTheme Theme => shell.Theme;

    public MosaicsSettingsPage(ShellRoot shell)
    {
        this.shell = shell;
        Children.Add(body);
        Build();
        Loaded += (_, _) => Model.ShellChanged += OnShellChanged;
        Unloaded += (_, _) => Model.ShellChanged -= OnShellChanged;
    }

    private void OnShellChanged()
    {
        // Changes made on this page keep its controls (a slider mid-drag must not be rebuilt);
        // the cards still show what is active and customised. Changes from elsewhere rebuild it.
        if (applying)
        {
            FillCards();
            return;
        }
        if (rebuildScheduled) return;
        rebuildScheduled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            rebuildScheduled = false;
            Build();
        });
    }

    private void Change(Action action)
    {
        applying = true;
        try
        {
            action();
        }
        finally
        {
            applying = false;
        }
    }

    private void Update(Action<MosaicLayout> change) => Change(() => Store.Update(change));

    private void Build()
    {
        var kit = new SettingsKit(Theme);
        var layout = Store.Layout;
        var a = layout.Appearance;

        if (Store.PreviewedPreset is { } previewed) kit.Stack.Children.Add(PreviewBanner(previewed));

        kit.Section("Presets");
        cards = new Grid { ColumnSpacing = 14, RowSpacing = 14 };
        cards.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) FillCards();
        };
        kit.Stack.Children.Add(cards);
        FillCards();
        kit.Note("Switching is instant: pages keep running and nothing is reloaded. Each preset remembers your changes.");

        kit.Section($"Layout — {layout.Name}");
        kit.Card("Tabs", layout.Preset == MosaicPresetID.Edge ? "Vertical tabs keep the toolbar and put the tabs in a pane you can collapse (Ctrl+Shift+,)." : null,
            SettingsKit.Combo(Enum.GetValues<TabPlacement>().Select(p => (p, p.Title())), layout.TabPlacement,
                p => Change(() => Store.ReplaceActiveLayout(Store.Layout.WithTabPlacement(p))), "Tabs"));
        if (!layout.TabPlacement.IsVertical())
        {
            kit.Card("Tab style", null, SettingsKit.Combo(Enum.GetValues<TabStyle>().Select(t => (t, t.Title())), layout.TabStyle,
                t => Update(l => l.TabStyle = t), "Tab style"));
        }
        kit.Card("Address bar", null, SettingsKit.Combo(Enum.GetValues<AddressBarStyle>().Select(s => (s, s.Title())), layout.AddressBar,
            s => Update(l => l.AddressBar = s), "Address bar"));
        if (layout.TabPlacement.IsVertical())
        {
            kit.Card("Sidebar", null, SettingsKit.Combo(Enum.GetValues<SidebarBehavior>().Select(b => (b, b.Title())), layout.Sidebar,
                b => Update(l => l.Sidebar = b), "Sidebar"));
            kit.Card("Sidebar width", null, SettingsKit.Slider(layout.SidebarWidth, 180, 420, 4, v => Update(l => l.SidebarWidth = v), "Sidebar width"));
        }
        kit.Card("Bookmarks bar", null, SettingsKit.Toggle(layout.BookmarksBar, on => Update(l => l.BookmarksBar = on), "Bookmarks bar"));
        kit.Card("Customise layout", "Drag buttons between the title bar, toolbar, sidebar and rails, with undo.",
            SettingsKit.Button("Open Layout Editor…", () => Model.OpenInternalPage("customize")));

        kit.Section("Appearance");
        kit.Card("Light or dark", null, SettingsKit.Radios(
            [(ColorSchemePreference.Light, "Light"), (ColorSchemePreference.Dark, "Dark"), (ColorSchemePreference.System, "Use Windows setting")],
            a.ColorScheme, c => Update(l => l.Appearance.ColorScheme = c), "Light or dark"));
        kit.Card("Accent colour", Theme.HighContrast ? "A contrast theme is on: Windows's colours are used." : null, AccentPicker(a.Accent));
        kit.Card("Density", null, SettingsKit.Radios(Enum.GetValues<ShellDensity>().Select(d => (d, d.Title())), a.Density,
            d => Update(l => l.Appearance.Density = d), "Density"));
        kit.Card("Corner radius", "Square to round tabs, buttons and fields.",
            SettingsKit.Slider(a.CornerRadius, 0, 16, 1, v => Update(l => l.Appearance.CornerRadius = v), "Corner radius"));
        kit.Card("Page inset", "Flush with the window, or floating in a card.",
            SettingsKit.Slider(a.ContentInset, 0, 16, 1, v => Update(l => l.Appearance.ContentInset = v), "Page inset"));
        var corners = SettingsKit.Slider(a.ContentCornerRadius, 0, 24, 1, v => Update(l => l.Appearance.ContentCornerRadius = v), "Page corners");
        kit.Card("Page corners", null, corners);
        var materials = SettingsKit.Toggle(a.Material == ChromeMaterial.LiquidGlass,
            on => Update(l => l.Appearance.Material = on ? ChromeMaterial.LiquidGlass : ChromeMaterial.Solid),
            "Mica and Acrylic", "Translucent", "Solid");
        materials.IsEnabled = ShellTheme.MicaSupported && !Theme.HighContrast;
        kit.Card("Mica and Acrylic", MaterialsNote(), materials);
        kit.Card("Glow around the page", "Opera GX's soft accent-coloured edge.",
            SettingsKit.Toggle(a.Glow, on => Update(l => l.Appearance.Glow = on), "Glow around the page"));

        kit.Section("Background");
        kit.Card("Window background", BackgroundNote(a.Background), SettingsKit.Radios(
            [(BackgroundKind.System, "System"), (BackgroundKind.Solid, "Colour"), (BackgroundKind.Gradient, "Gradient")],
            a.Background.Kind, k => Change(() => { Store.Update(l => l.Appearance.Background.Kind = k); Build(); }), "Window background"));
        if (a.Background.Kind == BackgroundKind.Solid)
        {
            kit.Card("Colour", null, ColorButton(a.Background.Color, hex => Update(l => l.Appearance.Background.Color = hex), "Background colour"));
        }
        if (a.Background.Kind == BackgroundKind.Gradient) kit.Card("Gradient", null, GradientSwatches(a.Background.Gradient));
        kit.Card("New Tab page", null, SettingsKit.Combo(Enum.GetValues<NewTabBackgroundMode>().Select(m => (m, m.Title())),
            a.NewTabBackground.Mode, m => Update(l => l.Appearance.NewTabBackground.Mode = m), "New Tab page background"));
        kit.Card("Gradient Studio", "Free-form gradients for the window, the New Tab page and each workspace.",
            SettingsKit.Button("Open Gradient Studio…", () => Model.OpenInternalPage("customize")));

        kit.Section("Defaults");
        var restore = SettingsKit.Button($"Restore {layout.Name} Defaults", () => Change(() =>
        {
            Store.RestoreDefaults(Store.ActivePreset);
            Build();
        }));
        restore.IsEnabled = Store.IsCustomized(Store.ActivePreset);
        kit.Card("Restore defaults", "Restores the active preset's layout and appearance; other presets keep their changes.", restore);

        body.Child = kit.Scroller();
    }

    private string MaterialsNote()
    {
        if (!ShellTheme.MicaSupported) return "Mica needs Windows 11; solid surfaces are used on this PC.";
        if (Theme.HighContrast) return "Off while a contrast theme is on.";
        if (Theme.ReduceTransparency) return "Off while transparency effects are off (Windows Settings › Personalisation › Colours, or Settings › Accessibility).";
        return "Mica on the window and Acrylic on menus and panels. Off: solid surfaces.";
    }

    private static string? BackgroundNote(BackgroundSpec spec) => spec.Kind switch
    {
        BackgroundKind.System => "The standard window colour, following light and dark mode.",
        BackgroundKind.Gradient when spec.Gradient.Kind == GradientKind.Mesh => "A free-form gradient from the Gradient Studio.",
        _ => null,
    };

    // MARK: Preset cards

    private void FillCards()
    {
        if (cards is null) return;
        var width = cards.ActualWidth > 0 ? cards.ActualWidth : 600;
        var columns = Math.Max(1, (int)((width + 14) / 274));
        cards.Children.Clear();
        cards.ColumnDefinitions.Clear();
        cards.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var presets = Enum.GetValues<MosaicPresetID>();
        for (var r = 0; r < (presets.Length + columns - 1) / columns; r++) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < presets.Length; i++)
        {
            var card = PresetCard(presets[i], Math.Min(320, (width - (columns - 1) * 14) / columns - 26));
            SetRow(card, i / columns);
            SetColumn(card, i % columns);
            cards.Children.Add(card);
        }
    }

    private Border PresetCard(MosaicPresetID preset, double previewWidth)
    {
        var layout = Store.LayoutFor(preset);
        var active = Store.ActivePreset == preset;
        var previewing = Store.PreviewedPreset == preset;
        var stack = new StackPanel { Spacing = 8 };

        var preview = new Button
        {
            Content = MosaicPreview.Make(layout, Theme, Math.Max(180, previewWidth)),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(active ? 3 : 0),
            BorderBrush = Theme.Accent,
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Left,
        }.Named(active ? $"{preset.Title()}, active" : $"Preview {preset.Title()}");
        preview.Click += (_, _) => Change(() =>
        {
            if (active || previewing) Store.EndPreview();
            else Store.Preview(preset);
            Build();
        });
        stack.Children.Add(preview);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var heading = ShellUi.Text(preset.Title(), 15, semibold: true);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
        titleRow.Children.Add(heading);
        if (active) titleRow.Children.Add(ShellUi.Text("Active", 12, Theme.AccentText, semibold: true));
        else if (Store.IsCustomized(preset)) titleRow.Children.Add(ShellUi.Text("Customised", 12, Theme.SecondaryText));
        if (preset == MosaicPresetID.Edge) titleRow.Children.Add(ShellUi.Text("Windows", 12, Theme.SecondaryText));
        stack.Children.Add(titleRow);
        var summary = ShellUi.Text(preset.Summary(), 12.5, Theme.SecondaryText);
        summary.TextWrapping = TextWrapping.Wrap;
        summary.TextTrimming = TextTrimming.None;
        stack.Children.Add(summary);

        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (active)
        {
            buttons.Children.Add(new Button { Content = "Applied", IsEnabled = false }.Named($"{preset.Title()} applied"));
        }
        else
        {
            var apply = SettingsKit.Button("Apply", () => Change(() =>
            {
                Model.ApplyMosaic(preset);
                Build();
            }), accent: true);
            AutomationProperties.SetName(apply, $"Apply {preset.Title()}");
            buttons.Children.Add(apply);
            var previewButton = SettingsKit.Button(previewing ? "End Preview" : "Preview", () => Change(() =>
            {
                if (previewing) Store.EndPreview();
                else Store.Preview(preset);
                Build();
            }));
            AutomationProperties.SetName(previewButton, previewing ? $"End preview of {preset.Title()}" : $"Preview {preset.Title()}");
            SetColumn(previewButton, 1);
            buttons.Children.Add(previewButton);
        }
        var more = new MenuFlyout();
        if (preset == MosaicPresetID.Opera)
        {
            var gx = new ToggleMenuFlyoutItem { Text = "Opera GX styling", IsChecked = layout.HasGXStyling };
            gx.Click += (_, _) => Change(() =>
            {
                if (Store.ActivePreset != MosaicPresetID.Opera) Store.Apply(MosaicPresetID.Opera);
                Store.ReplaceActiveLayout(gx.IsChecked ? Store.Layout.WithGXStyling() : Store.Layout.WithoutGXStyling());
                Build();
            });
            more.Items.Add(gx);
            more.Items.Add(new MenuFlyoutSeparator());
        }
        if (preset == MosaicPresetID.Edge)
        {
            var vertical = new ToggleMenuFlyoutItem { Text = "Vertical tabs", IsChecked = layout.TabPlacement.IsVertical() };
            vertical.Click += (_, _) => Change(() =>
            {
                if (Store.ActivePreset != MosaicPresetID.Edge) Store.Apply(MosaicPresetID.Edge);
                Model.ToggleVerticalTabs();
                Build();
            });
            more.Items.Add(vertical);
            more.Items.Add(new MenuFlyoutSeparator());
        }
        var restore = new MenuFlyoutItem { Text = "Restore Defaults", IsEnabled = Store.IsCustomized(preset) };
        restore.Click += (_, _) => Change(() =>
        {
            Store.RestoreDefaults(preset);
            Build();
        });
        more.Items.Add(restore);
        var moreButton = new Button { Content = ShellUi.Icon("ellipsis", 14), Flyout = more, Width = 36, Height = 32 }
            .Subtle(Theme, 6).Named($"{preset.Title()} options");
        SetColumn(moreButton, 3);
        buttons.Children.Add(moreButton);
        stack.Children.Add(buttons);

        var card = new Border
        {
            Child = stack,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            Background = Theme.Card,
            BorderBrush = active ? Theme.Accent : Theme.CardStroke,
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetName(card, $"{preset.Title()} preset{(active ? ", active" : "")}");
        return card;
    }

    private Border PreviewBanner(MosaicPresetID preset)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(ShellUi.Icon("eye", 16, Theme.AccentText));
        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(ShellUi.Text($"Previewing {preset.Title()}", 14, semibold: true));
        var detail = ShellUi.Text("The window around this page shows it now. Closing Settings without applying goes back.", 12, Theme.SecondaryText);
        detail.TextWrapping = TextWrapping.Wrap;
        texts.Children.Add(detail);
        SetColumn(texts, 1);
        grid.Children.Add(texts);
        var cancel = SettingsKit.Button("Cancel", () => Change(() =>
        {
            Store.EndPreview();
            Build();
        }));
        AutomationProperties.SetName(cancel, "Cancel preview");
        SetColumn(cancel, 2);
        grid.Children.Add(cancel);
        var apply = SettingsKit.Button("Apply", () => Change(() =>
        {
            Model.ApplyMosaic(preset);
            Build();
        }), accent: true);
        AutomationProperties.SetName(apply, $"Apply {preset.Title()}");
        SetColumn(apply, 3);
        grid.Children.Add(apply);
        var banner = new Border
        {
            Child = grid,
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(8),
            Background = Theme.AccentSubtle,
            Margin = new Thickness(0, 0, 0, 8),
        };
        AutomationProperties.SetLiveSetting(banner, AutomationLiveSetting.Polite);
        return banner;
    }

    // MARK: Colours

    private static readonly string[] Swatches =
        [HexColor.System, "#0078D4", "#1A73E8", "#7C6CF2", "#E8458B", "#FF1B2D", "#FF8A00", "#22A559", "#0FA3B1", "#6E6E73"];

    private FrameworkElement AccentPicker(string current)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var hex in Swatches)
        {
            var selected = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase);
            Brush fill = hex == HexColor.System
                ? new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new XamlGradientStop { Color = ShellTheme.C(0xFF, 0x3B, 0x30), Offset = 0 },
                        new XamlGradientStop { Color = ShellTheme.C(0xFF, 0xCC, 0x00), Offset = 0.33 },
                        new XamlGradientStop { Color = ShellTheme.C(0x34, 0xC7, 0x59), Offset = 0.66 },
                        new XamlGradientStop { Color = ShellTheme.C(0x00, 0x7A, 0xFF), Offset = 1 },
                    },
                }
                : new SolidColorBrush(ShellTheme.ColorFromHex(hex) ?? ShellTheme.C(0x80, 0x80, 0x80));
            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(selected ? 2 : 0),
                BorderBrush = Theme.PrimaryText,
                Content = new Ellipse { Width = selected ? 18 : 22, Height = selected ? 18 : 22, Fill = fill },
            }.Subtle(Theme, 14).Named(hex == HexColor.System ? "Windows accent colour" : $"Accent {hex}");
            swatch.BorderThickness = new Thickness(selected ? 2 : 0);
            swatch.BorderBrush = Theme.PrimaryText;
            AutomationProperties.SetItemStatus(swatch, selected ? "Selected" : "");
            swatch.Click += (_, _) => Change(() =>
            {
                Store.Update(l => l.Appearance.Accent = hex);
                Build();
            });
            panel.Children.Add(swatch);
        }
        panel.Children.Add(ColorButton(current == HexColor.System ? HexColor.String(Theme.AccentColor.R / 255.0, Theme.AccentColor.G / 255.0, Theme.AccentColor.B / 255.0) : current,
            hex => Update(l => l.Appearance.Accent = hex), "Custom accent colour"));
        return panel;
    }

    private FrameworkElement ColorButton(string current, Action<string> picked, string name)
    {
        var picker = new ColorPicker
        {
            Color = ShellTheme.ColorFromHex(current) ?? ShellTheme.C(0x80, 0x80, 0x80),
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
        };
        picker.ColorChanged += (_, e) => picked(HexColor.String(e.NewColor.R / 255.0, e.NewColor.G / 255.0, e.NewColor.B / 255.0));
        var swatch = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(picker.Color),
            BorderBrush = Theme.CardStroke,
            BorderThickness = new Thickness(1),
        };
        picker.ColorChanged += (_, e) => swatch.Background = new SolidColorBrush(e.NewColor);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(swatch);
        content.Children.Add(new TextBlock { Text = "Custom…" });
        return new Button { Content = content, Flyout = new Flyout { Content = picker } }.Named(name);
    }

    private static readonly (string Name, GradientSpec Spec)[] Gradients =
    [
        ("Lavender", new GradientSpec(GradientKind.Linear, 125, new("#C9BFF7", 0), new("#E7D6F5", 0.55), new("#BFD3F5", 1))),
        ("Sunset", new GradientSpec(GradientKind.Linear, 135, new("#FFB88C", 0), new("#FF8FA3", 0.5), new("#C79BFF", 1))),
        ("Ocean", new GradientSpec(GradientKind.Linear, 140, new("#A1C4FD", 0), new("#C2E9FB", 1))),
        ("Mint", new GradientSpec(GradientKind.Linear, 120, new("#B8F1D3", 0), new("#D9F6C4", 1))),
        ("Night", new GradientSpec(GradientKind.Linear, 160, new("#1B1024", 0), new("#0F0E15", 0.6), new("#24101A", 1))),
        ("Graphite", new GradientSpec(GradientKind.Linear, 90, new("#3A3D44", 0), new("#24262B", 1))),
    ];

    private FrameworkElement GradientSwatches(GradientSpec current)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var currentJson = new BackgroundSpec(BackgroundKind.Gradient, gradient: current).Clamped();
        foreach (var (name, spec) in Gradients)
        {
            var selected = new MosaicLayout { Appearance = new MosaicAppearance { Background = new BackgroundSpec(BackgroundKind.Gradient, gradient: spec) } }
                .ContentEquals(new MosaicLayout { Appearance = new MosaicAppearance { Background = currentJson } });
            var swatch = new Button
            {
                Width = 44,
                Height = 30,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = selected ? Theme.PrimaryText : Theme.CardStroke,
                Background = ShellBackgrounds.Gradient(spec, dark: false),
            }.Named($"{name} gradient", name);
            swatch.Resources["ButtonBackground"] = swatch.Background;
            swatch.Resources["ButtonBackgroundPointerOver"] = swatch.Background;
            swatch.Resources["ButtonBackgroundPressed"] = swatch.Background;
            swatch.Click += (_, _) => Change(() =>
            {
                Store.Update(l => l.Appearance.Background.Gradient = spec.Clone());
                Build();
            });
            panel.Children.Add(swatch);
        }
        return panel;
    }
}
