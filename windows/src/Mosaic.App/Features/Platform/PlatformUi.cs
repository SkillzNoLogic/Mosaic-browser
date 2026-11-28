using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;

namespace Mosaic.App.Features.Platform;

/// <summary>
/// Small building blocks shared by the platform area's settings pages (code-only WinUI: no XAML),
/// the counterparts of <c>PlatformSettingsComponents.swift</c>: section headers, cards, notes,
/// status badges, capability rows, and a page that re-renders parts of itself when the model changes.
/// </summary>
internal static class PlatformUi
{
    public const double PageMaxWidth = 860;

    public static Brush Brush(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(fallback);

    public static Style? Style(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    public static Brush Secondary => Brush("TextFillColorSecondaryBrush", Windows.UI.Color.FromArgb(0xFF, 0x60, 0x60, 0x60));
    public static Brush Critical => Brush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
    public static Brush Caution => Brush("SystemFillColorCautionBrush", Windows.UI.Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00));
    public static Brush Success => Brush("SystemFillColorSuccessBrush", Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F));

    public static TextBlock Title(string text) => new()
    {
        Text = text,
        Style = Style("TitleTextBlockStyle"),
        Margin = new Thickness(0, 0, 0, 4),
    };

    public static TextBlock Header(string text)
    {
        var header = new TextBlock { Text = text, Style = Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 18, 0, 6) };
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return header;
    }

    public static TextBlock Text(string text, bool strong = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Style = Style(strong ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle"),
    };

    /// <summary>Secondary explanatory text (SwiftUI's SettingsNote).</summary>
    public static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Secondary,
        Style = Style("CaptionTextBlockStyle"),
        Margin = new Thickness(0, 2, 0, 2),
    };

    /// <summary>A Fluent card: the grouped-form row of Windows Settings.</summary>
    public static Border Card(UIElement content) => new()
    {
        Child = content,
        Padding = new Thickness(16, 12, 16, 12),
        Margin = new Thickness(0, 0, 0, 4),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        Background = Brush("CardBackgroundFillColorDefaultBrush", Windows.UI.Color.FromArgb(0x0D, 0x80, 0x80, 0x80)),
        BorderBrush = Brush("CardStrokeColorDefaultBrush", Windows.UI.Color.FromArgb(0x19, 0x00, 0x00, 0x00)),
    };

    public static StackPanel Column(double spacing = 6, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Row(double spacing = 8, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    /// <summary>Label on the left, control on the right.</summary>
    public static Grid Labeled(string label, UIElement control, string? note = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Column(2, Text(label));
        if (note is not null) text.Children.Add(Note(note));
        grid.Children.Add(text);
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(element, label);
        }
        Grid.SetColumn((FrameworkElement)control, 1);
        grid.Children.Add(control);
        return grid;
    }

    public enum Tone { Neutral, Positive, Warning, Negative }

    public static Border Badge(string text, Tone tone = Tone.Neutral)
    {
        var brush = tone switch
        {
            Tone.Positive => Success,
            Tone.Warning => Caution,
            Tone.Negative => Critical,
            _ => Secondary,
        };
        var badge = new Border
        {
            Child = new TextBlock { Text = text, Foreground = brush, Style = Style("CaptionTextBlockStyle") },
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 1, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(badge, text);
        return badge;
    }

    public enum Capability { Yes, No, Info }

    public static StackPanel CapabilityRow(Capability kind, string text)
    {
        var (icon, brush) = kind switch
        {
            Capability.Yes => ("checkmark.circle.fill", Success),
            Capability.No => ("minus.circle", Critical),
            _ => ("info.circle", Secondary),
        };
        var glyph = Icons.Make(icon, 14);
        glyph.Foreground = brush;
        glyph.VerticalAlignment = VerticalAlignment.Top;
        glyph.Margin = new Thickness(0, 3, 0, 0);
        var row = Row(8, glyph, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 });
        AutomationProperties.SetName(row, text);
        return row;
    }

    public static StackPanel Message(string text, Tone tone)
    {
        var (icon, brush) = tone switch
        {
            Tone.Positive => ("checkmark.circle.fill", Success),
            Tone.Warning => ("exclamationmark.triangle.fill", Caution),
            Tone.Negative => ("exclamationmark.triangle.fill", Critical),
            _ => ("info.circle", Secondary),
        };
        var glyph = Icons.Make(icon, 14);
        glyph.Foreground = brush;
        glyph.VerticalAlignment = VerticalAlignment.Top;
        glyph.Margin = new Thickness(0, 3, 0, 0);
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 640, Foreground = tone == Tone.Neutral ? Secondary : brush };
        var row = Row(8, glyph, label);
        AutomationProperties.SetName(row, text);
        AutomationProperties.SetLiveSetting(row, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        return row;
    }

    public static Button Button(string text, Action click, bool accent = false, string? help = null)
    {
        var button = new Button { Content = text };
        if (accent && Style("AccentButtonStyle") is { } style) button.Style = style;
        if (help is not null) ToolTipService.SetToolTip(button, help);
        button.Click += (_, _) => click();
        return button;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot? root, string title, string message, string confirm)
    {
        if (root is null) return false;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        // Mosaic draws over the page: hide Gecko's window while the dialog is up.
        FeatureMounts.CoverPage("platform.dialog", true);
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            FeatureMounts.CoverPage("platform.dialog", false);
        }
    }

    public static string Relative(DateTimeOffset? date)
    {
        if (date is not { } value) return "never";
        var age = DateTimeOffset.Now - value;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} minutes ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} hours ago"
            : age.TotalDays < 2 ? "yesterday"
            : value.LocalDateTime.ToString("d MMMM yyyy", global::System.Globalization.CultureInfo.CurrentCulture);
    }
}

/// <summary>
/// A settings page: a scrolling column whose sections re-render themselves when the objects they
/// show change. Subscriptions exist only while the page is in the window (no idle work, no leaks).
/// </summary>
internal abstract class PlatformPage : UserControl
{
    private readonly List<(INotifyPropertyChanged Source, Action Refresh)> watches = [];
    private readonly HashSet<Action> queued = [];
    private bool attached;

    protected PlatformPage(string title)
    {
        Content = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Padding = new Thickness(24, 16, 24, 32),
                MaxWidth = PlatformUi.PageMaxWidth,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 2,
                Children = { PlatformUi.Title(title) },
            },
        };
        AutomationProperties.SetName(this, title);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    protected StackPanel Body => (StackPanel)((ScrollViewer)Content).Content;

    /// <summary>A section whose content <paramref name="render"/> fills again whenever one of <paramref name="sources"/> changes.</summary>
    protected StackPanel Section(Action<StackPanel> render, params INotifyPropertyChanged?[] sources)
    {
        var panel = new StackPanel { Spacing = 6 };
        void Refresh()
        {
            panel.Children.Clear();
            render(panel);
        }
        Refresh();
        foreach (var source in sources)
        {
            if (source is not null) watches.Add((source, Refresh));
        }
        return panel;
    }

    private void Attach()
    {
        if (attached) return;
        attached = true;
        foreach (var (source, _) in watches.DistinctBy(watch => watch.Source)) source.PropertyChanged += Changed;
        foreach (var (_, refresh) in watches.DistinctBy(watch => watch.Refresh)) refresh();
    }

    private void Detach()
    {
        if (!attached) return;
        attached = false;
        foreach (var (source, _) in watches.DistinctBy(watch => watch.Source)) source.PropertyChanged -= Changed;
    }

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        foreach (var (source, refresh) in watches)
        {
            if (!ReferenceEquals(source, sender) || !queued.Add(refresh)) continue;
            // Coalesce bursts (download progress, several properties at once) into one render.
            var dispatcher = DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null || !dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => Run(refresh))) Run(refresh);
        }
    }

    private void Run(Action refresh)
    {
        queued.Remove(refresh);
        if (attached) refresh();
    }
}

/// <summary>
/// The Gecko engine's status changes as property changes, for <see cref="PlatformPage"/>. It is
/// subscribed to the engine only while somebody listens, so a closed page leaves nothing behind.
/// </summary>
internal sealed class GeckoStatusSignal(Mosaic.Core.Gecko.IGeckoEngine gecko) : INotifyPropertyChanged
{
    private PropertyChangedEventHandler? handlers;

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add
        {
            if (handlers is null) gecko.StatusChanged += Raise;
            handlers += value;
        }
        remove
        {
            handlers -= value;
            if (handlers is null) gecko.StatusChanged -= Raise;
        }
    }

    private void Raise() => handlers?.Invoke(this, new PropertyChangedEventArgs(nameof(gecko.Status)));
}

/// <summary>A change a page announces to its own sections (expanding a row, say).</summary>
internal sealed class Signal : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
