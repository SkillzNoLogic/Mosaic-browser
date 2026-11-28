using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;

namespace Mosaic.App.Features.Shell;

/// <summary>Small builders shared by the shell's views (code-only WinUI).</summary>
internal static class ShellUi
{
    /// <summary>Glyphs the shell needs beyond the system area's starter table (Segoe Fluent Icons).</summary>
    internal static void RegisterGlyphs()
    {
        void Add(string name, string glyph)
        {
            if (Icons.Glyph(name) == Icons.Glyph("\u0000missing")) Icons.Add(name, glyph);
        }
        Add("sidebar.right", "\uE89F");
        Add("sidebar.squares.left", "\uE8A9");
        Add("square.stack", "\uE7C4");
        Add("shield.lefthalf.filled", "\uEA18");
        Add("puzzlepiece.extension", "\uEA86");
        Add("circle.lefthalf.filled", "\uE793");
        Add("text.magnifyingglass", "\uF6FA");
        Add("book", "\uE728");
        Add("clock", "\uE81C");
        Add("eyeglasses", "\uE7BC");
        Add("arrow.down.doc", "\uE896");
        Add("globe", "\uE774");
        Add("square.grid.2x2", "\uF0E2");
        Add("square.on.square", "\uE8A9");
        Add("hand.raised", "\uE72E");
        Add("key", "\uE8D7");
        Add("command", "\uE765");
        Add("accessibility", "\uE776");
        Add("gauge", "\uEC4A");
        Add("info.circle", "\uE946");
        Add("engine", "\uE950");
        Add("eye", "\uE7B3");
        Add("checkmark", "\uE73E");
        Add("paintbrush", "\uE790");
        Add("window", "\uE737");
        Add("person", "\uE77B");
        Add("textformat.size", "\uE8E9");
        Add("arrow.counterclockwise", "\uE777");
    }

    public static T Named<T>(this T element, string name, string? help = null) where T : FrameworkElement
    {
        AutomationProperties.SetName(element, name);
        if (help is not null)
        {
            ToolTipService.SetToolTip(element, help);
            AutomationProperties.SetHelpText(element, help);
        }
        return element;
    }

    public static FontIcon Icon(string name, double size, Brush? foreground = null)
    {
        var icon = Icons.Make(name, size);
        if (foreground is not null) icon.Foreground = foreground;
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return icon;
    }

    public static TextBlock Text(string text, double size = 13, Brush? foreground = null, bool semibold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = foreground,
        FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A borderless button whose hover and pressed fills come from the theme (toolbar style).</summary>
    public static T Subtle<T>(this T button, ShellTheme theme, double radius) where T : ButtonBase
    {
        button.Background = theme.Transparent;
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(radius);
        button.Padding = new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        var r = button.Resources;
        r["ButtonBackground"] = theme.Transparent;
        r["ButtonBackgroundPointerOver"] = theme.Hover;
        r["ButtonBackgroundPressed"] = theme.Pressed;
        r["ButtonBackgroundDisabled"] = theme.Transparent;
        r["ButtonBorderBrush"] = theme.Transparent;
        r["ButtonBorderBrushPointerOver"] = theme.Transparent;
        r["ButtonBorderBrushPressed"] = theme.Transparent;
        r["ButtonBorderBrushDisabled"] = theme.Transparent;
        r["ButtonForeground"] = theme.PrimaryText;
        r["ButtonForegroundPointerOver"] = theme.PrimaryText;
        r["ButtonForegroundPressed"] = theme.SecondaryText;
        r["ButtonForegroundDisabled"] = theme.DisabledText;
        r["ToggleButtonBackground"] = theme.Transparent;
        r["ToggleButtonBackgroundPointerOver"] = theme.Hover;
        r["ToggleButtonBackgroundPressed"] = theme.Pressed;
        r["ToggleButtonBackgroundChecked"] = theme.AccentSubtle;
        r["ToggleButtonBackgroundCheckedPointerOver"] = theme.AccentSubtle;
        r["ToggleButtonBackgroundCheckedPressed"] = theme.Pressed;
        r["ToggleButtonForegroundChecked"] = theme.AccentText;
        r["ToggleButtonForegroundCheckedPointerOver"] = theme.AccentText;
        r["ToggleButtonBorderBrush"] = theme.Transparent;
        r["ToggleButtonBorderBrushChecked"] = theme.Transparent;
        r["ToggleButtonBorderBrushPointerOver"] = theme.Transparent;
        r["ToggleButtonBorderBrushCheckedPointerOver"] = theme.Transparent;
        FocusRing(button, theme);
        return button;
    }

    public static void FocusRing(Control control, ShellTheme theme)
    {
        if (!theme.ProminentFocusRings) return;
        control.FocusVisualPrimaryBrush = theme.Accent;
        control.FocusVisualPrimaryThickness = new Thickness(3);
        control.FocusVisualSecondaryThickness = new Thickness(1);
    }

    /// <summary>A toolbar icon button with a tooltip naming its shortcut.</summary>
    public static Button IconButton(ShellTheme theme, string symbol, string label, string? shortcut, Action action, bool enabled = true)
    {
        var m = theme.Metrics;
        var button = new Button
        {
            Content = Icon(symbol, m.Icon),
            Width = m.Button,
            Height = m.Button,
            IsEnabled = enabled,
        }.Subtle(theme, Math.Clamp(theme.CornerRadius, 4, 10));
        button.Click += (_, _) => action();
        return button.Named(label, shortcut is null ? label : $"{label} ({shortcut})");
    }

    /// <summary>Hides the Gecko page (a native window above WinUI content) while the flyout is open.</summary>
    public static T CoversPage<T>(this T flyout, string id) where T : FlyoutBase
    {
        flyout.Opened += (_, _) => FeatureMounts.CoverPage(id, true);
        flyout.Closed += (_, _) => FeatureMounts.CoverPage(id, false);
        return flyout;
    }

    public static Border Divider(ShellTheme theme, bool vertical) => new()
    {
        Background = theme.Divider,
        Width = vertical ? 1 : double.NaN,
        Height = vertical ? double.NaN : 1,
    };

    /// <summary>The gap that drags the window in a caption row and just spaces elsewhere.</summary>
    public static Border Gap(double minWidth = 8) => new() { MinWidth = minWidth, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
}
