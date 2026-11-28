using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Windows.System;
using Windows.UI.Core;

namespace Mosaic.App.Features.Productivity;

/// <summary>
/// Settings › Shortcuts &amp; Gestures: every command with its default and current shortcut; click
/// a shortcut to record a new one. Port of <c>ShortcutsSettingsPane.swift</c>.
/// </summary>
internal sealed class ShortcutsSettingsPage : UserControl
{
    private readonly ShortcutManager shortcuts;
    private readonly StackPanel groups = new() { Spacing = 4 };
    private readonly TextBox filter;
    private readonly Button resetAll;
    private readonly TextBlock saveError;
    private string? recording;
    private (string CommandId, KeyCombo Combo, IReadOnlyList<string> Conflicts)? pending;
    private (string CommandId, string Text)? message;

    public ShortcutsSettingsPage(BrowserModel model)
    {
        shortcuts = model.Productivity.Shortcuts;

        var intro = new TextBlock
        {
            Text = "Every command is listed with its default shortcut. Select a shortcut to change it, then press the new keys: "
                + "Esc cancels, Backspace removes the shortcut. A shortcut needs Ctrl or Alt (function keys work alone). "
                + "Shortcuts also work while a Mozilla page has focus.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = ProductivityStyle.SecondaryText,
        };

        filter = new TextBox { PlaceholderText = "Filter commands or keys", MinWidth = 240 };
        AutomationProperties.SetName(filter, "Filter shortcuts");
        filter.TextChanged += (_, _) => Rebuild();

        resetAll = new Button { Content = "Restore all defaults…" };
        resetAll.Click += (_, _) => ConfirmResetAll();

        var toolbar = new Grid { ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(filter);
        Grid.SetColumn(resetAll, 1);
        toolbar.Children.Add(resetAll);

        saveError = new TextBlock { Foreground = ProductivityStyle.Critical, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var gestures = new StackPanel { Spacing = 4, Margin = new Thickness(0, 16, 0, 0) };
        gestures.Children.Add(Header("Mouse gestures"));
        gestures.Children.Add(new TextBlock { Text = "Mouse gestures are not available yet.", Foreground = ProductivityStyle.SecondaryText });

        var content = new StackPanel { Spacing = 12, Padding = new Thickness(24, 16, 24, 24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(new TextBlock { Text = "Shortcuts & Gestures", Style = TitleStyle() });
        content.Children.Add(intro);
        content.Children.Add(toolbar);
        content.Children.Add(saveError);
        content.Children.Add(groups);
        content.Children.Add(gestures);

        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // Key presses while recording reach this page before the window's accelerators.
        PreviewKeyDown += RecordKey;
        Loaded += (_, _) =>
        {
            shortcuts.Changed += Rebuild;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            shortcuts.Changed -= Rebuild;
            StopRecording();
        };
    }

    private static Style? TitleStyle() =>
        Application.Current?.Resources is { } r && r.TryGetValue("SubtitleTextBlockStyle", out var style) ? style as Style : null;

    private static TextBlock Header(string text)
    {
        var header = new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
        AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
        return header;
    }

    private void Rebuild()
    {
        groups.Children.Clear();
        resetAll.IsEnabled = shortcuts.Bindings.Overrides.Count > 0;
        saveError.Text = shortcuts.SaveError ?? "";
        saveError.Visibility = shortcuts.SaveError is null ? Visibility.Collapsed : Visibility.Visible;
        var query = filter.Text.Trim().ToLowerInvariant();
        foreach (var group in shortcuts.Commands.GroupBy(command => command.Group))
        {
            var rows = group.Where(command => Matches(command, query)).ToList();
            if (rows.Count == 0) continue;
            groups.Children.Add(Header(group.Key));
            var card = new StackPanel
            {
                Background = ProductivityStyle.Card,
                BorderBrush = ProductivityStyle.CardStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = ProductivityStyle.OverlayCorners,
                Padding = new Thickness(16, 4, 8, 4),
            };
            foreach (var command in rows) card.Children.Add(Row(command));
            groups.Children.Add(card);
        }
    }

    private bool Matches(ShortcutCommand command, string query)
    {
        if (query.Length == 0) return true;
        var current = string.Join(" ", shortcuts.Combos(command).Select(c => c.DisplayString)).ToLowerInvariant();
        return $"{command.Group} {command.Title}".ToLowerInvariant().Contains(query) || current.Contains(query)
            || PaletteMatcher.Score(query, command.Title) is not null;
    }

    private FrameworkElement Row(ShortcutCommand command)
    {
        var customised = shortcuts.IsCustomised(command);
        var current = shortcuts.Combos(command);
        var isRecording = recording == command.Id;

        var line = new Grid { ColumnSpacing = 10, MinHeight = 44 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        line.Children.Add(new TextBlock { Text = command.Title, VerticalAlignment = VerticalAlignment.Center });

        // The defaults table: every row names its default.
        var defaults = new TextBlock
        {
            Text = command.Defaults.Count > 0 ? "Default " + string.Join(", ", command.Defaults.Select(c => c.DisplayString)) : "No default",
            FontSize = 12,
            Foreground = customised ? ProductivityStyle.SecondaryText : ProductivityStyle.TertiaryText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(defaults, 1);
        line.Children.Add(defaults);

        var recorder = new ToggleButton
        {
            Content = isRecording ? "Type shortcut…" : current.Count > 0 ? string.Join(", ", current.Select(c => c.DisplayString)) : "None",
            IsChecked = isRecording,
            MinWidth = 120,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(recorder, $"Shortcut for {command.Title}");
        AutomationProperties.SetHelpText(recorder, isRecording ? "Recording" : current.Count > 0 ? string.Join(", ", current.Select(c => c.SpokenString)) : "None");
        ToolTipService.SetToolTip(recorder, isRecording ? "Press the new shortcut (Esc cancels, Backspace removes)" : $"Change the shortcut for {command.Title}");
        recorder.Click += (_, _) =>
        {
            pending = null;
            message = null;
            if (isRecording) StopRecording();
            else StartRecording(command.Id);
        };
        Grid.SetColumn(recorder, 2);
        line.Children.Add(recorder);

        var reset = new Button
        {
            Content = Icons.Make("arrow.uturn.backward", 14),
            Padding = new Thickness(6),
            Visibility = customised ? Visibility.Visible : Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(reset, $"Restore default ({(command.Defaults.Count > 0 ? string.Join(", ", command.Defaults.Select(c => c.DisplayString)) : "none")})");
        AutomationProperties.SetName(reset, $"Restore default shortcut for {command.Title}");
        reset.Click += (_, _) =>
        {
            if (pending?.CommandId == command.Id) pending = null;
            if (message?.CommandId == command.Id) message = null;
            shortcuts.Reset(command);
        };
        Grid.SetColumn(reset, 3);
        line.Children.Add(reset);

        if (isRecording)
        {
            // The keys go to the focused element's route, which passes through this page.
            recorder.Loaded += (_, _) => recorder.Focus(FocusState.Programmatic);
        }

        var block = new StackPanel { Spacing = 4, Padding = new Thickness(0, 2, 0, 6) };
        block.Children.Add(line);
        if (pending is { } conflict && conflict.CommandId == command.Id)
        {
            var bar = new Grid { ColumnSpacing = 8 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.Children.Add(new TextBlock
            {
                Text = $"{conflict.Combo.DisplayString} is used by {string.Join(", ", conflict.Conflicts.Select(shortcuts.TitleOf))}.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = ProductivityStyle.Caution,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, _) =>
            {
                pending = null;
                Rebuild();
            };
            Grid.SetColumn(cancel, 1);
            bar.Children.Add(cancel);
            var replace = new Button { Content = "Replace", Style = AccentStyle() };
            replace.Click += (_, _) =>
            {
                pending = null;
                shortcuts.Assign(conflict.Combo, command);
            };
            Grid.SetColumn(replace, 2);
            bar.Children.Add(replace);
            block.Children.Add(bar);
        }
        else if (message is { } note && note.CommandId == command.Id)
        {
            block.Children.Add(new TextBlock { Text = note.Text, Foreground = ProductivityStyle.Critical, TextWrapping = TextWrapping.Wrap });
        }
        return block;
    }

    private static Style? AccentStyle() =>
        Application.Current?.Resources is { } r && r.TryGetValue("AccentButtonStyle", out var style) ? style as Style : null;

    private void StartRecording(string commandId)
    {
        recording = commandId;
        shortcuts.IsSuspended = true;  // the keys must reach the recorder, not run commands
        Rebuild();
    }

    private void StopRecording()
    {
        if (recording is null) return;
        recording = null;
        shortcuts.IsSuspended = false;
        Rebuild();
    }

    private void RecordKey(object sender, KeyRoutedEventArgs e)
    {
        if (recording is not { } id || shortcuts.Command(id) is not { } command) return;
        var key = e.Key;
        // Modifier keys alone: wait for the rest of the shortcut.
        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.Shift
            or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
        var modifiers = ShortcutModifiers.None;
        if (IsDown(VirtualKey.Control)) modifiers |= ShortcutModifiers.Control;
        if (IsDown(VirtualKey.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (IsDown(VirtualKey.Menu)) modifiers |= ShortcutModifiers.Alt;

        recording = null;
        shortcuts.IsSuspended = false;
        if (modifiers == ShortcutModifiers.None && key == VirtualKey.Escape)
        {
            Rebuild();
            return;
        }
        if (modifiers == ShortcutModifiers.None && key is VirtualKey.Back or VirtualKey.Delete)
        {
            shortcuts.Clear(command);
            return;
        }
        var combo = new KeyCombo((int)key, modifiers);
        switch (shortcuts.Problem(combo, command))
        {
            case null:
                shortcuts.Assign(combo, command);
                break;
            case ShortcutProblem.NeedsModifier:
                message = (id, $"{combo.DisplayString} can’t be a shortcut: add Ctrl or Alt.");
                Rebuild();
                break;
            case ShortcutProblem.Reserved reserved:
                message = (id, $"Windows uses {combo.DisplayString} for {reserved.Reason}.");
                Rebuild();
                break;
            case ShortcutProblem.Conflict conflict:
                pending = (id, combo, conflict.CommandIds);
                Rebuild();
                break;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private async void ConfirmResetAll()
    {
        if (XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Restore every shortcut to its default?",
            PrimaryButtonText = "Restore defaults",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        FeatureMounts.CoverPage("productivity.resetShortcuts", true);
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                pending = null;
                message = null;
                shortcuts.ResetAll();
            }
        }
        finally
        {
            FeatureMounts.CoverPage("productivity.resetShortcuts", false);
        }
    }
}
