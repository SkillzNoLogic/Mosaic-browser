using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Windows.UI;

namespace Mosaic.App.Features.Productivity;

/// <summary>
/// Toolbar component "reader": switches reader view for the selected tab (either engine), and
/// opens Mosaic's reader controls (font, size, line width, spacing, background), which apply to
/// both engines' reader views.
/// </summary>
internal sealed class ReaderButton : StackPanel
{
    public const string CoverId = "productivity.readerSettings";

    private readonly BrowserModel model;
    private readonly ReaderMode reader;
    private readonly ToggleButton toggle;
    private readonly Button options;
    private readonly ReaderSettingsPanel panel;

    public ReaderButton(BrowserModel model)
    {
        this.model = model;
        reader = model.Productivity.Reader;
        Orientation = Orientation.Horizontal;
        Spacing = 2;

        toggle = new ToggleButton { Content = Icons.Make("doc.plaintext"), Padding = new Thickness(8, 6, 8, 6) };
        toggle.Click += (_, _) =>
        {
            model.ToggleReaderMode();
            Update();
        };
        Children.Add(toggle);

        panel = new ReaderSettingsPanel(reader);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        // A flyout is drawn over the page: Gecko's page window has to be hidden meanwhile.
        flyout.Opened += (_, _) => FeatureMounts.CoverPage(CoverId, true);
        flyout.Closed += (_, _) => FeatureMounts.CoverPage(CoverId, false);
        options = new Button { Content = new TextBlock { Text = "Aa", FontSize = 13 }, Padding = new Thickness(8, 5, 8, 5), Flyout = flyout };
        ToolTipService.SetToolTip(options, "Reader view settings");
        AutomationProperties.SetName(options, "Reader view settings");
        Children.Add(options);

        Loaded += (_, _) =>
        {
            model.PropertyChanged += ModelChanged;
            reader.Changed += ReaderChanged;
            Update();
        };
        Unloaded += (_, _) =>
        {
            model.PropertyChanged -= ModelChanged;
            reader.Changed -= ReaderChanged;
        };
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserModel.SelectedTab) or nameof(BrowserModel.Content) or nameof(BrowserModel.IsLoading)) Update();
    }

    private void ReaderChanged(BrowserTab? tab)
    {
        if (tab is null) panel.Update();
        if (tab is null || ReferenceEquals(tab, model.SelectedTab)) Update();
    }

    private void Update()
    {
        var tab = model.SelectedTab;
        var active = tab is not null && reader.IsActive(tab);
        var available = tab is not null && reader.IsAvailable(tab);
        toggle.IsChecked = active;
        toggle.IsEnabled = active || reader.CanToggle(tab);
        // Shown prominently when the page looks like an article, as Firefox and Edge do.
        toggle.Opacity = active || available ? 1 : 0.6;
        var shortcut = model.Productivity.Shortcuts.Combo(ShortcutCommands.ReaderView);
        var tip = active ? "Leave reader view" : available ? "Show reader view" : "Reader view (this page may not be an article)";
        ToolTipService.SetToolTip(toggle, shortcut is { } key ? $"{tip} ({key.DisplayString})" : tip);
        AutomationProperties.SetName(toggle, "Reader view");
        options.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Reader typography and colours, on Firefox's scales (the same in both engines).</summary>
internal sealed class ReaderSettingsPanel : StackPanel
{
    private readonly ReaderMode reader;
    private readonly RadioButton sans;
    private readonly RadioButton serif;
    private readonly TextBlock size;
    private readonly TextBlock width;
    private readonly TextBlock spacing;
    private readonly List<(ReaderTheme Theme, ToggleButton Button)> themes = [];
    private bool updating;

    public ReaderSettingsPanel(ReaderMode reader)
    {
        this.reader = reader;
        Spacing = 12;
        MinWidth = 260;

        var fonts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        sans = new RadioButton { Content = "Sans-serif", GroupName = "readerFont" };
        serif = new RadioButton { Content = "Serif", GroupName = "readerFont", FontFamily = new FontFamily("Georgia, Cambria, serif") };
        sans.Checked += (_, _) => Change(s => s with { Font = ReaderFont.Sans });
        serif.Checked += (_, _) => Change(s => s with { Font = ReaderFont.Serif });
        fonts.Children.Add(sans);
        fonts.Children.Add(serif);
        Children.Add(Labeled("Font", fonts));

        size = new TextBlock();
        Children.Add(Labeled("Text size", Stepper(size, "Smaller text", "Larger text", d => s => s with { FontSize = s.FontSize + d })));
        width = new TextBlock();
        Children.Add(Labeled("Line width", Stepper(width, "Narrower lines", "Wider lines", d => s => s with { ContentWidth = s.ContentWidth + d })));
        spacing = new TextBlock();
        Children.Add(Labeled("Line spacing", Stepper(spacing, "Less line spacing", "More line spacing", d => s => s with { LineHeight = s.LineHeight + d })));

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (theme, color) in new[]
        {
            (ReaderTheme.Light, Color.FromArgb(255, 255, 255, 255)),
            (ReaderTheme.Sepia, Color.FromArgb(255, 244, 236, 216)),
            (ReaderTheme.Dark, Color.FromArgb(255, 28, 27, 34)),
        })
        {
            var chosen = theme;
            var button = new ToggleButton
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(color),
                BorderBrush = ProductivityStyle.CardStroke,
                Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(button, $"{theme.Title()} background");
            AutomationProperties.SetName(button, $"{theme.Title()} background");
            button.Click += (_, _) => Change(s => s with { Theme = chosen });
            themes.Add((theme, button));
            swatches.Children.Add(button);
        }
        Children.Add(Labeled("Background", swatches));
        Update();
    }

    private static StackPanel Labeled(string label, UIElement control)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = ProductivityStyle.SecondaryText });
        stack.Children.Add(control);
        return stack;
    }

    private StackPanel Stepper(TextBlock value, string less, string more, Func<int, Func<ReaderSettings, ReaderSettings>> step)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var minus = new Button { Content = "−", MinWidth = 36 };
        var plus = new Button { Content = "+", MinWidth = 36 };
        AutomationProperties.SetName(minus, less);
        AutomationProperties.SetName(plus, more);
        ToolTipService.SetToolTip(minus, less);
        ToolTipService.SetToolTip(plus, more);
        minus.Click += (_, _) => Change(step(-1));
        plus.Click += (_, _) => Change(step(1));
        value.MinWidth = 24;
        value.TextAlignment = TextAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(minus);
        row.Children.Add(value);
        row.Children.Add(plus);
        return row;
    }

    private void Change(Func<ReaderSettings, ReaderSettings> edit)
    {
        if (updating) return;
        reader.Update(edit(reader.Settings));
        Update();
    }

    public void Update()
    {
        updating = true;
        var s = reader.Settings;
        sans.IsChecked = s.Font == ReaderFont.Sans;
        serif.IsChecked = s.Font == ReaderFont.Serif;
        size.Text = $"{s.FontSize}";
        width.Text = $"{s.ContentWidth}";
        spacing.Text = $"{s.LineHeight}";
        foreach (var (theme, button) in themes) button.IsChecked = theme == s.Theme;
        updating = false;
    }
}
