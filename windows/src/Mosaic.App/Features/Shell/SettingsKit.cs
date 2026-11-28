using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// Building blocks for a settings page in the Windows 11 Settings style: section headings and
/// cards with a title and description on the left and the control on the right.
/// </summary>
internal sealed class SettingsKit
{
    private readonly ShellTheme theme;

    public StackPanel Stack { get; } = new() { Spacing = 4, MaxWidth = 960, Padding = new Thickness(28, 18, 28, 32), HorizontalAlignment = HorizontalAlignment.Stretch };

    public SettingsKit(ShellTheme theme) => this.theme = theme;

    public ScrollViewer Scroller() => new() { Content = Stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled };

    public TextBlock Section(string title)
    {
        var text = ShellUi.Text(title, 14, theme.PrimaryText, semibold: true);
        text.Margin = new Thickness(2, Stack.Children.Count == 0 ? 0 : 22, 0, 6);
        AutomationProperties.SetHeadingLevel(text, AutomationHeadingLevel.Level2);
        Stack.Children.Add(text);
        return text;
    }

    public Border Card(string title, string? description, FrameworkElement? control, UIElement? below = null)
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var heading = ShellUi.Text(title, 14, theme.PrimaryText);
        heading.TextWrapping = TextWrapping.Wrap;
        texts.Children.Add(heading);
        if (description is { Length: > 0 })
        {
            var note = ShellUi.Text(description, 12, theme.SecondaryText);
            note.TextWrapping = TextWrapping.Wrap;
            note.TextTrimming = TextTrimming.None;
            texts.Children.Add(note);
        }
        grid.Children.Add(texts);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            if (string.IsNullOrEmpty(AutomationProperties.GetName(control))) AutomationProperties.SetName(control, title);
            if (description is { Length: > 0 }) AutomationProperties.SetHelpText(control, description);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        UIElement child = grid;
        if (below is not null)
        {
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(grid);
            stack.Children.Add(below);
            child = stack;
        }
        var card = new Border
        {
            Child = child,
            Background = theme.Card,
            BorderBrush = theme.CardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 16, 12),
        };
        Stack.Children.Add(card);
        return card;
    }

    public TextBlock Note(string text)
    {
        var note = ShellUi.Text(text, 12, theme.SecondaryText);
        note.TextWrapping = TextWrapping.Wrap;
        note.TextTrimming = TextTrimming.None;
        note.Margin = new Thickness(2, 4, 2, 0);
        Stack.Children.Add(note);
        return note;
    }

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Title)> options, T current, Action<T> changed, string name)
    {
        var list = options.ToList();
        var box = new ComboBox { MinWidth = 180 };
        foreach (var (_, title) in list) box.Items.Add(title);
        box.SelectedIndex = Math.Max(0, list.FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, current)));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0) changed(list[box.SelectedIndex].Value);
        };
        AutomationProperties.SetName(box, name);
        return box;
    }

    public static RadioButtons Radios<T>(IEnumerable<(T Value, string Title)> options, T current, Action<T> changed, string name)
    {
        var list = options.ToList();
        var radios = new RadioButtons { MaxColumns = list.Count };
        foreach (var (_, title) in list) radios.Items.Add(title);
        radios.SelectedIndex = Math.Max(0, list.FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, current)));
        radios.SelectionChanged += (_, _) =>
        {
            if (radios.SelectedIndex >= 0) changed(list[radios.SelectedIndex].Value);
        };
        AutomationProperties.SetName(radios, name);
        return radios;
    }

    public static ToggleSwitch Toggle(bool on, Action<bool> changed, string name, string onText = "On", string offText = "Off")
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = onText, OffContent = offText, MinWidth = 0 };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        AutomationProperties.SetName(toggle, name);
        return toggle;
    }

    public static Slider Slider(double value, double min, double max, double step, Action<double> changed, string name)
    {
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value, Width = 220, SnapsTo = SliderSnapsTo.StepValues };
        slider.ValueChanged += (_, e) => changed(e.NewValue);
        AutomationProperties.SetName(slider, name);
        return slider;
    }

    public static Button Button(string text, Action action, bool accent = false)
    {
        var button = new Button { Content = text };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style s) button.Style = s;
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, text.TrimEnd('…'));
        return button;
    }
}
