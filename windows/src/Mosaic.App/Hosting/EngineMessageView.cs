using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;

namespace Mosaic.App.Hosting;

/// <summary>
/// What the page area says when there is no page to show: an engine is starting, or the page
/// failed and can be reloaded. Shows <see cref="BrowserModel.Message"/>. Port of the v0.1 view.
/// </summary>
internal sealed class EngineMessageView : Grid
{
    public EngineMessageView(BrowserModel model)
    {
        Background = Ui.Brush("LayerFillColorDefaultBrush");
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 480, Padding = new Thickness(16) };
        Children.Add(column);

        // Exactly one of these three: working, failed, or simply waiting.
        var progress = new ProgressRing { Width = 28, Height = 28, IsActive = false };
        var failed = Icons.Make("exclamationmark.triangle", 30);
        failed.Glyph = "";
        var waiting = Icons.Make("globe", 30);
        waiting.Glyph = "";
        failed.HorizontalAlignment = waiting.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(progress);
        column.Children.Add(failed);
        column.Children.Add(waiting);

        var title = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        column.Children.Add(title);

        // Selectable, so an error can be copied.
        var detail = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            MaxWidth = 440,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = Ui.Brush("TextFillColorSecondaryBrush"),
        };
        column.Children.Add(detail);

        var reload = new Button { Content = "Reload", Margin = new Thickness(0, 16, 0, 0), MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Center };
        reload.Click += (_, _) => model.Reload();
        column.Children.Add(reload);

        Ui.Watch(model, () =>
        {
            var message = model.Message;
            bool busy = message?.ShowsProgress == true, failure = message?.IsFailure == true;
            progress.IsActive = busy && Visibility == Visibility.Visible;
            progress.Visibility = Ui.Visible(busy);
            failed.Visibility = Ui.Visible(failure);
            waiting.Visibility = Ui.Visible(message is not null && !busy && !failure);
            reload.Visibility = Ui.Visible(failure);
            title.Text = message?.Title ?? "";
            detail.Text = message?.Detail ?? "";
            detail.Visibility = Ui.Visible(message?.Detail is not null);
        }, nameof(BrowserModel.Message));
        // The ring animates only while it can be seen (no idle work).
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
            progress.IsActive = Visibility == Visibility.Visible && model.Message?.ShowsProgress == true);
    }
}
