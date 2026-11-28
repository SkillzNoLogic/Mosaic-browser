using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.App.Features;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Search;
using Windows.System;

namespace Mosaic.App;

/// <summary>
/// Mosaic's minimal built-in UI, the v0.1 window in WinUI: tabs in the title bar, a toolbar
/// (navigation, address field, search engine, engine switch, content blocker, extensions), the
/// page area, a notice, the blocker's popover and the Extensions sheet. Used while no shell is
/// registered (<see cref="FeatureMounts.Shell"/>), and as the fallback if the shell fails.
/// </summary>
/// <remarks>
/// State is shown by following the model's change notifications (<see cref="Ui.Watch"/>);
/// actions call the model. Everything drawn over the page area (the Extensions sheet) declares
/// itself with <see cref="FeatureMounts.CoverPage"/>, so Gecko's page window hides under it;
/// the popover and the notice are windowed popups, which draw above it anyway.
/// </remarks>
internal sealed partial class BasicShell : Grid
{
    private const double TabWidth = 210;

    private readonly BrowserModel model;
    private readonly MainWindow window;
    private readonly StackPanel tabList = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly ScrollViewer tabScroller;
    private readonly Dictionary<BrowserTab, (Button Chip, IDisposable Watch)> chips = [];
    private readonly TextBox addressBox;
    private readonly Popup notice;
    private readonly Border noticeCard;
    private bool updatingAddress;

    public BasicShell(BrowserModel model, MainWindow window)
    {
        this.model = model;
        this.window = window;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- Title bar: tabs, new tab, a strip to drag the window by, the caption buttons ----
        var titleBar = new Grid { Padding = new Thickness(8, 6, 0, 0) };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        var captionSpace = new ColumnDefinition { Width = new GridLength(window.CaptionButtonsWidth) };
        titleBar.ColumnDefinitions.Add(captionSpace);

        var tabsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Stretch };
        tabScroller = new ScrollViewer
        {
            Content = tabList,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var newTab = IconButton("plus", "New Tab (Ctrl+T)", () => model.NewTab());
        newTab.VerticalAlignment = VerticalAlignment.Center;
        var tabsHost = new Grid();
        tabsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        tabsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabsHost.Children.Add(tabScroller);
        SetColumn(newTab, 1);
        tabsHost.Children.Add(newTab);
        tabsHost.HorizontalAlignment = HorizontalAlignment.Left;
        titleBar.Children.Add(tabsHost);
        // The strip can take no more than its column; the scroller then scrolls.
        titleBar.SizeChanged += (_, _) => tabScroller.MaxWidth = Math.Max(0, titleBar.ActualWidth - 120 - captionSpace.Width.Value - 48);
        tabScroller.PointerWheelChanged += TabScroller_PointerWheelChanged;
        Children.Add(titleBar);

        // ---- Toolbar ----
        var toolbar = new Grid { Padding = new Thickness(8, 0, 10, 0), ColumnSpacing = 6 };
        SetRow(toolbar, 1);
        for (int i = 0; i < 8; i++) toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions[3].Width = new GridLength(1, GridUnitType.Star);

        var back = IconButton("back", "Back (Alt+Left)", model.GoBack);
        var forward = IconButton("forward", "Forward (Alt+Right)", model.GoForward);
        var reload = IconButton("reload", "Reload (Ctrl+R)", model.ReloadOrStop);
        Place(toolbar, back, 0);
        Place(toolbar, forward, 1);
        Place(toolbar, reload, 2);

        addressBox = new TextBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            CornerRadius = new CornerRadius(8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(addressBox, "Address and search");
        addressBox.KeyDown += AddressBox_KeyDown;
        addressBox.GotFocus += (_, _) =>
        {
            model.BeginAddressEditing();
            addressBox.SelectAll();
        };
        addressBox.LostFocus += (_, _) =>
        {
            // Switching to another app and back is not leaving the field.
            if (window.AppWindow.IsVisible && IsWindowActive()) model.EndAddressEditing();
        };
        addressBox.TextChanged += (_, _) =>
        {
            if (!updatingAddress) model.AddressText = addressBox.Text;
        };
        Place(toolbar, addressBox, 3);

        // Appears while a Chrome Web Store listing is open.
        var addExtension = new Button { Style = Ui.Style("AccentButtonStyle"), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(addExtension, "Convert this Chrome extension and run it on the Mozilla engine");
        addExtension.Click += async (_, _) => await model.AddWebStoreExtensionAsync();
        Place(toolbar, addExtension, 4);

        var searchEngines = new ComboBox { MinWidth = 132, VerticalAlignment = VerticalAlignment.Center };
        foreach (var engine in SearchEngine.BuiltIn) searchEngines.Items.Add(new ComboBoxItem { Content = engine.Name, Tag = engine });
        ToolTipService.SetToolTip(searchEngines, "Search engine (Ctrl+Shift+S cycles)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(searchEngines, "Search engine");
        searchEngines.SelectionChanged += (_, _) =>
        {
            if (searchEngines.SelectedItem is ComboBoxItem { Tag: SearchEngine engine } && engine != model.SearchEngine) model.SearchEngine = engine;
        };
        Place(toolbar, searchEngines, 5);

        // Mozilla / Chromium switch for the current tab.
        var useGecko = EngineToggle("Mozilla", Ui.GeckoColor);
        var useChromium = EngineToggle("Chromium", Ui.ChromiumColor);
        void SyncEngines()
        {
            useGecko.IsChecked = model.IsGeckoEngine;
            useChromium.IsChecked = model.IsChromiumEngine;
        }
        // The model decides; the toggles only show its state (a refused switch springs back).
        useGecko.Click += (_, _) =>
        {
            model.SetEngine(EngineKind.Gecko);
            SyncEngines();
        };
        useChromium.Click += (_, _) =>
        {
            model.SetEngine(EngineKind.Chromium);
            SyncEngines();
        };
        var engines = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        engines.Children.Add(useGecko);
        engines.Children.Add(useChromium);
        ToolTipService.SetToolTip(engines, "Rendering engine for this tab (Ctrl+Shift+E switches)");
        Place(toolbar, engines, 6);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(CreateBlockerButton());
        tools.Children.Add(IconButton("puzzlepiece", "Extensions (Ctrl+Shift+X)", () => model.ShowExtensions = true));
        Place(toolbar, tools, 7);
        Children.Add(toolbar);

        // ---- Page area, the notice above it, the Extensions sheet over it ----
        var page = new Grid { BorderBrush = Ui.Brush("ControlStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        SetRow(page, 2);
        page.Children.Add(window.ContentArea);
        noticeCard = new Border
        {
            Padding = new Thickness(14, 8, 14, 8),
            Background = Ui.Brush("SolidBackgroundFillColorTertiaryBrush"),
            BorderBrush = Ui.Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            IsHitTestVisible = false,
            MaxWidth = 540,
        };
        // Windowed, so it draws above Gecko's page window.
        notice = new Popup { Child = noticeCard, ShouldConstrainToRootBounds = false, IsHitTestVisible = false };
        noticeCard.SizeChanged += (_, _) => PlaceNotice(page);
        page.SizeChanged += (_, _) => PlaceNotice(page);
        page.Children.Add(notice);
        Children.Add(page);

        extensionsSheet = CreateExtensionsSheet();
        SetRow(extensionsSheet, 1);
        SetRowSpan(extensionsSheet, 2);
        Children.Add(extensionsSheet);

        // ---- Following the model ----
        Ui.Watch(model, () =>
        {
            back.IsEnabled = model.CanGoBack;
            forward.IsEnabled = model.CanGoForward;
            reload.IsEnabled = model.CanReload;
            ((FontIcon)reload.Content).Glyph = Icons.Glyph(model.IsLoading ? "xmark" : "reload");
            ToolTipService.SetToolTip(reload, model.IsLoading ? "Stop" : "Reload (Ctrl+R)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reload, model.IsLoading ? "Stop" : "Reload");
        }, nameof(BrowserModel.CanGoBack), nameof(BrowserModel.CanGoForward), nameof(BrowserModel.CanReload), nameof(BrowserModel.IsLoading));
        Ui.Watch(model, () =>
        {
            if (addressBox.Text == model.AddressText) return;
            updatingAddress = true;
            addressBox.Text = model.AddressText;
            updatingAddress = false;
        }, nameof(BrowserModel.AddressText));
        Ui.Watch(model, () => addressBox.PlaceholderText = model.AddressPlaceholder, nameof(BrowserModel.AddressPlaceholder));
        Ui.Watch(model, () =>
        {
            addExtension.Visibility = Ui.Visible(model.ShowsAddExtension);
            addExtension.Content = model.AddExtensionLabel;
        }, nameof(BrowserModel.ShowsAddExtension), nameof(BrowserModel.AddExtensionLabel));
        Ui.Watch(model.Extensions, () => addExtension.IsEnabled = model.Extensions.IsIdle, nameof(ExtensionManager.IsIdle), nameof(ExtensionManager.IsBusy));
        Ui.Watch(model, () =>
        {
            foreach (ComboBoxItem item in searchEngines.Items)
            {
                if (item.Tag is SearchEngine engine && engine == model.SearchEngine) searchEngines.SelectedItem = item;
            }
        }, nameof(BrowserModel.SearchEngine));
        Ui.Watch(model, SyncEngines, nameof(BrowserModel.Engine), nameof(BrowserModel.IsGeckoEngine), nameof(BrowserModel.IsChromiumEngine));
        Ui.Watch(model, () =>
        {
            noticeCard.Child = Ui.Text(model.Notice ?? "", 14, "TextFillColorPrimaryBrush");
            notice.IsOpen = model.HasNotice && XamlRoot is not null;
        }, nameof(BrowserModel.Notice), nameof(BrowserModel.HasNotice));
        Ui.Watch(model, () => SyncExtensionsSheet(), nameof(BrowserModel.ShowExtensions));
        Ui.Watch(model, SyncBlockerFlyout, nameof(BrowserModel.ShowBlocker));
        Ui.Watch(model, BringSelectedTabIntoView, nameof(BrowserModel.SelectedTab));

        model.Tabs.CollectionChanged += Tabs_CollectionChanged;
        RebuildTabs();
        model.AddressFocusRequested += FocusAddressBox;

        Loaded += (_, _) =>
        {
            window.SetTitleBar(titleBar);
            window.SetInteractiveTitleBarElements(tabsHost);
            captionSpace.Width = new GridLength(window.CaptionButtonsWidth);
            FocusAddressBox();
        };
    }

    // ---- Building blocks ----

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static Button IconButton(string icon, string tip, Action click)
    {
        var button = new Button
        {
            Content = Icons.Make(icon, 14),
            Width = 34,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip.Split(" (")[0]);
        button.Click += (_, _) => click();
        return button;
    }

    private static ToggleButton EngineToggle(string name, Windows.UI.Color color)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(color) });
        content.Children.Add(new TextBlock { Text = name, FontSize = 12 });
        var toggle = new ToggleButton { Content = content, Padding = new Thickness(10, 4, 10, 5), MinHeight = 30 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, $"{name} engine");
        return toggle;
    }

    private bool IsWindowActive() => NativeWindowActive(window.Handle);

    private static bool NativeWindowActive(nint handle) => Hosting.NativeMethods.GetActiveWindow() == handle;

    // ---- Tab strip ----

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildTabs();

    private void RebuildTabs()
    {
        var current = new HashSet<BrowserTab>(model.Tabs);
        foreach (var (tab, (_, watch)) in chips.Where(pair => !current.Contains(pair.Key)).ToList())
        {
            watch.Dispose();
            chips.Remove(tab);
        }
        tabList.Children.Clear();
        foreach (var tab in model.Tabs)
        {
            if (!chips.TryGetValue(tab, out var chip))
            {
                chip = CreateChip(tab);
                chips[tab] = chip;
            }
            tabList.Children.Add(chip.Chip);
        }
        DispatcherQueue.TryEnqueue(BringSelectedTabIntoView);
    }

    private (Button, IDisposable) CreateChip(BrowserTab tab)
    {
        var dot = new Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ring = new ProgressRing { Width = 12, Height = 12, IsActive = false, Visibility = Visibility.Collapsed };
        var title = new TextBlock { FontSize = 12, Margin = new Thickness(7, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        var close = new Button
        {
            Content = Icons.Make("xmark", 10),
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(close, "Close Tab (Ctrl+W)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Close Tab");
        close.Click += (_, _) => model.Close(tab);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(dot);
        layout.Children.Add(ring);
        SetColumn(title, 1);
        layout.Children.Add(title);
        SetColumn(close, 2);
        layout.Children.Add(close);

        var chip = new Button
        {
            Content = layout,
            Width = TabWidth,
            Height = 34,
            Padding = new Thickness(10, 0, 4, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            BorderThickness = new Thickness(0),
        };
        chip.Click += (_, _) => model.Select(tab);
        // Middle click closes, as in every browser.
        chip.PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(chip).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) model.Close(tab);
        };

        var watch = Ui.Watch(tab, () =>
        {
            title.Text = tab.DisplayTitle;
            dot.Fill = new SolidColorBrush(tab.Engine == EngineKind.Chromium ? Ui.ChromiumColor : Ui.GeckoColor);
            dot.Visibility = Ui.Visible(!tab.IsLoading);
            ring.Visibility = Ui.Visible(tab.IsLoading);
            ring.IsActive = tab.IsLoading;
            ToolTipService.SetToolTip(chip, $"{tab.DisplayTitle}\n{tab.EngineHint}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, tab.AccessibleName);
            chip.Background = tab.IsSelected ? Ui.Brush("LayerFillColorDefaultBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        });
        return (chip, watch);
    }

    private void BringSelectedTabIntoView()
    {
        if (model.SelectedTab is { } tab && chips.TryGetValue(tab, out var chip) && chip.Chip.XamlRoot is not null)
        {
            chip.Chip.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private void TabScroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // The strip only scrolls sideways, so that is what the wheel does over it.
        var delta = e.GetCurrentPoint(tabScroller).Properties.MouseWheelDelta;
        tabScroller.ChangeView(tabScroller.HorizontalOffset - delta, null, null, disableAnimation: true);
        e.Handled = true;
    }

    // ---- Address field ----

    private void FocusAddressBox()
    {
        // In a scripted run Mosaic's window is normally in the background, and taking focus there
        // is the first step towards taking it from the person using the PC.
        if (!model.AllowsFocusChanges && !IsWindowActive()) return;
        if (addressBox.XamlRoot is null) return;
        addressBox.Focus(FocusState.Programmatic);
        addressBox.SelectAll();
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                var input = addressBox.Text;
                // Leave the field first (which puts the current address back), then navigate:
                // the page, not the field, is where the user is headed.
                model.EndAddressEditing();
                model.Navigate(input);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                model.EndAddressEditing();
                model.FocusContent();
                break;
        }
    }

    // ---- Notice ----

    private void PlaceNotice(FrameworkElement page)
    {
        notice.HorizontalOffset = Math.Max(0, (page.ActualWidth - noticeCard.ActualWidth) / 2);
        notice.VerticalOffset = Math.Max(0, page.ActualHeight - noticeCard.ActualHeight - 14);
    }
}
