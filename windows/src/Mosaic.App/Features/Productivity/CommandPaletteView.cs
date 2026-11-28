using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Windows.System;

namespace Mosaic.App.Features.Productivity;

/// <summary>
/// Shows the command palette above everything in the window, in a popup sized to the window
/// (independent of the shell's layout), and hides Gecko's page while it is open.
/// </summary>
internal sealed class CommandPaletteHost(BrowserModel model, Window window)
{
    public const string CoverId = "productivity.palette";

    private readonly CommandPalette palette = model.Productivity.Palette;
    private Popup? popup;
    private CommandPaletteView? view;

    public void Attach() => palette.PropertyChanged += PaletteChanged;

    private void PaletteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommandPalette.IsPresented)) return;
        if (palette.IsPresented) Open();
        else Close();
    }

    private void Open()
    {
        if (window.Content?.XamlRoot is not { } xamlRoot) return;
        FeatureMounts.CoverPage(CoverId, true);
        view ??= new CommandPaletteView(model);
        popup ??= new Popup { Child = view };
        popup.XamlRoot = xamlRoot;
        Resize(xamlRoot);
        xamlRoot.Changed -= RootChanged;
        xamlRoot.Changed += RootChanged;
        popup.IsOpen = true;
        view.Shown();
    }

    private void Close()
    {
        if (popup is not null)
        {
            popup.IsOpen = false;
            if (popup.XamlRoot is { } root) root.Changed -= RootChanged;
        }
        FeatureMounts.CoverPage(CoverId, false);
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize(sender);

    private void Resize(XamlRoot root)
    {
        if (view is null) return;
        view.Width = root.Size.Width;
        view.Height = root.Size.Height;
    }
}

/// <summary>The palette itself: a search field over a list of results, on Acrylic. Port of <c>CommandPaletteOverlay.swift</c>.</summary>
internal sealed class CommandPaletteView : Grid
{
    private readonly BrowserModel model;
    private readonly CommandPalette palette;
    private readonly TextBox search;
    private readonly ListView list;
    private readonly TextBlock empty;
    private readonly TextBlock count;
    private bool updating;

    public CommandPaletteView(BrowserModel model)
    {
        this.model = model;
        palette = model.Productivity.Palette;

        // Dims the window and closes on a click outside.
        var smoke = new Border { Background = ProductivityStyle.Smoke };
        smoke.Tapped += (_, _) => palette.Dismiss();
        AutomationProperties.SetAccessibilityView(smoke, AccessibilityView.Raw);
        Children.Add(smoke);

        search = new TextBox
        {
            PlaceholderText = "Search actions, tabs, bookmarks, history, settings",
            FontSize = 18,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(search, "Command palette search");
        search.TextChanged += (_, _) =>
        {
            if (!updating) palette.Query = search.Text;
        };

        var field = new Grid { Height = 56, Padding = new Thickness(16, 0, 12, 0), ColumnSpacing = 10 };
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = Icons.Make("command", 16);
        glyph.Foreground = ProductivityStyle.SecondaryText;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        field.Children.Add(glyph);
        SetColumn(search, 1);
        field.Children.Add(search);

        list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 400,
            Padding = new Thickness(4),
        };
        AutomationProperties.SetName(list, "Results");
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: PaletteAction action }) palette.Perform(action);
        };
        list.SelectionChanged += (_, _) =>
        {
            if (!updating && list.SelectedIndex >= 0) palette.Selection = list.SelectedIndex;
        };

        empty = new TextBlock
        {
            Text = "No matches",
            Foreground = ProductivityStyle.SecondaryText,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        count = new TextBlock { Foreground = ProductivityStyle.SecondaryText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var footer = new Grid { Height = 30, Padding = new Thickness(14, 0, 14, 0), Background = ProductivityStyle.Subtle };
        var hints = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        hints.Children.Add(Hint("↑↓", "Select"));
        hints.Children.Add(Hint("Enter", "Open"));
        hints.Children.Add(Hint("Esc", "Close"));
        footer.Children.Add(hints);
        count.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(count);
        AutomationProperties.SetAccessibilityView(footer, AccessibilityView.Raw);

        var stack = new StackPanel();
        stack.Children.Add(field);
        stack.Children.Add(new Border { Height = 1, Background = ProductivityStyle.CardStroke });
        stack.Children.Add(list);
        stack.Children.Add(empty);
        stack.Children.Add(footer);

        var panel = new Border
        {
            Child = stack,
            MaxWidth = 640,
            Margin = new Thickness(24, 64, 24, 24),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = ProductivityStyle.OverlayCorners,
            Background = ProductivityStyle.TransientSurface,
            BorderBrush = ProductivityStyle.CardStroke,
            BorderThickness = new Thickness(1),
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 32),
        };
        AutomationProperties.SetName(panel, "Command palette");
        Children.Add(panel);

        // Arrow keys, Enter and Escape go to the palette whatever has focus inside it.
        panel.PreviewKeyDown += PanelKeyDown;
        palette.PropertyChanged += PaletteChanged;
    }

    private static StackPanel Hint(string key, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new Border
        {
            Background = ProductivityStyle.Subtle,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 0, 5, 1),
            Child = new TextBlock { Text = key, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
        });
        row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = ProductivityStyle.SecondaryText });
        return row;
    }

    /// <summary>The popup opened: show the current results and take the keyboard (not in automation runs).</summary>
    public void Shown()
    {
        updating = true;
        search.Text = palette.Query;
        updating = false;
        Fill();
        if (model.AllowsFocusChanges) search.Focus(FocusState.Programmatic);
    }

    private void PaletteChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CommandPalette.Results):
                Fill();
                break;
            case nameof(CommandPalette.Selection):
                Select();
                break;
            case nameof(CommandPalette.Query) when search.Text != palette.Query:
                updating = true;
                search.Text = palette.Query;
                updating = false;
                break;
        }
    }

    private void Fill()
    {
        updating = true;
        list.Items.Clear();
        foreach (var action in palette.Results) list.Items.Add(Row(action));
        updating = false;
        empty.Visibility = palette.Results.Count == 0 && palette.Query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        count.Text = palette.Results.Count == 1 ? "1 result" : $"{palette.Results.Count} results";
        Select();
    }

    private void Select()
    {
        if (palette.Selection < 0 || palette.Selection >= list.Items.Count) return;
        updating = true;
        list.SelectedIndex = palette.Selection;
        updating = false;
        list.ScrollIntoView(list.Items[palette.Selection]);
    }

    private static FrameworkElement Row(PaletteAction action)
    {
        var row = new Grid { Tag = action, MinHeight = 40, ColumnSpacing = 12, Padding = new Thickness(2, 4, 2, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = Icons.Make(action.Icon, 14);
        row.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = ProductivityStyle.Subtle,
            Child = icon,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = action.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        if (!string.IsNullOrEmpty(action.Subtitle))
        {
            text.Children.Add(new TextBlock
            {
                Text = action.Subtitle,
                FontSize = 12,
                Foreground = ProductivityStyle.SecondaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
        }
        SetColumn(text, 1);
        row.Children.Add(text);

        var trailing = action.Shortcut?.DisplayString ?? action.Kind.Label();
        if (trailing is not null)
        {
            var label = new TextBlock
            {
                Text = trailing,
                FontSize = 12,
                Foreground = ProductivityStyle.SecondaryText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            SetColumn(label, 2);
            row.Children.Add(label);
        }
        var spoken = action.Title + (action.Subtitle is { Length: > 0 } s ? ", " + s : "")
            + (action.Shortcut is { } shortcut ? ", " + shortcut.SpokenString : action.Kind.Label() is { } kind ? ", " + kind : "");
        AutomationProperties.SetName(row, spoken);
        return row;
    }

    private void PanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        switch (e.Key)
        {
            case VirtualKey.Up:
                palette.MoveSelection(-1);
                break;
            case VirtualKey.Down:
                palette.MoveSelection(1);
                break;
            case VirtualKey.Enter:
                palette.PerformSelection();
                break;
            case VirtualKey.Escape:
                palette.Dismiss();
                break;
            case VirtualKey.N when ctrl:
                palette.MoveSelection(1);
                break;
            case VirtualKey.P when ctrl:
                palette.MoveSelection(-1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
