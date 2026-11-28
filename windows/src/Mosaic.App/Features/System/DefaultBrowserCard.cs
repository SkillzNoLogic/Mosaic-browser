using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.App.Features.System;

/// <summary>
/// "Default browser" settings block, for the shell area's General page:
/// <c>panel.Children.Add(DefaultBrowserCard.Create());</c>. Shows whether Mosaic is the default,
/// and a button that registers Mosaic (per user) and opens Settings › Default apps at Mosaic's
/// page, where Windows lets the user choose it. The status refreshes when the card is shown and
/// whenever Mosaic's window is activated again (coming back from Settings). Registry reads run
/// off the UI thread.
/// </summary>
public static class DefaultBrowserCard
{
    public static FrameworkElement Create()
    {
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { Icons.Make("defaultbrowser"), new TextBlock { Text = "Make Mosaic the default browser" } },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Make Mosaic the default browser");
        var settingsLink = new HyperlinkButton { Content = "Open Default apps settings", Padding = new Thickness(0) };

        var card = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Default browser", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                status,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { button, settingsLink } },
            },
        };

        async void Refresh()
        {
            var current = await Task.Run(() => DefaultBrowser.Shared.Status());
            Show(current);
        }

        void Show(DefaultBrowserStatus current)
        {
            status.Text = current switch
            {
                { IsDefault: true } => "Mosaic is your default browser.",
                { RegisteredForThisCopy: true } => "Mosaic is registered. Choose it under Web browser in Settings › Default apps to make it your default.",
                _ => "Mosaic is not your default browser.",
            };
            button.Visibility = current.IsDefault ? Visibility.Collapsed : Visibility.Visible;
        }

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                var current = await Task.Run(() => DefaultBrowser.Shared.Register());
                DefaultBrowser.Shared.OpenSettings();
                Show(current);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        settingsLink.Click += (_, _) => DefaultBrowser.Shared.OpenSettings();

        void OnActivated(object sender, WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) Refresh();
        }
        card.Loaded += (_, _) =>
        {
            Refresh();
            if (SystemIntegration.MainWindow is { } window) window.Activated += OnActivated;
        };
        card.Unloaded += (_, _) =>
        {
            if (SystemIntegration.MainWindow is { } window) window.Activated -= OnActivated;
        };
        return card;
    }
}
