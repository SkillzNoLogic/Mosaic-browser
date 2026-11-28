using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Browser;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// The engines' page area, which the winui area provides (its window's <c>ContentArea</c>
/// element: the WebView2 views, the Gecko host and the start page). The shell only places it.
/// </summary>
/// <remarks>
/// TODO(shell): read <c>MainWindow.ContentArea</c> directly once winui's milestone M1 lands on
/// win-next; until then it is found by name so this compiles against the skeleton window.
/// </remarks>
internal static class PageArea
{
    public static UIElement Resolve(Window window, BrowserModel model)
    {
        foreach (var name in new[] { "ContentArea", "PageArea" })
        {
            var property = window.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(window) is UIElement element)
            {
                // The element may still sit in the winui area's own fallback layout.
                if (element is FrameworkElement { Parent: Panel panel }) panel.Children.Remove(element);
                else if (element is FrameworkElement { Parent: Border border }) border.Child = null;
                else if (element is FrameworkElement { Parent: ContentControl control }) control.Content = null;
                IsPlaceholder = false;
                return element;
            }
        }
        IsPlaceholder = true;
        return new Grid
        {
            Children =
            {
                new TextBlock
                {
                    Text = "The page area appears here (provided by the window).",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.6,
                },
            },
        };
    }

    /// <summary>The window had no page area to give (skeleton window).</summary>
    public static bool IsPlaceholder { get; private set; }
}
