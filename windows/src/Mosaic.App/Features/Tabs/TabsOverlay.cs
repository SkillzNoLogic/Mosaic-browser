using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Browser;
using Windows.Foundation;
using Windows.UI;

namespace Mosaic.App.Features.Tabs;

/// <summary>
/// The tabs area's overlay over the content area (port of <c>TabsOverlay.swift</c>): the tab
/// search panel (Ctrl+Shift+A), the "already open in another tab" notice and the crash-recovery
/// banner. Where it draws nothing it takes no clicks.
/// </summary>
/// <remarks>
/// Tab search dims the page and takes the keyboard, so it covers the page
/// (<see cref="FeatureMounts.CoverPage"/>: Gecko's window is hidden meanwhile). The two notices
/// must not hide the page for the ten seconds they are up, so they are windowed popups
/// (<see cref="Popup.ShouldConstrainToRootBounds"/> false), which Windows draws above Gecko's
/// child window.
/// </remarks>
public sealed class TabsOverlay : Grid
{
    private const string SearchCover = "tabs.search";

    private readonly BrowserModel model;
    private readonly Grid dim = new() { Background = new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), Visibility = Visibility.Collapsed };
    private readonly TabSearchPanel search;
    private readonly Popup duplicatePopup = new() { ShouldConstrainToRootBounds = false };
    private readonly Popup crashPopup = new() { ShouldConstrainToRootBounds = false };
    private readonly TextBlock duplicateText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock crashDetail = new() { Foreground = TabsStyle.Secondary, TextWrapping = TextWrapping.Wrap };
    private readonly Button switchButton = new() { Content = "Switch to Tab" };
    private bool attached;

    public TabsOverlay(BrowserModel model)
    {
        this.model = model;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        // No background: the empty overlay lets clicks through to whatever is below.

        search = new TabSearchPanel(model) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0) };
        AutomationProperties.SetName(dim, "Close tab search");
        dim.Tapped += (_, e) =>
        {
            // Only a tap on the dimmed area itself, not one inside the panel.
            if (ReferenceEquals(e.OriginalSource, dim)) model.HideTabSearch();
        };
        dim.Children.Add(search);
        Children.Add(dim);

        duplicatePopup.Child = DuplicateNotice();
        crashPopup.Child = CrashBanner();

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, _) => PlacePopups();
    }

    private void Attach()
    {
        if (attached) return;
        attached = true;
        model.PropertyChanged += ModelChanged;
        model.TabSearchFocusRequested += FocusSearch;
        duplicatePopup.XamlRoot = XamlRoot;
        crashPopup.XamlRoot = XamlRoot;
        Update();
    }

    private void Detach()
    {
        if (!attached) return;
        attached = false;
        model.PropertyChanged -= ModelChanged;
        model.TabSearchFocusRequested -= FocusSearch;
        duplicatePopup.IsOpen = false;
        crashPopup.IsOpen = false;
        FeatureMounts.CoverPage(SearchCover, false);
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserModel.IsTabSearchVisible) or nameof(BrowserModel.DuplicateOffer)
            or nameof(BrowserModel.OffersCrashRecovery) or nameof(BrowserModel.PreviousSessionPageCount))
        {
            Update();
        }
    }

    private void Update()
    {
        var searching = model.IsTabSearchVisible;
        var wasSearching = dim.Visibility == Visibility.Visible;
        dim.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        FeatureMounts.CoverPage(SearchCover, searching);
        if (searching && !wasSearching && model.AllowsFocusChanges) FocusSearch();

        if (model.DuplicateOffer is { } offer)
        {
            duplicateText.Text = offer.Message;
            ToolTipService.SetToolTip(switchButton, offer.ClosesNewTab ? "Go to the open tab and close this one" : "Go to the open tab");
        }
        var count = model.PreviousSessionPageCount;
        crashDetail.Text = $"Restore the {TabsStyle.Plural(count, "tab", "tabs")} from your last session?";
        PlacePopups();
        duplicatePopup.IsOpen = model.DuplicateOffer is not null && XamlRoot is not null;
        crashPopup.IsOpen = model.OffersCrashRecovery && XamlRoot is not null;
    }

    private void FocusSearch()
    {
        if (model.IsTabSearchVisible) DispatcherQueue.TryEnqueue(search.FocusQuery);
    }

    /// <summary>Centres the notices over the overlay's area: the banner near the top, the notice near the bottom.</summary>
    private void PlacePopups()
    {
        if (XamlRoot is null || ActualWidth <= 0) return;
        Point origin;
        try
        {
            origin = TransformToVisual(null).TransformPoint(new Point(0, 0));
        }
        catch (ArgumentException)
        {
            return;
        }
        Place(crashPopup, origin, top: true);
        Place(duplicatePopup, origin, top: false);
    }

    private void Place(Popup popup, Point origin, bool top)
    {
        if (popup.Child is not FrameworkElement child) return;
        child.Measure(new Size(Math.Min(560, ActualWidth), double.PositiveInfinity));
        var size = child.DesiredSize;
        popup.HorizontalOffset = origin.X + Math.Max(0, (ActualWidth - size.Width) / 2);
        popup.VerticalOffset = origin.Y + (top ? 16 : Math.Max(0, ActualHeight - size.Height - 48));
    }

    /// <summary>"“page” is already open in another tab. [Switch to Tab] [×]" (non-blocking; ten seconds).</summary>
    private Border DuplicateNotice()
    {
        switchButton.Style = Application.Current?.Resources.TryGetValue("AccentButtonStyle", out var style) == true ? style as Style : null;
        switchButton.Click += (_, _) => model.AcceptDuplicateOffer();
        var dismiss = new Button
        {
            Content = TabsStyle.Icon("xmark", "×", 10),
            Padding = new Thickness(6),
            Background = new SolidColorBrush(TabsStyle.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(dismiss, "Dismiss");
        ToolTipService.SetToolTip(dismiss, "Dismiss");
        dismiss.Click += (_, _) => model.DismissDuplicateOffer();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(14, 9, 10, 9) };
        var icon = TabsStyle.Icon("square.on.square", "⧉", 14);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        row.Children.Add(duplicateText);
        row.Children.Add(switchButton);
        row.Children.Add(dismiss);
        var card = TabsStyle.FloatingCard(row, 10);
        AutomationProperties.SetName(card, "Already open in another tab");
        return card;
    }

    /// <summary>Shown after a crash when the startup setting is "Start page".</summary>
    private Border CrashBanner()
    {
        var notNow = new Button { Content = "Not Now" };
        notNow.Click += (_, _) => model.DismissCrashRecovery();
        var restore = new Button { Content = "Restore Tabs" };
        if (Application.Current?.Resources.TryGetValue("AccentButtonStyle", out var style) == true) restore.Style = style as Style;
        restore.Click += (_, _) => model.RestorePreviousSession();

        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = TabsStyle.Icon("exclamationmark.arrow.circlepath", "⚠", 20);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Mosaic quit unexpectedly", FontWeight = FontWeights.SemiBold });
        text.Children.Add(crashDetail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(notNow);
        buttons.Children.Add(restore);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        var card = TabsStyle.FloatingCard(grid, 10);
        card.Width = 560;
        AutomationProperties.SetName(card, "Mosaic quit unexpectedly. Restore tabs from your last session?");
        return card;
    }
}
