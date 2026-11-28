using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Tabs;
using Windows.UI;

namespace Mosaic.App.Features.Tabs;

/// <summary>Colours, cards and rows shared by the tabs area's views (Windows 11 settings look).</summary>
internal static class TabsStyle
{
    /// <summary>A theme brush from <c>XamlControlsResources</c>, or <paramref name="fallback"/> when it is missing.</summary>
    public static Brush Resource(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);

    public static Brush Secondary => Resource("TextFillColorSecondaryBrush", Color.FromArgb(0xC0, 0x80, 0x80, 0x80));
    public static Brush Card => Resource("CardBackgroundFillColorDefaultBrush", Color.FromArgb(0x10, 0x80, 0x80, 0x80));
    public static Brush CardStroke => Resource("CardStrokeColorDefaultBrush", Color.FromArgb(0x20, 0x80, 0x80, 0x80));
    public static Brush Accent => Resource("AccentFillColorDefaultBrush", Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    public static Brush Subtle => Resource("SubtleFillColorSecondaryBrush", Color.FromArgb(0x18, 0x80, 0x80, 0x80));

    /// <summary>Acrylic for transient surfaces, solid when transparency is off (the brush handles the fallback).</summary>
    public static Brush Flyout => Resource("AcrylicInAppFillColorDefaultBrush", Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B));

    public static SolidColorBrush GroupBrush(TabGroupColor color)
    {
        var (r, g, b) = color.Rgb();
        return new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
    }

    /// <summary>The engine dot (orange Mozilla, blue Chromium), as in the tab strip.</summary>
    public static SolidColorBrush EngineBrush(EngineKind engine) =>
        new(engine == EngineKind.Gecko ? Color.FromArgb(0xFF, 0xFF, 0x73, 0x1A) : Color.FromArgb(0xFF, 0x33, 0x85, 0xF5));

    public static bool HasIcon(string name) => Icons.Has(name);

    /// <summary>The icon, or a short text in its place for a name the icon table lacks.</summary>
    public static FrameworkElement Icon(string name, string fallbackText, double size = 14) =>
        HasIcon(name)
            ? Icons.Make(name, size)
            : new TextBlock { Text = fallbackText, FontSize = size * 0.85, VerticalAlignment = VerticalAlignment.Center };

    public static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    public static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Margin = new Thickness(2, 18, 0, 6),
    };

    public static TextBlock Footnote(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Secondary,
        FontSize = 12,
        Margin = new Thickness(2, 6, 0, 0),
    };

    /// <summary>A settings card: title (and description) on the left, the control on the right.</summary>
    public static Border Row(string title, string? description, UIElement? control)
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 48 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description is not null)
        {
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap });
        }
        grid.Children.Add(text);
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, 1);
            grid.Children.Add(element);
            if (AutomationProperties.GetName(element).Length == 0) AutomationProperties.SetName(element, title);
        }
        return CardOf(grid);
    }

    public static Border CardOf(UIElement child) => new()
    {
        Background = Card,
        BorderBrush = CardStroke,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(16, 8, 16, 8),
        Margin = new Thickness(0, 0, 0, 3),
        Child = child,
    };

    /// <summary>A floating card for overlays: acrylic, rounded, with a shadow and a hairline border.</summary>
    public static Border FloatingCard(UIElement child, double cornerRadius = 8) => new()
    {
        Background = Flyout,
        BorderBrush = CardStroke,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(cornerRadius),
        Translation = new global::System.Numerics.Vector3(0, 0, 32),
        Shadow = new ThemeShadow(),
        Child = child,
    };

    /// <summary>The page every settings page of this area is laid out in.</summary>
    public static ScrollViewer Page(string title, StackPanel content)
    {
        content.Padding = new Thickness(24, 12, 24, 24);
        content.MaxWidth = 860;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.Children.Insert(0, new TextBlock
        {
            Text = title,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 4),
        });
        return new ScrollViewer { Content = content, HorizontalScrollMode = ScrollMode.Disabled };
    }

    public static string Minutes(int minutes)
    {
        if (minutes < 60) return $"{minutes} minutes";
        var hours = minutes / 60;
        return minutes % 60 == 0 ? (hours == 1 ? "1 hour" : $"{hours} hours") : $"{minutes} minutes";
    }

    public static string Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.UtcNow - when;
        if (elapsed < TimeSpan.FromSeconds(60)) return "just now";
        if (elapsed < TimeSpan.FromMinutes(60)) return Plural((int)elapsed.TotalMinutes, "minute", "minutes") + " ago";
        if (elapsed < TimeSpan.FromHours(24)) return Plural((int)elapsed.TotalHours, "hour", "hours") + " ago";
        return Plural((int)elapsed.TotalDays, "day", "days") + " ago";
    }

    public static Color Transparent => Colors.Transparent;
}
