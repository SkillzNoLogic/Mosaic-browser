using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Browser;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// The engines' page area, which the winui area provides (<see cref="MainWindow.ContentArea"/>:
/// the WebView2 views, the Gecko host, the start page and Mosaic's own mosaic:// pages). The
/// shell only places it, and never moves it once placed.
/// </summary>
internal static class PageArea
{
    public static UIElement Resolve(Window window, BrowserModel model)
    {
        if (window is MainWindow { ContentArea: { } area })
        {
            // Remove it from any previous parent first (ContentArea's contract).
            if (area.Parent is Panel panel) panel.Children.Remove(area);
            else if (area.Parent is Border border) border.Child = null;
            IsPlaceholder = false;
            return area;
        }
        IsPlaceholder = true;
        return new TextBlock
        {
            Text = "The page area appears here (provided by the window).",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
        };
    }

    /// <summary>The window had no page area to give (not Mosaic's main window).</summary>
    public static bool IsPlaceholder { get; private set; }
}
