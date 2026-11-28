using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Mosaic.App.Features.Productivity;

/// <summary>
/// The new-tab page's Speed Dial (<c>mosaic://newtab</c>, and component "speedDial" for a shell
/// that embeds it in its start page): pinned sites, then most visited sites, then an "Add site"
/// tile. It paints no background, so the shell's or canvas's background shows through. Port of
/// <c>StartPageContent.swift</c>.
/// </summary>
internal sealed class SpeedDialView : UserControl
{
    public const string CoverId = "productivity.speedDialEditor";

    private readonly BrowserModel model;
    private readonly SpeedDialModel dial;
    private readonly FaviconCache favicons;
    private readonly VariableSizedWrapGrid grid;
    private readonly TextBlock error;
    private readonly Button optionsButton;

    public SpeedDialView(BrowserModel model)
    {
        this.model = model;
        dial = model.Productivity.SpeedDial;
        favicons = model.Productivity.Favicons;

        var title = new TextBlock
        {
            Text = "Speed Dial",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ProductivityStyle.SecondaryText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);

        optionsButton = new Button { Content = Icons.Make("ellipsis", 14), Padding = new Thickness(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(optionsButton, "Speed Dial options");
        AutomationProperties.SetName(optionsButton, "Speed Dial options");
        optionsButton.Click += (_, _) => ShowOptions();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(optionsButton, 1);
        header.Children.Add(optionsButton);

        grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 112, ItemHeight = 108 };
        AutomationProperties.SetName(grid, "Speed Dial");
        error = new TextBlock { Foreground = ProductivityStyle.Critical, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };

        var stack = new StackPanel { Spacing = 10, MaxWidth = 760, Padding = new Thickness(24, 0, 24, 0), HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(header);
        stack.Children.Add(grid);
        stack.Children.Add(error);
        Content = new ScrollViewer { Content = stack, VerticalAlignment = VerticalAlignment.Center, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        Loaded += (_, _) =>
        {
            dial.Changed += Rebuild;
            favicons.IconReady += IconReady;
            _ = model.Productivity.StartAsync();
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            dial.Changed -= Rebuild;
            favicons.IconReady -= IconReady;
        };
    }

    private void IconReady(string host) => Rebuild();

    private void Rebuild()
    {
        grid.Children.Clear();
        var (pinned, topSites) = dial.Tiles();
        for (var index = 0; index < pinned.Count; index++) grid.Children.Add(Tile(pinned[index], true, index, pinned.Count));
        foreach (var site in topSites) grid.Children.Add(Tile(site, false, 0, 0));
        grid.Children.Add(AddTile());
        error.Text = dial.SaveError ?? "";
        error.Visibility = dial.SaveError is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement Tile(SpeedDialSite site, bool pinned, int index, int count)
    {
        var face = new Grid { Width = 64, Height = 64 };
        var plate = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ProductivityStyle.Card,
            BorderBrush = ProductivityStyle.CardStroke,
            BorderThickness = new Thickness(1),
        };
        face.Children.Add(plate);
        var icon = dial.Dial.ShowsIcons ? favicons.IconPath(site.Url, fetch: true) : null;
        face.Children.Add(icon is not null
            ? new Image { Source = new BitmapImage(new Uri(icon)) { DecodePixelWidth = 68 }, Width = 34, Height = 34 }
            : LetterTile(site));

        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 104 };
        if (pinned)
        {
            var pin = Icons.Make("pin.fill", 8);
            pin.Foreground = ProductivityStyle.SecondaryText;
            pin.VerticalAlignment = VerticalAlignment.Center;
            label.Children.Add(pin);
        }
        label.Children.Add(new TextBlock { Text = site.DisplayTitle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 92 });

        var content = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(face);
        content.Children.Add(label);

        var button = new Button
        {
            Content = content,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, $"{site.DisplayTitle}\n{site.Url}");
        AutomationProperties.SetName(button, site.DisplayTitle);
        AutomationProperties.SetHelpText(button, pinned ? $"Pinned site. Opens {site.Host}" : $"Frequently visited. Opens {site.Host}");
        button.Click += (_, _) => model.OpenFromProductivity(site.Url, inNewTab: IsCtrlDown());

        var menu = new MenuFlyout();
        menu.Items.Add(Item("Open in new tab", () => model.OpenFromProductivity(site.Url, inNewTab: true)));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (pinned)
        {
            menu.Items.Add(Item("Edit…", () => ShowEditor(button, site)));
            menu.Items.Add(Item("Move left", () => dial.Shift(site.Id, -1), index > 0));
            menu.Items.Add(Item("Move right", () => dial.Shift(site.Id, 1), index < count - 1));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Remove", () => dial.Remove(site.Id)));
        }
        else
        {
            menu.Items.Add(Item("Pin to Speed Dial", () => dial.Pin(site)));
            menu.Items.Add(Item("Remove", () => dial.HideTopSite(site)));
        }
        Cover(menu);
        button.ContextFlyout = menu;

        if (pinned)
        {
            // Drag a pinned tile onto another to move it there.
            button.CanDrag = true;
            button.DragStarting += (_, e) => e.Data.SetText(site.Id.ToString());
            button.AllowDrop = true;
            button.DragOver += (_, e) => e.AcceptedOperation = DataPackageOperation.Move;
            button.Drop += async (_, e) =>
            {
                if (!e.DataView.Contains(StandardDataFormats.Text)) return;
                if (Guid.TryParse(await e.DataView.GetTextAsync(), out var dragged)) dial.Move(dragged, index);
            };
        }
        return button;
    }

    private static FrameworkElement LetterTile(SpeedDialSite site)
    {
        var letter = site.DisplayTitle.Length > 0 ? char.ToUpperInvariant(site.DisplayTitle[0]).ToString() : "?";
        var hash = 5381u;
        foreach (var c in site.Host) hash = unchecked(hash * 33 + c);
        var color = FromHsv(hash % 360, 0.5, 0.75);
        var circle = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letter,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        AutomationProperties.SetAccessibilityView(circle, AccessibilityView.Raw);
        return circle;
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private FrameworkElement AddTile()
    {
        var plus = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(12),
            BorderBrush = ProductivityStyle.TertiaryText,
            BorderThickness = new Thickness(1.5),
            Child = Icons.Make("plus", 20),
        };
        var content = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(plus);
        content.Children.Add(new TextBlock { Text = "Add site", FontSize = 12, Foreground = ProductivityStyle.SecondaryText, HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button
        {
            Content = content,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, "Add a site to Speed Dial");
        AutomationProperties.SetName(button, "Add site to Speed Dial");
        button.Click += (_, _) => ShowEditor(button, null);
        return button;
    }

    private void ShowOptions()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Add site…", () => ShowEditor(optionsButton, null)));
        if (model.SelectedTab is { IsStartPage: false } tab && SpeedDial.NormalizedUrl(tab.Url) is not null && !dial.Dial.Contains(tab.Url))
        {
            var (title, url) = (tab.Title, tab.Url);
            menu.Items.Add(Item("Add current page", () => dial.TryAdd(title, url)));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var topSites = new ToggleMenuFlyoutItem { Text = "Show most visited sites", IsChecked = dial.Dial.ShowsTopSites };
        topSites.Click += (_, _) => dial.SetShowsTopSites(topSites.IsChecked);
        menu.Items.Add(topSites);
        var icons = new ToggleMenuFlyoutItem { Text = "Show site icons", IsChecked = dial.Dial.ShowsIcons };
        icons.Click += (_, _) => dial.SetShowsIcons(icons.IsChecked);
        menu.Items.Add(icons);
        if (dial.Dial.HiddenTopSites.Count > 0) menu.Items.Add(Item("Restore removed sites", dial.RestoreHiddenTopSites));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Icons are fetched directly from each site, without cookies, and kept in your profile.", IsEnabled = false });
        Cover(menu);
        menu.ShowAt(optionsButton);
    }

    /// <summary>Add or edit a pinned site.</summary>
    private void ShowEditor(FrameworkElement anchor, SpeedDialSite? site)
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "Optional", Text = site?.Title ?? "" };
        var address = new TextBox { Header = "Address", PlaceholderText = "example.com", Text = site?.Url ?? "" };
        var message = new TextBlock { Foreground = ProductivityStyle.Critical, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var save = new Button { Content = site is null ? "Add" : "Save", Style = AccentStyle(), IsEnabled = address.Text.Trim().Length > 0 };
        var cancel = new Button { Content = "Cancel" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var panel = new StackPanel { Spacing = 12, Width = 320 };
        panel.Children.Add(new TextBlock { Text = site is null ? "Add to Speed Dial" : "Edit site", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(name);
        panel.Children.Add(address);
        panel.Children.Add(message);
        panel.Children.Add(buttons);

        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom };
        Cover(flyout);
        void Save()
        {
            try
            {
                if (site is null) dial.Add(name.Text, address.Text);
                else dial.Update(site.Id, name.Text, address.Text);
                flyout.Hide();
            }
            catch (SpeedDialException failure)
            {
                message.Text = failure.Message;
                message.Visibility = Visibility.Visible;
            }
        }
        address.TextChanged += (_, _) => save.IsEnabled = address.Text.Trim().Length > 0;
        address.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            Save();
        };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => flyout.Hide();
        flyout.ShowAt(anchor);
    }

    private static Style? AccentStyle() =>
        Application.Current?.Resources is { } r && r.TryGetValue("AccentButtonStyle", out var style) ? style as Style : null;

    /// <summary>Menus and flyouts draw over the page: Gecko's page window is hidden meanwhile.</summary>
    private static void Cover(FlyoutBase flyout)
    {
        flyout.Opened += (_, _) => FeatureMounts.CoverPage(CoverId, true);
        flyout.Closed += (_, _) => FeatureMounts.CoverPage(CoverId, false);
    }

    private static MenuFlyoutItem Item(string text, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static bool IsCtrlDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
}
