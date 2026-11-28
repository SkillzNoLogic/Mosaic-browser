using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Shell;
using Windows.System;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// Settings inside the main window's page area (port of macOS's <c>SettingsView</c>; never a
/// second window): a searchable list of the fourteen categories on the left, the selected
/// category's page (from <c>FeatureMounts.SettingsPages</c>, or a placeholder until its owner
/// delivers it) on the right. Esc or Done returns to the page. Opaque, and the page under it is
/// hidden while it shows (<c>FeatureMounts.CoverPage</c>).
/// </summary>
internal sealed class SettingsView : Grid
{
    private readonly ShellRoot shell;
    private readonly TextBox search;
    private readonly StackPanel list = new() { Spacing = 2, Padding = new Thickness(8, 0, 8, 12) };
    private readonly Border content = new();
    private readonly TextBlock heading;
    private readonly FontIcon headingIcon;
    private SettingsCategory? shown;
    private bool updatingQuery;

    public SettingsView(ShellRoot shell)
    {
        this.shell = shell;
        var theme = shell.Theme;
        AutomationProperties.SetName(this, "Settings");
        AutomationProperties.SetLandmarkType(this, AutomationLandmarkType.Main);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(264) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Category list.
        var side = new Grid { Background = theme.SettingsSidebar };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        search = new TextBox
        {
            PlaceholderText = "Search settings",
            Margin = new Thickness(12, 14, 12, 10),
            IsSpellCheckEnabled = false,
        };
        AutomationProperties.SetName(search, "Search settings");
        search.TextChanged += (_, _) =>
        {
            if (updatingQuery) return;
            shell.Model.SettingsQuery = search.Text;
            Fill();
        };
        search.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && SettingsSearch.Search(search.Text).FirstOrDefault() is { } first)
            {
                e.Handled = true;
                shell.Model.ShowSettings(first.Category);
            }
        };
        side.Children.Add(search);
        list.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        list.TabFocusNavigation = KeyboardNavigationMode.Once;
        AutomationProperties.SetName(list, "Settings categories");
        AutomationProperties.SetLandmarkType(list, AutomationLandmarkType.Navigation);
        var scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(scroller, 1);
        side.Children.Add(scroller);
        Children.Add(side);

        var divider = ShellUi.Divider(theme, vertical: true);
        SetColumn(divider, 1);
        Children.Add(divider);

        // Page.
        var main = new Grid { Background = theme.PageBackground };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Height = 60, Padding = new Thickness(28, 0, 20, 0), BorderBrush = theme.Divider, BorderThickness = new Thickness(0, 0, 0, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headingIcon = ShellUi.Icon("gearshape", 18, theme.AccentText);
        headingIcon.Margin = new Thickness(0, 0, 12, 0);
        header.Children.Add(headingIcon);
        heading = ShellUi.Text("", 20, semibold: true);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        SetColumn(heading, 1);
        header.Children.Add(heading);
        var donePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        donePanel.Children.Add(ShellUi.Icon("xmark", 12));
        donePanel.Children.Add(new TextBlock { Text = "Done" });
        var done = new Button { Content = donePanel, VerticalAlignment = VerticalAlignment.Center }.Named("Close settings", "Close Settings (Esc)");
        done.Click += (_, _) => shell.Model.DismissSettings();
        SetColumn(done, 2);
        header.Children.Add(done);
        main.Children.Add(header);
        SetRow(content, 1);
        main.Children.Add(content);
        SetColumn(main, 2);
        Children.Add(main);

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            e.Handled = true;
            shell.Model.DismissSettings();
        };
        KeyboardAccelerators.Add(escape);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>Shows <paramref name="category"/> (rebuilding the page only when it changes).</summary>
    public void Show(SettingsCategory category)
    {
        if (search.Text != shell.Model.SettingsQuery)
        {
            updatingQuery = true;
            search.Text = shell.Model.SettingsQuery;
            updatingQuery = false;
        }
        var focused = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject f && IsInList(f);
        Fill();
        if (focused) FocusSelectedRow();
        if (shown == category && content.Child is not null) return;
        shown = category;
        heading.Text = category.Title();
        headingIcon.Glyph = System.Icons.Glyph(Symbol(category));
        content.Child = Page(category);
    }

    private bool IsInList(DependencyObject element)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (ReferenceEquals(e, list)) return true;
        }
        return false;
    }

    private void FocusSelectedRow()
    {
        foreach (var child in list.Children.OfType<Button>())
        {
            if (AutomationProperties.GetItemStatus(child) == "Selected")
            {
                child.Focus(FocusState.Keyboard);
                return;
            }
        }
    }

    /// <summary>Settings closed: the next opening builds its page afresh (it may have changed meanwhile).</summary>
    public void Hidden()
    {
        shown = null;
        content.Child = null;
    }

    private UIElement Page(SettingsCategory category)
    {
        if (FeatureMounts.SettingsPages.TryGetValue(category, out var make))
        {
            try
            {
                return make(shell.Model);
            }
            catch (Exception error)
            {
                return shell.Pending(category.Title(), $"This page could not be shown: {error.Message}", Symbol(category));
            }
        }
        return shell.Pending(category.Title(), "This section is being built.", Symbol(category));
    }

    private void Fill()
    {
        var theme = shell.Theme;
        list.Children.Clear();
        var query = shell.Model.SettingsQuery;
        if (query.Trim().Length == 0)
        {
            var first = true;
            foreach (var group in SettingsSearch.Groups)
            {
                if (!first) list.Children.Add(new Border { Height = 1, Background = theme.Divider, Margin = new Thickness(10, 6, 10, 6) });
                first = false;
                foreach (var category in group) list.Children.Add(Row(category, []));
            }
            return;
        }
        var matches = SettingsSearch.Search(query);
        if (matches.Count == 0)
        {
            var none = ShellUi.Text($"No settings match “{query}”.", 13, theme.SecondaryText);
            none.Margin = new Thickness(10);
            none.TextWrapping = TextWrapping.Wrap;
            list.Children.Add(none);
        }
        foreach (var match in matches) list.Children.Add(Row(match.Category, match.Labels));
    }

    private Button Row(SettingsCategory category, IReadOnlyList<string> labels)
    {
        var theme = shell.Theme;
        var selected = category == shell.Model.SettingsPage;
        var tile = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(7),
            Background = selected ? theme.Accent : theme.AccentSubtle,
            Child = ShellUi.Icon(Symbol(category), 14, selected ? theme.OnAccent : theme.AccentText),
        };
        var title = ShellUi.Text(category.Title(), 13.5, theme.PrimaryText, semibold: selected);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        line.Children.Add(tile);
        line.Children.Add(title);
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(line);
        foreach (var label in labels.Take(4))
        {
            var text = ShellUi.Text(label, 12, theme.SecondaryText);
            text.Margin = new Thickness(40, 0, 0, 0);
            stack.Children.Add(text);
        }
        var row = new Button
        {
            Content = stack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 8, 6),
        }.Subtle(theme, 6);
        if (selected) row.Background = theme.Hover;
        row.Click += (_, _) => shell.Model.ShowSettings(category);
        AutomationProperties.SetName(row, category.Title());
        AutomationProperties.SetItemStatus(row, selected ? "Selected" : "");
        if (labels.Count > 0) AutomationProperties.SetHelpText(row, string.Join(", ", labels));
        return row;
    }

    /// <summary>Icon of a category (macOS's SF Symbol names, mapped to Fluent glyphs).</summary>
    public static string Symbol(SettingsCategory category) => category switch
    {
        SettingsCategory.Mosaics => "square.grid.2x2",
        SettingsCategory.Engine => "engine",
        SettingsCategory.General => "gearshape",
        SettingsCategory.TabsAndWorkspaces => "square.on.square",
        SettingsCategory.Search => "magnifyingglass",
        SettingsCategory.Privacy => "hand.raised",
        SettingsCategory.Passwords => "key",
        SettingsCategory.Downloads => "arrow.down.circle",
        SettingsCategory.Extensions => "puzzlepiece.extension",
        SettingsCategory.Shortcuts => "command",
        SettingsCategory.Languages => "globe",
        SettingsCategory.Accessibility => "accessibility",
        SettingsCategory.Performance => "gauge",
        _ => "info.circle",
    };
}
