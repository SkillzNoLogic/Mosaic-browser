using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;
using static Mosaic.App.Features.Privacy.PrivacyUi;

namespace Mosaic.App.Features.Privacy;

/// <summary>
/// The content blocker's toolbar component (layout component id "blocker"): the shield with the
/// page's blocked count, and a flyout with the master and per-site switches and the page's
/// dashboard (what was blocked, by which filter, from which list). Port of macOS's
/// <c>BlockerButton</c>/<c>BlockerPopover</c> with the dashboard.
/// </summary>
internal sealed class BlockerButton : UserControl
{
    private const string Cover = "privacy.blocker";

    private readonly BrowserModel model;
    private readonly FontIcon shield = Icon("", 16);
    private readonly TextBlock badge = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button button;
    private readonly List<IDisposable> watches = [];

    public BlockerButton(BrowserModel model)
    {
        this.model = model;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        content.Children.Add(shield);
        content.Children.Add(badge);
        button = new Button
        {
            Content = content,
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, "Content blocker");
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        flyout.Opening += (_, _) =>
        {
            FeatureMounts.CoverPage(Cover, true);
            model.ShowBlocker = true;
            flyout.Content = new BlockerPanel(model, () => flyout.Hide());
        };
        flyout.Closed += (_, _) =>
        {
            FeatureMounts.CoverPage(Cover, false);
            model.ShowBlocker = false;
            flyout.Content = null;
        };
        button.Flyout = flyout;
        Content = button;
        Loaded += (_, _) => watches.Add(Watch(model, Update,
            nameof(BrowserModel.BlockerShield), nameof(BrowserModel.BlockerBadge), nameof(BrowserModel.BlockerHelp)));
        Unloaded += (_, _) =>
        {
            foreach (var watch in watches) watch.Dispose();
            watches.Clear();
        };
    }

    private void Update()
    {
        badge.Text = model.BlockerBadge;
        badge.Visibility = model.BlockerBadge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        shield.Opacity = model.BlockerShield == BlockerShield.Filtering ? 1 : 0.55;
        // Shield (EA18) when on, a struck shield's stand-in (the outline, dimmed) when off.
        shield.Glyph = model.BlockerShield == BlockerShield.Off ? "" : "";
        ToolTipService.SetToolTip(button, model.BlockerHelp);
        AutomationProperties.SetHelpText(button, model.BlockerHelp);
    }
}

/// <summary>The blocker flyout's content; built each time it opens, so the dashboard is current.</summary>
internal sealed class BlockerPanel : UserControl
{
    public BlockerPanel(BrowserModel model, Action close)
    {
        var blocker = model.Blocker;
        var panel = new StackPanel { Spacing = 10, Width = 360 };
        panel.Children.Add(Text("Content Blocker", 16, bold: true));
        if (!blocker.IsAvailable)
        {
            panel.Children.Add(Caption("The content blocker is not included in this build."));
            Content = panel;
            return;
        }
        Toggle(panel, "Block ads and trackers", blocker.StatusLine, blocker.IsEnabled, on => model.SetBlockerEnabled(on));
        if (model.HasBlockerSite && model.SelectedTab is { } tab)
        {
            var site = Toggle(panel, model.BlockerSiteLabel, "Turning it off reloads the page.", model.IsBlockingSite, on => model.SetBlocking(on, tab));
            site.IsEnabled = blocker.IsEnabled;
        }
        panel.Children.Add(Text(model.BlockedCountText, 13));

        var list = new StackPanel { Spacing = 6 };
        var scroller = new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(scroller, "Blocked requests on this page");
        panel.Children.Add(scroller);
        if (model.SelectedTab is { } current && model.IsFilteringPage)
        {
            list.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left });
            _ = FillAsync(blocker, current, list);
        }

        var settings = Button("Privacy & Security Settings…", () =>
        {
            close();
            ShellBridge.ShowSettings(model, SettingsCategory.Privacy);
            model.ShowPrivacySettings();
        });
        settings.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(settings);
        Content = panel;
    }

    private static async Task FillAsync(ContentBlocker blocker, BrowserTab tab, StackPanel list)
    {
        var requests = await blocker.BlockedRequestsAsync(tab);
        list.Children.Clear();
        if (requests.Count == 0)
        {
            list.Children.Add(Caption("Nothing has been blocked on this page."));
            return;
        }
        foreach (var group in requests.GroupBy(request => request.Host).OrderByDescending(group => group.Sum(request => request.Count)))
        {
            var expander = new Expander
            {
                Header = Row(Text(group.Key, 13), Caption(group.Sum(request => request.Count).ToString(global::System.Globalization.CultureInfo.CurrentCulture))),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(expander, $"{group.Key}, {group.Sum(request => request.Count)} blocked");
            var details = new StackPanel { Spacing = 6 };
            foreach (var request in group)
            {
                var item = new StackPanel { Spacing = 1 };
                item.Children.Add(new TextBlock { Text = request.Url, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true });
                var why = request.Filter.Length == 0 ? request.Type : $"{request.Type} · {request.Filter}" + (request.List is { } source ? $" ({source})" : "");
                item.Children.Add(Caption(request.Count > 1 ? $"{why} · ×{request.Count}" : why));
                details.Children.Add(item);
            }
            expander.Content = details;
            list.Children.Add(expander);
        }
    }
}
