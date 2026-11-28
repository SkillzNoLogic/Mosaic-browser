using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Passwords;

namespace Mosaic.App.Features.Passwords;

/// <summary>Small building blocks shared by the password views (code-only WinUI, Fluent resources).</summary>
internal static class PasswordsUi
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    /// <summary>A theme brush from the Fluent resources, or the fallback key's, or a neutral grey.</summary>
    public static Brush Brush(string key, string? fallback = null)
    {
        var resources = Application.Current.Resources;
        if (resources.TryGetValue(key, out var value) && value is Brush brush) return brush;
        if (fallback is not null && resources.TryGetValue(fallback, out value) && value is Brush other) return other;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public static Style? Style(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    public static Button IconButton(string icon, string label, Action click, double size = 14)
    {
        var button = new Button
        {
            Content = Icons.Make(icon, size),
            Padding = new Thickness(6),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    public static void SetIcon(Button button, string icon, string label)
    {
        var size = button.Content is FontIcon old ? old.FontSize : 14;
        button.Content = Icons.Make(icon, size);
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = Style("CaptionTextBlockStyle"),
        Foreground = Brush("TextFillColorSecondaryBrush"),
    };

    public static void AddLabel(Grid grid, string text, int row) =>
        Place(grid, new TextBlock { Text = text, Foreground = Brush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center }, row, 0);

    public static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    public static string UsernameText(string username) => username.Length == 0 ? "No username" : username;

    /// <summary>A full-width row button for one saved login ("Fill alice").</summary>
    public static Button LoginButton(PasswordCredential credential, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var person = Icons.Make("person.crop.circle", 14);
        person.Foreground = Brush("TextFillColorSecondaryBrush");
        row.Children.Add(person);
        row.Children.Add(new TextBlock { Text = UsernameText(credential.Username), TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brush("SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, $"Fill {(credential.Username.Length == 0 ? "login without username" : credential.Username)}");
        button.Click += (_, _) => click();
        return button;
    }

    public static Brush StrengthBrush(PasswordStrength strength) => strength switch
    {
        PasswordStrength.Weak => Brush("SystemFillColorCautionBrush"),
        PasswordStrength.Fair => Brush("SystemFillColorAttentionBrush"),
        _ => Brush("SystemFillColorSuccessBrush"),
    };
}
