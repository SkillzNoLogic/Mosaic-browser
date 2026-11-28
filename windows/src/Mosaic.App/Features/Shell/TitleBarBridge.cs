using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// Makes whichever shell row is at the top right the window's title bar: it drags the window
/// (and double-click maximises, Snap Layouts on the maximise button), while the tabs, buttons and
/// fields inside it stay clickable (passthrough regions), and the caption buttons follow the
/// Mosaic's light/dark colours. Uses the Windows App SDK's own APIs (<c>Window.SetTitleBar</c>,
/// <c>InputNonClientPointerSource</c>, <c>AppWindowTitleBar</c>).
/// </summary>
/// <remarks>
/// TODO(shell): switch to the winui area's title-bar API once its milestone M1 lands; until then
/// the shell drives the window's title bar itself.
/// </remarks>
internal sealed class TitleBarBridge
{
    private readonly Window window;
    private readonly WindowApi api;
    private FrameworkElement? row;
    private bool scheduled;

    public TitleBarBridge(Window window)
    {
        this.window = window;
        api = new WindowApi(window);
    }

    /// <summary>Space the caption buttons take at the right, in DIPs.</summary>
    public double CaptionInset
    {
        get
        {
            if (api.CaptionButtonsWidth is > 0 and var width) return width;
            try
            {
                var scale = window.Content?.XamlRoot?.RasterizationScale ?? 1;
                var inset = window.AppWindow.TitleBar.RightInset / scale;
                return inset > 0 ? inset : 138;
            }
            catch
            {
                return 138;
            }
        }
    }

    /// <summary>The number of passthrough rectangles last set (for <c>dump</c>).</summary>
    public int PassthroughCount { get; private set; }

    public double CaptionHeight { get; private set; }

    public void Attach(FrameworkElement captionRow, double height, ShellTheme theme)
    {
        if (!ReferenceEquals(row, captionRow))
        {
            if (row is not null) row.SizeChanged -= OnRowSizeChanged;
            row = captionRow;
            row.SizeChanged += OnRowSizeChanged;
        }
        try
        {
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(captionRow);
            var bar = window.AppWindow.TitleBar;
            var tall = height >= 46;
            bar.PreferredHeightOption = tall ? TitleBarHeightOption.Tall : TitleBarHeightOption.Standard;
            CaptionHeight = tall ? 48 : 32;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = theme.PrimaryText.Color;
            bar.ButtonHoverForegroundColor = theme.PrimaryText.Color;
            bar.ButtonPressedForegroundColor = theme.SecondaryText.Color;
            bar.ButtonInactiveForegroundColor = theme.DisabledText.Color;
            bar.ButtonHoverBackgroundColor = theme.Hover.Color;
            bar.ButtonPressedBackgroundColor = theme.Pressed.Color;
        }
        catch
        {
            // An older Windows App SDK runtime or a window without an AppWindow: the system title bar stays.
        }
        Schedule();
    }

    private void OnRowSizeChanged(object sender, SizeChangedEventArgs e) => Schedule();

    /// <summary>Recomputes the clickable parts of the title row soon (coalesced).</summary>
    public void Schedule()
    {
        if (scheduled) return;
        scheduled = true;
        window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            scheduled = false;
            UpdatePassthrough();
        });
    }

    private void UpdatePassthrough()
    {
        if (row?.XamlRoot is not { } root) return;
        var scale = root.RasterizationScale;
        var rects = new List<RectInt32>();
        var controls = new List<FrameworkElement>();
        Collect(row, rects, controls, scale);
        try
        {
            // The window keeps the regions up to date itself when it can.
            if (api.SetInteractiveTitleBarElements([.. controls]))
            {
                PassthroughCount = controls.Count;
                return;
            }
        }
        catch
        {
            // Fall back to setting the regions here.
        }
        try
        {
            var source = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
            PassthroughCount = rects.Count;
        }
        catch
        {
            PassthroughCount = -1;
        }
    }

    /// <summary>Every visible control in the row (not descending into controls) stays clickable.</summary>
    private static void Collect(DependencyObject element, List<RectInt32> rects, List<FrameworkElement> controls, double scale)
    {
        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(element, i) is not FrameworkElement child) continue;
            if (child.Visibility != Visibility.Visible || child.ActualWidth <= 0 || child.ActualHeight <= 0) continue;
            if (child is Control { IsHitTestVisible: true })
            {
                controls.Add(child);
                try
                {
                    var bounds = child.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, child.ActualWidth, child.ActualHeight));
                    rects.Add(new RectInt32(
                        (int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                        (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale)));
                }
                catch (ArgumentException)
                {
                    // Not in the tree any more.
                }
                continue;
            }
            Collect(child, rects, controls, scale);
        }
    }
}
