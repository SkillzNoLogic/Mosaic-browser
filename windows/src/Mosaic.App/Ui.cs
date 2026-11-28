using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mosaic.App;

/// <summary>
/// Small helpers for building UI in code (there is no XAML): theme resources, and following a
/// property of a model object without data binding.
/// </summary>
/// <remarks>
/// Views follow the model with <see cref="Watch"/> rather than <c>{Binding}</c>: the model
/// lives in <c>Mosaic.Core</c> (plain .NET, no WinRT attributes), and an explicit handler is
/// cheaper and trim-safe.
/// </remarks>
internal static class Ui
{
    /// <summary>Mozilla's engine colour (the dot next to a Gecko tab).</summary>
    public static readonly Color GeckoColor = Color.FromArgb(0xFF, 0xE6, 0x60, 0x00);

    /// <summary>Chromium's engine colour.</summary>
    public static readonly Color ChromiumColor = Color.FromArgb(0xFF, 0x1A, 0x73, 0xE8);

    /// <summary>A brush from the Fluent theme resources (<c>XamlControlsResources</c>), with a fallback.</summary>
    public static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));

    public static Style? Style(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    /// <summary>
    /// Calls <paramref name="update"/> now and whenever one of <paramref name="properties"/> of
    /// <paramref name="source"/> changes (or all of them, an empty name). Dispose to stop.
    /// </summary>
    public static IDisposable Watch(INotifyPropertyChanged source, Action update, params string[] properties)
    {
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName) || properties.Length == 0 || Array.IndexOf(properties, e.PropertyName) >= 0)
            {
                update();
            }
        };
        source.PropertyChanged += handler;
        update();
        return new Subscription(() => source.PropertyChanged -= handler);
    }

    public static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static TextBlock Text(string text, double size = 14, string? brush = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (brush is not null) block.Foreground = Brush(brush);
        return block;
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;

        public void Dispose()
        {
            dispose?.Invoke();
            dispose = null;
        }
    }
}
