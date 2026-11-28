using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mosaic.App.Features.Privacy;

/// <summary>Building blocks for the privacy area's views, in code (there is no XAML).</summary>
internal static class PrivacyUi
{
    /// <summary>The colour private tabs are drawn in (macOS's <c>PrivateBrowsingBadge.tint</c>).</summary>
    public static readonly Color PrivateTint = Color.FromArgb(0xFF, 0x6B, 0x40, 0xBF);

    public static Brush Brush(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush ? brush : new SolidColorBrush(fallback);

    public static Brush Secondary => Brush("TextFillColorSecondaryBrush", Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
    public static Brush Critical => Brush("SystemFillColorCriticalBrush", Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
    public static Brush Caution => Brush("SystemFillColorCautionBrush", Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00));

    public static TextBlock Text(string text, double size = 14, Brush? foreground = null, bool bold = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (foreground is not null) block.Foreground = foreground;
        if (bold) block.FontWeight = FontWeights.SemiBold;
        return block;
    }

    public static TextBlock Caption(string text) => Text(text, 12, Secondary);

    /// <summary>A settings card: a titled group of rows with an optional footer, as the other settings pages draw them.</summary>
    public static (Border Card, StackPanel Rows) Section(StackPanel page, string title, string? footer = null)
    {
        page.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 18, 0, 6),
        });
        var rows = new StackPanel { Spacing = 10 };
        var card = new Border
        {
            Background = Brush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(0x0D, 0x80, 0x80, 0x80)),
            BorderBrush = Brush("CardStrokeColorDefaultBrush", Color.FromArgb(0x19, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Child = rows,
        };
        AutomationProperties.SetName(card, title);
        page.Children.Add(card);
        if (footer is not null)
        {
            var note = Caption(footer);
            note.Margin = new Thickness(2, 6, 0, 0);
            page.Children.Add(note);
        }
        return (card, rows);
    }

    /// <summary>A switch with a title and an explanation under it.</summary>
    public static ToggleSwitch Toggle(Panel rows, string title, string detail, bool isOn, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsOn = isOn, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title);
        AutomationProperties.SetHelpText(toggle, detail);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(title));
        text.Children.Add(Caption(detail));
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        rows.Children.Add(grid);
        return toggle;
    }

    /// <summary>A row with content on the left and controls on the right.</summary>
    public static Grid Row(UIElement leading, params UIElement[] trailing)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(leading);
        for (var i = 0; i < trailing.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn((FrameworkElement)trailing[i], i + 1);
            if (trailing[i] is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(trailing[i]);
        }
        return grid;
    }

    public static Button Button(string title, Action click, string? accessibleName = null, bool accent = false)
    {
        var button = new Button { Content = title };
        if (accent && Application.Current?.Resources.TryGetValue("AccentButtonStyle", out var style) == true) button.Style = (Style)style;
        AutomationProperties.SetName(button, accessibleName ?? title);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Calls <paramref name="update"/> now and whenever <paramref name="source"/> announces one of <paramref name="properties"/> (any, if none). Dispose to stop.</summary>
    public static IDisposable Watch(INotifyPropertyChanged source, Action update, params string[] properties)
    {
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName) || properties.Length == 0 || Array.IndexOf(properties, e.PropertyName) >= 0) update();
        };
        source.PropertyChanged += handler;
        update();
        return new Subscription(() => source.PropertyChanged -= handler);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;

        public void Dispose()
        {
            dispose?.Invoke();
            dispose = null;
        }
    }

    /// <summary>A glyph from the Fluent icon font (Segoe Fluent Icons; Segoe MDL2 Assets on Windows 10).</summary>
    public static FontIcon Icon(string glyph, double size = 16) =>
        new() { Glyph = glyph, FontSize = size, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };

    /// <summary>
    /// The mark of a private tab: shell views draw it on private tabs' chips and the address field
    /// (<c>tab.IsPrivate</c>), and the privacy settings use it too.
    /// </summary>
    public static FrameworkElement PrivateBadge(bool compact = false)
    {
        var size = compact ? 16 : 24;
        var badge = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2.0),
            Background = new SolidColorBrush(PrivateTint),
            // "Hide" (an eye with a stroke): Segoe Fluent Icons E9A9 / MDL2 ED1A.
            Child = new FontIcon
            {
                Glyph = "",
                FontSize = compact ? 9 : 13,
                Foreground = new SolidColorBrush(Colors.White),
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            },
        };
        AutomationProperties.SetName(badge, "Private tab");
        ToolTipService.SetToolTip(badge, "Private tab: nothing is saved after it closes");
        return badge;
    }

    private static class Colors
    {
        public static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }
}
