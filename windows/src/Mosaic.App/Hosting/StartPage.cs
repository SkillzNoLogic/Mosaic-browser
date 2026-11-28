using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.Core.Browser;
using Windows.System;

namespace Mosaic.App.Hosting;

/// <summary>What a blank tab shows: Mosaic's mark and a search field. Port of the v0.1 StartPage.</summary>
/// <remarks>The productivity area's Speed Dial replaces it through <c>FeatureMounts</c> later.</remarks>
internal sealed class StartPage : Grid
{
    private readonly BrowserModel model;
    private readonly TextBox query;

    public StartPage(BrowserModel model)
    {
        this.model = model;
        Background = Ui.Brush("LayerFillColorDefaultBrush");
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(16, 0, 16, 0) };
        SetRow(column, 1);
        Children.Add(column);

        column.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Mosaic.png"))) { DecodePixelWidth = 144 },
            Width = 72,
            Height = 72,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        column.Children.Add(new TextBlock
        {
            Text = "Mosaic",
            Margin = new Thickness(0, 14, 0, 22),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        query = new TextBox
        {
            Width = 520,
            MaxWidth = 520,
            Height = 42,
            FontSize = 15,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(query, "Search or enter address");
        query.KeyDown += Query_KeyDown;
        column.Children.Add(query);

        var caption = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) };
        var text = Ui.Text("", 14, "TextFillColorSecondaryBrush");
        caption.Children.Add(dot);
        caption.Children.Add(text);
        column.Children.Add(caption);

        Ui.Watch(model, () =>
        {
            query.PlaceholderText = model.AddressPlaceholder;
            text.Text = model.StartPageCaption;
            dot.Fill = new SolidColorBrush(model.IsChromiumEngine ? Ui.ChromiumColor : Ui.GeckoColor);
        }, nameof(BrowserModel.AddressPlaceholder), nameof(BrowserModel.StartPageCaption), nameof(BrowserModel.Engine));
    }

    private void Query_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var text = query.Text;
        // One start page serves every blank tab; it must not greet the next one with this query.
        query.Text = "";
        model.Navigate(text);
    }
}
