using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.Core.Browser;
using Mosaic.Core.Tabs;
using Windows.System;

namespace Mosaic.App.Features.Tabs;

/// <summary>
/// Tab search (Ctrl+Shift+A): every open tab of every workspace, filtered as you type, plus
/// recently closed tabs. ↑/↓ choose, Enter switches (to another workspace if needed), Escape
/// closes. Port of <c>TabSearchPanel.swift</c>; <see cref="TabsOverlay"/> shows it while
/// <see cref="BrowserModel.IsTabSearchVisible"/>, and the command palette can host it too.
/// </summary>
public sealed class TabSearchPanel : UserControl
{
    /// <summary>Rows drawn at most (the rest are reachable by typing more).</summary>
    private const int MaxOpenRows = 150;

    private readonly BrowserModel model;
    private readonly TextBox query = new()
    {
        PlaceholderText = "Search open tabs",
        BorderThickness = new Thickness(0),
        Background = new SolidColorBrush(TabsStyle.Transparent),
        FontSize = 15,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly StackPanel list = new() { Spacing = 2, Padding = new Thickness(6) };
    private readonly ScrollViewer scroller;
    private readonly TextBlock empty = new() { Foreground = TabsStyle.Secondary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 28) };
    private readonly List<(Border View, Action Activate)> rows = [];
    private int highlighted;

    public TabSearchPanel(BrowserModel model)
    {
        this.model = model;
        AutomationProperties.SetName(this, "Tab search");
        AutomationProperties.SetName(query, "Search open tabs");

        var header = new Grid { Height = 46, Padding = new Thickness(14, 0, 14, 0), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glass = TabsStyle.Icon("search", "⌕");
        glass.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(glass);
        Grid.SetColumn(query, 1);
        header.Children.Add(query);
        var hint = new TextBlock { Text = "Ctrl+Shift+A", FontSize = 12, Foreground = TabsStyle.Secondary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(hint, 2);
        header.Children.Add(hint);

        scroller = new ScrollViewer { Content = list, MaxHeight = 412 };
        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(new Rectangle { Height = 1, Fill = TabsStyle.CardStroke });
        body.Children.Add(empty);
        body.Children.Add(scroller);

        Content = TabsStyle.FloatingCard(body, 12);
        Width = 560;

        query.TextChanged += (_, _) =>
        {
            highlighted = 0;
            Rebuild();
        };
        query.KeyDown += QueryKeyDown;
        Loaded += (_, _) =>
        {
            query.Text = model.TabSearchQuery;
            query.SelectAll();
            Rebuild();
        };
    }

    /// <summary>Puts the keyboard in the search field.</summary>
    public void FocusQuery() => query.Focus(FocusState.Programmatic);

    private void QueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                Highlight(Math.Min(highlighted + 1, Math.Max(rows.Count - 1, 0)));
                e.Handled = true;
                break;
            case VirtualKey.Up:
                Highlight(Math.Max(highlighted - 1, 0));
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (highlighted < rows.Count) rows[highlighted].Activate();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                model.HideTabSearch();
                e.Handled = true;
                break;
        }
    }

    private void Rebuild()
    {
        var text = query.Text ?? "";
        model.TabSearchQuery = text;
        list.Children.Clear();
        rows.Clear();
        var open = model.TabSearchResults(text);
        var closed = model.ClosedSearchResults(text);
        if (open.Count > 0) list.Children.Add(Header($"Open Tabs · {open.Count}"));
        foreach (var tab in open.Take(MaxOpenRows)) AddRow(OpenRow(tab), () => Activate(() => model.Select(tab)), Describe(tab));
        if (closed.Count > 0) list.Children.Add(Header("Recently Closed"));
        foreach (var item in closed) AddRow(ClosedRow(item), () => Activate(() => model.ReopenClosed(item.Id)), $"Reopen {item.Title}");
        empty.Text = $"No tabs match “{text}”";
        empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        scroller.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Highlight(Math.Min(highlighted, Math.Max(rows.Count - 1, 0)));
    }

    private void Activate(Action action)
    {
        model.HideTabSearch();
        action();
    }

    private void AddRow(UIElement content, Action activate, string name)
    {
        var index = rows.Count;
        var view = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 4), MinHeight = 38, Child = content };
        AutomationProperties.SetName(view, name);
        view.PointerEntered += (_, _) => Highlight(index);
        view.Tapped += (_, _) => activate();
        rows.Add((view, activate));
        list.Children.Add(view);
    }

    private void Highlight(int index)
    {
        highlighted = index;
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].View.Background = i == index ? TabsStyle.Subtle : null;
        }
        if (index < rows.Count)
        {
            rows[index].View.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            if (FrameworkElementAutomationPeer.FromElement(rows[index].View) is { } peer)
            {
                peer.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
            }
        }
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = TabsStyle.Secondary,
        Margin = new Thickness(8, 6, 0, 2),
    };

    private UIElement OpenRow(BrowserTab tab)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse { Width = 8, Height = 8, Fill = TabsStyle.EngineBrush(tab.Engine), Opacity = tab.TabOpacity, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(dot);

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Opacity = tab.IsAsleep ? BrowserTab.AsleepOpacity + 0.2 : 1 };
        text.Children.Add(new TextBlock
        {
            Text = tab.DisplayTitle,
            FontSize = 13,
            FontWeight = ReferenceEquals(tab, model.SelectedTab) ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var detail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (model.GroupOf(tab) is { } group)
        {
            detail.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = TabsStyle.GroupBrush(group.Color), VerticalAlignment = VerticalAlignment.Center });
            detail.Children.Add(Small(group.DisplayName));
        }
        if (model.WorkspaceOf(tab) is { } workspace && workspace.Id != model.SelectedWorkspaceId) detail.Children.Add(Small($"in {workspace.Name}"));
        if (!tab.IsStartPage) detail.Children.Add(Small(Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : tab.Url));
        text.Children.Add(detail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (tab.IsPinned) badges.Children.Add(Badge("pin.fill", "Pinned"));
        if (tab.IsMuted) badges.Children.Add(Badge("speaker.slash.fill", "Muted"));
        else if (tab.IsPlayingAudio) badges.Children.Add(Badge("speaker.wave.2.fill", "Playing audio"));
        if (tab.IsAsleep) badges.Children.Add(Badge("moon.zzz", "Unloaded"));
        Grid.SetColumn(badges, 2);
        grid.Children.Add(badges);

        var close = new Button
        {
            Content = TabsStyle.Icon("xmark", "×", 10),
            Padding = new Thickness(4),
            Background = new SolidColorBrush(TabsStyle.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(close, $"Close {tab.DisplayTitle}");
        ToolTipService.SetToolTip(close, "Close Tab");
        close.Click += (_, _) =>
        {
            model.Close(tab);
            Rebuild();
        };
        Grid.SetColumn(close, 3);
        grid.Children.Add(close);
        return grid;
    }

    private static UIElement ClosedRow(ClosedItem item)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = item.Kind switch
        {
            ClosedItemKind.Group => TabsStyle.Icon("folder", "▤", 12),
            ClosedItemKind.Workspace => TabsStyle.Icon("square.stack", "▦", 12),
            _ => TabsStyle.Icon("arrow.uturn.backward", "↶", 12),
        };
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(Small(item.ClosedAt == DateTimeOffset.MinValue ? "" : $"Closed {TabsStyle.Ago(item.ClosedAt)}"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var reopen = Small("Reopen");
        reopen.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(reopen, 2);
        grid.Children.Add(reopen);
        return grid;
    }

    private static TextBlock Small(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = TabsStyle.Secondary,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 260,
    };

    private static FrameworkElement Badge(string icon, string help)
    {
        var view = TabsStyle.Icon(icon, help, 11);
        if (view is TextBlock text) text.Foreground = TabsStyle.Secondary;
        ToolTipService.SetToolTip(view, help);
        return view;
    }

    private string Describe(BrowserTab tab)
    {
        var parts = new List<string> { tab.DisplayTitle, tab.Engine.DisplayName() };
        if (model.WorkspaceOf(tab) is { } workspace && workspace.Id != model.SelectedWorkspaceId) parts.Add($"in {workspace.Name}");
        if (model.GroupOf(tab) is { } group) parts.Add($"group {group.DisplayName}");
        if (tab.IsPinned) parts.Add("pinned");
        if (tab.IsMuted) parts.Add("muted");
        if (tab.IsAsleep) parts.Add("unloaded");
        return string.Join(", ", parts);
    }
}
