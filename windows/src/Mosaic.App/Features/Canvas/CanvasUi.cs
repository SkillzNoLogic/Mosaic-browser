using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Canvas.Interim;
using Windows.UI;

namespace Mosaic.App.Features.Canvas;

/// <summary>Small builders for the canvas views (UI in code, Fluent theme resources).</summary>
internal static class CanvasUi
{
    public static Brush Brush(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(fallback);

    public static Brush Secondary => Brush("TextFillColorSecondaryBrush", Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
    public static Brush Accent => Brush("AccentFillColorDefaultBrush", Color.FromArgb(0xFF, 0x00, 0x67, 0xC0));
    public static Brush CardBackground => Brush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(0x10, 0x80, 0x80, 0x80));
    public static Brush CardStroke => Brush("CardStrokeColorDefaultBrush", Color.FromArgb(0x30, 0x80, 0x80, 0x80));
    public static Brush Subtle => Brush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x10, 0x80, 0x80, 0x80));
    public static Brush Critical => Brush("SystemFillColorCriticalBrush", Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
    public static Brush CriticalBackground => Brush("SystemFillColorCriticalBackgroundBrush", Color.FromArgb(0x30, 0xC4, 0x2B, 0x1C));
    public static Brush Caution => Brush("SystemFillColorCautionBrush", Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00));

    public static TextBlock Text(string text, double size = 14, Brush? brush = null, bool bold = false, bool wrap = true)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (brush is not null) block.Foreground = brush;
        if (bold) block.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return block;
    }

    public static TextBlock Title(string text) => Text(text, 20, bold: true);

    public static TextBlock Heading(string text) => Text(text, 15, bold: true);

    public static TextBlock Caption(string text) => Text(text, 12, Secondary);

    /// <summary>A button with a Fluent icon and a label (or icon only), named for UI Automation.</summary>
    public static Button Button(string label, string? icon, Action click, bool iconOnly = false, string? tooltip = null)
    {
        object content = label;
        if (icon is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(Icons.Make(icon, 14));
            if (!iconOnly) row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            content = row;
        }
        var button = new Button { Content = content };
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, tooltip ?? label);
        return button;
    }

    public static Button Accented(Button button)
    {
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style s) button.Style = s;
        return button;
    }

    public static StackPanel Row(double spacing = 8, params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    public static StackPanel Column(double spacing = 8, params UIElement[] children)
    {
        var column = new StackPanel { Spacing = spacing };
        foreach (var child in children) column.Children.Add(child);
        return column;
    }

    public static Border Card(UIElement child, double padding = 12) => new()
    {
        Child = child,
        Padding = new Thickness(padding),
        CornerRadius = new CornerRadius(8),
        Background = CardBackground,
        BorderBrush = CardStroke,
        BorderThickness = new Thickness(1),
    };

    /// <summary>A labelled control, e.g. "Tabs [Top|Left|Right]".</summary>
    public static StackPanel Labelled(string label, UIElement control)
    {
        var row = Row(6, Text(label, 14, Secondary, wrap: false), control);
        if (control is FrameworkElement element) AutomationProperties.SetName(element, label);
        ((FrameworkElement)row.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    public static ComboBox Choice<T>(IReadOnlyList<T> values, T selected, Func<T, string> title, Action<T> chosen) where T : struct, Enum
    {
        var box = new ComboBox();
        foreach (var value in values) box.Items.Add(title(value));
        box.SelectedIndex = Math.Max(0, values.ToList().IndexOf(selected));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0 && !EqualityComparer<T>.Default.Equals(values[box.SelectedIndex], selected)) chosen(values[box.SelectedIndex]);
        };
        return box;
    }

    /// <summary>A segmented choice built from toggle buttons (RadioButtons are vertical lists in WinUI).</summary>
    public static StackPanel Segmented<T>(string name, IReadOnlyList<(T Value, string Title)> options, T selected, Action<T> chosen)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        AutomationProperties.SetName(row, name);
        foreach (var (value, title) in options)
        {
            var isOn = EqualityComparer<T>.Default.Equals(value, selected);
            var button = new ToggleButton { Content = title, IsChecked = isOn, Padding = new Thickness(10, 4, 10, 4) };
            AutomationProperties.SetName(button, $"{name}: {title}");
            button.Click += (_, _) =>
            {
                button.IsChecked = true;
                if (!isOn) chosen(value);
            };
            row.Children.Add(button);
        }
        return row;
    }

    public static Slider Slider(string name, double min, double max, double value, Action<double> changed, double width = 140, double step = 0)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = width, VerticalAlignment = VerticalAlignment.Center };
        if (step > 0) slider.StepFrequency = step;
        else slider.StepFrequency = (max - min) / 200;
        AutomationProperties.SetName(slider, name);
        slider.ValueChanged += (_, e) => { if (Math.Abs(e.NewValue - e.OldValue) > 1e-9) changed(e.NewValue); };
        return slider;
    }

    public static SolidColorBrush ColorBrush(string hex, byte alpha = 255)
    {
        var c = HexColor.Components(hex) ?? (0.5, 0.5, 0.5, 1);
        return new SolidColorBrush(Color.FromArgb(alpha, (byte)Math.Round(c.Red * 255), (byte)Math.Round(c.Green * 255), (byte)Math.Round(c.Blue * 255)));
    }

    public static Color ToColor(Mosaic.Core.Canvas.ColorValue c, byte alpha = 255) =>
        Color.FromArgb(alpha, (byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255));

    /// <summary>Speaks text through UI Automation (Narrator), as macOS posts announcements.</summary>
    public static void Announce(UIElement element, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, "MosaicCanvas");
    }
}
