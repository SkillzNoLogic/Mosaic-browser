using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;

namespace Mosaic.App;

/// <summary>
/// The content blocker's shield in the toolbar and its popover: the master switch, the switch
/// for the site in front, what was blocked, and the state of the engine and its lists. Same
/// controls and wording as <c>Sources/Mosaic/Views/BlockerPopover.swift</c>, as in v0.1.
/// </summary>
internal sealed partial class BasicShell
{
    private Flyout? blockerFlyout;
    private Button? blockerButton;

    private Button CreateBlockerButton()
    {
        var shield = Icons.Make("shield", 14);
        // Neither icon font has a crossed-out shield, so the stroke is drawn.
        var stroke = new Line { X1 = 2, Y1 = 14, X2 = 14, Y2 = 2, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed };
        var glyph = new Grid { Width = 16, Height = 16 };
        glyph.Children.Add(shield);
        glyph.Children.Add(stroke);
        var badge = new TextBlock { Margin = new Thickness(4, 0, 0, 0), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0) };
        content.Children.Add(glyph);
        content.Children.Add(badge);

        var button = blockerButton = new Button
        {
            Content = content,
            MinWidth = 34,
            Height = 32,
            Padding = new Thickness(4, 0, 4, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Content blocker");

        // Windowed, so it opens above Gecko's page window.
        var flyout = blockerFlyout = new Flyout
        {
            Content = CreateBlockerPopover(),
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false,
        };
        flyout.Opened += (_, _) => model.ShowBlocker = true;
        flyout.Closed += (_, _) => model.ShowBlocker = false;
        button.Click += (_, _) => model.ShowBlocker = !model.ShowBlocker;

        Ui.Watch(model, () =>
        {
            var shieldState = model.BlockerShield;
            shield.Foreground = shieldState == BlockerShield.Filtering ? Ui.Brush("AccentFillColorDefaultBrush") : Ui.Brush("TextFillColorPrimaryBrush");
            stroke.Stroke = shield.Foreground;
            glyph.Opacity = shieldState == BlockerShield.Off ? 0.55 : 1;
            stroke.Visibility = Ui.Visible(shieldState == BlockerShield.Off);
            badge.Text = model.BlockerBadge;
            badge.Visibility = Ui.Visible(model.BlockerBadge.Length > 0);
            ToolTipService.SetToolTip(button, model.BlockerHelp);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button, model.BlockerHelp);
        }, nameof(BrowserModel.BlockerShield), nameof(BrowserModel.BlockerBadge), nameof(BrowserModel.BlockerHelp));
        return button;
    }

    private void SyncBlockerFlyout()
    {
        if (blockerFlyout is not { } flyout || blockerButton is not { } button) return;
        if (model.ShowBlocker && !flyout.IsOpen && button.XamlRoot is not null)
        {
            // A scripted run opens it in the background and expects it to stay until it closes it.
            flyout.ShowMode = model.AllowsFocusChanges ? FlyoutShowMode.Standard : FlyoutShowMode.Transient;
            flyout.ShowAt(button);
        }
        else if (!model.ShowBlocker && flyout.IsOpen)
        {
            flyout.Hide();
        }
    }

    private FrameworkElement CreateBlockerPopover()
    {
        var blocker = model.Blocker;
        var panel = new StackPanel { Width = 300 };
        panel.Children.Add(new TextBlock { Text = "Content Blocker", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var intro = Ui.Text("Built in. Uses uBlock Origin’s filtering engine and filter lists.", 12, "TextFillColorSecondaryBrush");
        intro.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(intro);

        // Not part of this build.
        var missing = Ui.Text("Not included in this build. Run scripts\\bootstrap.ps1 and rebuild.", 13, "SystemFillColorCautionBrush");
        missing.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(missing);

        var controls = new StackPanel();
        panel.Children.Add(controls);
        var enabled = new CheckBox { Content = "Block ads and trackers", Margin = new Thickness(0, 12, 0, 0) };
        enabled.Click += (_, _) => model.SetBlockerEnabled(enabled.IsChecked == true);
        controls.Children.Add(enabled);

        // Only for a web page: the site in front.
        var site = new CheckBox { Margin = new Thickness(0, 8, 0, 0) };
        site.Click += (_, _) =>
        {
            if (model.SelectedTab is { } tab) model.SetBlocking(site.IsChecked == true, tab);
        };
        var blocked = Ui.Text("", 13);
        blocked.Margin = new Thickness(0, 8, 0, 0);
        controls.Children.Add(site);
        controls.Children.Add(blocked);

        controls.Children.Add(new Rectangle { Height = 1, Margin = new Thickness(0, 12, 0, 12), Fill = Ui.Brush("ControlStrokeColorDefaultBrush") });

        var status = new Grid();
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var statusLine = Ui.Text("", 12);
        var listsLine = Ui.Text("", 12, "TextFillColorSecondaryBrush");
        lines.Children.Add(statusLine);
        lines.Children.Add(listsLine);
        status.Children.Add(lines);
        var update = new Button { Content = "Update Lists", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        // Safe as async: UpdateListsAsync handles whatever a download or a file write can throw.
        update.Click += async (_, _) => await blocker.UpdateListsAsync();
        var updating = new ProgressRing { Width = 20, Height = 20, Margin = new Thickness(12, 0, 0, 0), IsActive = false };
        Grid.SetColumn(update, 1);
        Grid.SetColumn(updating, 1);
        status.Children.Add(update);
        status.Children.Add(updating);
        controls.Children.Add(status);

        var footnote = Ui.Text("Blocks network requests. It does not yet hide leftover page elements or run uBlock Origin’s scriptlets, so some ads (video ads in particular) can still appear.", 12, "TextFillColorSecondaryBrush");
        footnote.Margin = new Thickness(0, 12, 0, 0);
        controls.Children.Add(footnote);

        Ui.Watch(blocker, () =>
        {
            missing.Visibility = Ui.Visible(!blocker.IsAvailable);
            controls.Visibility = Ui.Visible(blocker.IsAvailable);
            enabled.IsChecked = blocker.IsEnabled;
            site.IsEnabled = blocker.IsEnabled;
            statusLine.Text = blocker.StatusLine;
            listsLine.Text = blocker.ListsLine;
            update.Visibility = Ui.Visible(!blocker.IsUpdating);
            updating.Visibility = Ui.Visible(blocker.IsUpdating);
            updating.IsActive = blocker.IsUpdating;
        });
        Ui.Watch(model, () =>
        {
            site.Visibility = Ui.Visible(model.HasBlockerSite);
            site.Content = model.BlockerSiteLabel;
            site.IsChecked = model.IsBlockingSite;
            blocked.Text = model.BlockedCountText;
            blocked.Visibility = Ui.Visible(model.HasBlockerSite && model.IsFilteringPage);
        }, nameof(BrowserModel.HasBlockerSite), nameof(BrowserModel.BlockerSite), nameof(BrowserModel.BlockerSiteLabel),
           nameof(BrowserModel.IsBlockingSite), nameof(BrowserModel.BlockedCountText), nameof(BrowserModel.IsFilteringPage));
        return panel;
    }
}
