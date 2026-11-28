using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.Core.Shell;
using Windows.UI;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// A miniature of a layout for the preset cards (port of macOS's <c>MosaicPreview</c>): the
/// title row with its caption buttons, toolbar, tab bar, rail, sidebar and the page card,
/// in the layout's colours.
/// </summary>
internal static class MosaicPreview
{
    public static FrameworkElement Make(MosaicLayout layout, ShellTheme theme, double width = 260)
    {
        var s = width / 260;
        var a = layout.Appearance;
        var dark = a.ColorScheme switch
        {
            ColorSchemePreference.Dark => true,
            ColorSchemePreference.Light => false,
            _ => theme.IsDark,
        };
        var accent = ShellTheme.ColorFromHex(a.Accent) ?? theme.AccentColor;
        var ink = new SolidColorBrush(dark ? ShellTheme.C(255, 255, 255, 0x40) : ShellTheme.C(0, 0, 0, 0x26));
        var accentBrush = new SolidColorBrush(accent);
        var page = new SolidColorBrush(dark ? ShellTheme.C(0x2A, 0x2A, 0x2C) : Colors.White);
        var window = dark ? ShellTheme.C(0x20, 0x20, 0x22) : ShellTheme.C(0xEE, 0xEF, 0xF2);

        var root = new Grid { Width = width, Height = 150 * s };
        root.Children.Add(new Border
        {
            Background = a.Background.Kind == BackgroundKind.System ? new SolidColorBrush(window) : ShellBackgrounds.Fill(a.Background, dark, false),
        });

        var titleItems = layout.DrawnItems(ShellRegion.TitleBar, true);
        var toolbarItems = layout.DrawnItems(ShellRegion.Toolbar, true);
        var tabBarItems = layout.DrawnItems(ShellRegion.TabBar, true);
        var railItems = layout.DrawnItems(ShellRegion.Rail, true);
        var trailingRailItems = layout.DrawnItems(ShellRegion.TrailingRail, true);
        var vertical = layout.TabPlacement.IsVertical();
        var inset = a.ContentInset * s * 0.8;
        var radius = a.ContentCornerRadius * s * 0.8;

        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(column);

        if (titleItems.Count > 0)
        {
            var title = Row(titleItems, 22 * s, s, accentBrush, ink, captions: true);
            if (a.ToolbarSurface && a.Background.Kind == BackgroundKind.System)
            {
                title.Background = new SolidColorBrush(dark ? ShellTheme.C(0x20, 0x21, 0x24) : ShellTheme.C(0xDE, 0xE1, 0xE6));
            }
            column.Children.Add(title);
        }

        var body = new Grid();
        Grid.SetRow(body, 1);
        column.Children.Add(body);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (railItems.Count > 0) body.Children.Add(Rail(railItems, s, ink, 0));
        if (vertical)
        {
            var sidebar = Sidebar(s, accentBrush, ink, dark);
            Grid.SetColumn(sidebar, layout.TabPlacement == TabPlacement.SidebarLeading ? 1 : 3);
            body.Children.Add(sidebar);
        }

        var main = new Grid();
        Grid.SetColumn(main, 2);
        body.Children.Add(main);
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel();
        main.Children.Add(header);
        if (titleItems.Count == 0) header.Children.Add(Row([], 12 * s, s, accentBrush, ink, captions: true));
        if (toolbarItems.Count > 0)
        {
            var bar = Row(toolbarItems, 20 * s, s, accentBrush, ink, captions: false);
            if (a.ToolbarSurface) bar.Background = page;
            header.Children.Add(bar);
        }
        if (tabBarItems.Count > 0) header.Children.Add(Row(tabBarItems, 14 * s, s, accentBrush, ink, captions: false));
        if (layout.BookmarksBar)
        {
            var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 * s, Height = 10 * s, Padding = new Thickness(8 * s, 3 * s, 0, 3 * s) };
            if (a.ToolbarSurface) marks.Background = page;
            for (var i = 0; i < 4; i++) marks.Children.Add(new Rectangle { Width = 14 * s, Height = 3 * s, RadiusX = 1.5 * s, RadiusY = 1.5 * s, Fill = ink });
            header.Children.Add(marks);
        }

        var pageArea = new Grid();
        Grid.SetRow(pageArea, 1);
        main.Children.Add(pageArea);
        pageArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pageArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var card = PageCard(s, accentBrush, ink, page, radius, a.Glow ? accentBrush : null);
        card.Margin = new Thickness(inset, inset > 0 ? Math.Max(1.5, inset / 2) : 0, inset, inset);
        pageArea.Children.Add(card);
        if (trailingRailItems.Count > 0)
        {
            var rail = Rail(trailingRailItems, s, ink, 1);
            pageArea.Children.Add(rail);
        }

        var frame = new Border
        {
            Child = root,
            CornerRadius = new CornerRadius(8),
            BorderBrush = theme.CardStroke,
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetAccessibilityView(frame, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return frame;
    }

    private static Grid Row(IReadOnlyList<RegionItem> items, double height, double s, Brush accent, Brush ink, bool captions)
    {
        var row = new Grid { Height = height, Padding = new Thickness(6 * s, 0, 4 * s, 0) };
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 * s, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var item in items)
        {
            switch (item.Component)
            {
                case ShellComponent.TabStrip:
                    for (var i = 0; i < 3; i++)
                    {
                        stack.Children.Add(new Border
                        {
                            Width = 34 * s,
                            Height = height * 0.62,
                            CornerRadius = new CornerRadius(3 * s),
                            Background = i == 0 ? new SolidColorBrush(ShellTheme.C(255, 255, 255, 0xE0)) : null,
                            Child = new Rectangle { Width = 18 * s, Height = 2.5 * s, Fill = i == 0 ? accent : ink, VerticalAlignment = VerticalAlignment.Center },
                        });
                    }
                    break;
                case ShellComponent.AddressField:
                    stack.Children.Add(new Rectangle { Width = 80 * s, Height = 8 * s, RadiusX = 4 * s, RadiusY = 4 * s, Fill = ink });
                    break;
                case ShellComponent.FlexibleSpace:
                    stack.Children.Add(new Border { Width = 6 * s });
                    break;
                default:
                    stack.Children.Add(new Ellipse { Width = 5 * s, Height = 5 * s, Fill = ink });
                    break;
            }
        }
        row.Children.Add(stack);
        if (captions)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4 * s, 0) };
            foreach (var _ in Enumerable.Range(0, 3)) buttons.Children.Add(new Rectangle { Width = 5 * s, Height = 1.5 * s, Fill = ink });
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
        }
        return row;
    }

    private static StackPanel Rail(IReadOnlyList<RegionItem> items, double s, Brush ink, int column)
    {
        var rail = new StackPanel { Width = 18 * s, Spacing = 7 * s, Padding = new Thickness(0, 8 * s, 0, 0) };
        foreach (var _ in items.Where(i => i.Component != ShellComponent.FlexibleSpace).Take(6))
        {
            rail.Children.Add(new Ellipse { Width = 6 * s, Height = 6 * s, Fill = ink, HorizontalAlignment = HorizontalAlignment.Center });
        }
        Grid.SetColumn(rail, column);
        return rail;
    }

    private static StackPanel Sidebar(double s, Brush accent, Brush ink, bool dark)
    {
        var bar = new StackPanel { Width = 76 * s, Spacing = 6 * s, Padding = new Thickness(6 * s, 6 * s, 6 * s, 0) };
        bar.Children.Add(new Rectangle { Height = 9 * s, RadiusX = 3 * s, RadiusY = 3 * s, Fill = ink });
        for (var i = 0; i < 5; i++)
        {
            var line = new Border
            {
                Height = 9 * s,
                CornerRadius = new CornerRadius(3 * s),
                Background = i == 0 ? new SolidColorBrush(dark ? ShellTheme.C(255, 255, 255, 0x30) : ShellTheme.C(255, 255, 255, 0xD0)) : null,
                Padding = new Thickness(3 * s, 0, 0, 0),
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 * s, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new Rectangle { Width = 4 * s, Height = 4 * s, Fill = i == 0 ? accent : ink });
            content.Children.Add(new Rectangle { Width = (34 - i * 3) * s, Height = 2.5 * s, Fill = ink });
            line.Child = content;
            bar.Children.Add(line);
        }
        return bar;
    }

    private static Border PageCard(double s, Brush accent, Brush ink, Brush page, double radius, Brush? glow)
    {
        var lines = new StackPanel { Spacing = 5 * s, Padding = new Thickness(14 * s, 12 * s, 14 * s, 0) };
        lines.Children.Add(new Rectangle { Width = 50 * s, Height = 4 * s, RadiusX = 2 * s, RadiusY = 2 * s, Fill = accent, HorizontalAlignment = HorizontalAlignment.Left });
        foreach (var w in new[] { 110, 96, 104, 70 })
        {
            lines.Children.Add(new Rectangle { Width = w * s, Height = 2.5 * s, Fill = ink, HorizontalAlignment = HorizontalAlignment.Left });
        }
        var tiles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 * s, Margin = new Thickness(0, 6 * s, 0, 0) };
        for (var i = 0; i < 3; i++) tiles.Children.Add(new Rectangle { Width = 24 * s, Height = 16 * s, RadiusX = 2 * s, RadiusY = 2 * s, Fill = ink, Opacity = 0.6 });
        lines.Children.Add(tiles);
        return new Border
        {
            Child = lines,
            Background = page,
            CornerRadius = new CornerRadius(radius),
            BorderBrush = glow,
            BorderThickness = new Thickness(glow is null ? 0 : 1.5),
        };
    }
}
