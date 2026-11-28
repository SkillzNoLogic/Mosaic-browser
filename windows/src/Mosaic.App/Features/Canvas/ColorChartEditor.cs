using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.Core.Canvas;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Mosaic.App.Features.Canvas;

/// <summary>
/// The colour chart from Eminence (macOS ColorChartEditor): saturation/brightness square, hue
/// bar, the colour in every encoding (HEX, RGB, HSB, HSL, CMYK, LAB, OKLCH), all editable and
/// kept in sync, each copyable. Hue, saturation and brightness are kept separately so hue
/// survives at grey and black. (macOS's screen eyedropper has no Windows equivalent here.)
/// </summary>
internal sealed class ColorChartEditor : StackPanel
{
    /// <summary>The encoding last chosen (macOS keeps it in its defaults; here for the session).</summary>
    private static ColorEncoding encoding = ColorEncoding.Hex;

    private readonly Action<ColorValue> changed;
    private double hue, saturation, brightness;
    private readonly Grid square = new() { Height = 170, CornerRadius = new CornerRadius(10) };
    private readonly SolidColorBrush squareHue = new();
    private readonly Ellipse squareRing = new() { Width = 16, Height = 16, Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Grid hueBar = new() { Height = 18 };
    private readonly Ellipse hueThumb = new() { Width = 18, Height = 18, Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 2, IsHitTestVisible = false };
    private readonly ContentControl fieldsHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Grid all = new() { ColumnSpacing = 10, RowSpacing = 6 };
    private readonly List<TextBox> fields = [];
    private bool editing;

    public ColorChartEditor(string title, ColorValue color, Action<ColorValue> changed, UIElement? accessory = null)
    {
        this.changed = changed;
        Spacing = 14;
        Children.Add(CanvasUi.Heading(title));
        BuildSquare();
        BuildHueBar();
        Children.Add(square);
        Children.Add(hueBar);
        if (accessory is not null) Children.Add(accessory);
        BuildTabs();
        Children.Add(tabs);
        Children.Add(fieldsHost);
        Children.Add(new Border { Height = 1, Background = CanvasUi.CardStroke });
        all.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        all.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        all.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Children.Add(all);
        Load(color);
    }

    private ColorValue Value => ColorValue.FromHsb(hue, saturation, brightness);

    /// <summary>A new colour from outside (another source selected, undo); ignored while typing.</summary>
    public void SetColor(ColorValue color)
    {
        if (editing || color.Hex == Value.Hex) return;
        Load(color);
    }

    private void Load(ColorValue color)
    {
        var hsb = color.Hsb;
        if (hsb.S > 0 && hsb.B > 0) hue = hsb.H;  // keep the hue the user chose for greys and black
        saturation = hsb.S;
        brightness = hsb.B;
        Sync(rebuildFields: true);
    }

    private void Commit()
    {
        Sync(rebuildFields: !editing);
        changed(Value);
    }

    private void Sync(bool rebuildFields)
    {
        squareHue.Color = CanvasUi.ToColor(ColorValue.FromHsb(hue, 100, 100));
        PlaceThumbs();
        if (rebuildFields) BuildFields();
        BuildAll();
    }

    // MARK: Square and hue bar

    private void BuildSquare()
    {
        square.Children.Add(new Rectangle { Fill = squareHue });
        square.Children.Add(new Rectangle { Fill = new LinearGradientBrush(new GradientStopCollection
        {
            new() { Color = Microsoft.UI.Colors.White, Offset = 0 },
            new() { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1 },
        }, 0) });
        square.Children.Add(new Rectangle { Fill = new LinearGradientBrush(new GradientStopCollection
        {
            new() { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0 },
            new() { Color = Microsoft.UI.Colors.Black, Offset = 1 },
        }, 90) });
        var overlay = new Microsoft.UI.Xaml.Controls.Canvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        overlay.Children.Add(squareRing);
        square.Children.Add(overlay);
        square.SizeChanged += (_, _) => PlaceThumbs();
        Drag(overlay, p =>
        {
            saturation = Math.Clamp(p.X / Math.Max(square.ActualWidth, 1), 0, 1) * 100;
            brightness = (1 - Math.Clamp(p.Y / Math.Max(square.ActualHeight, 1), 0, 1)) * 100;
            Commit();
        });
        AutomationProperties.SetName(square, "Saturation and brightness");
        square.IsTabStop = true;
        square.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case VirtualKey.Up: brightness = Math.Min(brightness + 5, 100); break;
                case VirtualKey.Down: brightness = Math.Max(brightness - 5, 0); break;
                case VirtualKey.Right: saturation = Math.Min(saturation + 5, 100); break;
                case VirtualKey.Left: saturation = Math.Max(saturation - 5, 0); break;
                default: return;
            }
            e.Handled = true;
            Commit();
        };
    }

    private void BuildHueBar()
    {
        var stops = new GradientStopCollection();
        for (var i = 0; i <= 6; i++) stops.Add(new GradientStop { Color = CanvasUi.ToColor(ColorValue.FromHsb(i * 60, 100, 100)), Offset = i / 6.0 });
        hueBar.Children.Add(new Rectangle { Fill = new LinearGradientBrush(stops, 0), RadiusX = 9, RadiusY = 9 });
        var overlay = new Microsoft.UI.Xaml.Controls.Canvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        overlay.Children.Add(hueThumb);
        hueBar.Children.Add(overlay);
        hueBar.SizeChanged += (_, _) => PlaceThumbs();
        Drag(overlay, p =>
        {
            hue = Math.Clamp(p.X / Math.Max(hueBar.ActualWidth, 1), 0, 1) * 360;
            Commit();
        });
        AutomationProperties.SetName(hueBar, "Hue");
        hueBar.IsTabStop = true;
        hueBar.KeyDown += (_, e) =>
        {
            if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
            hue = (hue + (e.Key == VirtualKey.Right ? 10 : -10) + 360) % 360;
            e.Handled = true;
            Commit();
        };
    }

    private void PlaceThumbs()
    {
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(squareRing, saturation / 100 * square.ActualWidth - 8);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(squareRing, (1 - brightness / 100) * square.ActualHeight - 8);
        hueThumb.Fill = squareHue;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(hueThumb, hue / 360 * Math.Max(hueBar.ActualWidth - 18, 0));
        AutomationProperties.SetItemStatus(square, $"Saturation {(int)saturation} percent, brightness {(int)brightness} percent");
        AutomationProperties.SetItemStatus(hueBar, $"{(int)hue} degrees");
    }

    /// <summary>Press-and-drag on <paramref name="target"/>, reporting positions in its coordinates.</summary>
    internal static void Drag(UIElement target, Action<Point> at, Action? ended = null)
    {
        var down = false;
        target.PointerPressed += (_, e) =>
        {
            down = target.CapturePointer(e.Pointer);
            at(e.GetCurrentPoint(target).Position);
            e.Handled = true;
        };
        target.PointerMoved += (_, e) =>
        {
            if (!down) return;
            at(e.GetCurrentPoint(target).Position);
            e.Handled = true;
        };
        void End(PointerRoutedEventArgs e)
        {
            if (!down) return;
            down = false;
            target.ReleasePointerCapture(e.Pointer);
            ended?.Invoke();
        }
        target.PointerReleased += (_, e) => End(e);
        target.PointerCaptureLost += (_, _) =>
        {
            if (!down) return;
            down = false;
            ended?.Invoke();
        };
    }

    // MARK: Encodings

    private void BuildTabs()
    {
        tabs.Children.Clear();
        foreach (var item in Enum.GetValues<ColorEncoding>())
        {
            var button = new ToggleButton { Content = item.Raw(), IsChecked = item == encoding, Padding = new Thickness(8, 3, 8, 3), FontSize = 12 };
            AutomationProperties.SetName(button, $"Edit as {item.Raw()}");
            button.Click += (_, _) =>
            {
                encoding = item;
                BuildTabs();
                BuildFields();
            };
            tabs.Children.Add(button);
        }
    }

    private void BuildFields()
    {
        fields.Clear();
        var value = Value;
        if (encoding == ColorEncoding.Hex)
        {
            var box = new TextBox { Text = value.Hex, FontFamily = new FontFamily("Consolas") };
            AutomationProperties.SetName(box, "Hex colour");
            WireField(box, () => ColorValue.FromHex(box.Text));
            fieldsHost.Content = box;
            return;
        }
        var grid = new Grid { ColumnSpacing = 6 };
        var comps = encoding.Components();
        var values = encoding.Values(value);
        for (var i = 0; i < comps.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var box = new TextBox { Header = comps[i].Label, Text = ColorEncodings.F(values[i], comps[i].Digits) };
            AutomationProperties.SetName(box, $"{encoding.Raw()} {comps[i].Label}");
            Grid.SetColumn(box, i);
            grid.Children.Add(box);
            fields.Add(box);
        }
        foreach (var box in fields) WireField(box, () => encoding.Color(fields.Select(f => f.Text).ToList(), Value));
        fieldsHost.Content = grid;
    }

    private void WireField(TextBox box, Func<ColorValue?> parse)
    {
        void Apply()
        {
            if (parse() is not { } color) return;
            Load(color);
            changed(Value);
        }
        box.GotFocus += (_, _) => editing = true;
        box.LostFocus += (_, _) =>
        {
            editing = false;
            Apply();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            editing = false;
            Apply();
            e.Handled = true;
        };
    }

    private void BuildAll()
    {
        all.Children.Clear();
        all.RowDefinitions.Clear();
        var value = Value;
        var row = 0;
        foreach (var item in Enum.GetValues<ColorEncoding>())
        {
            all.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = item.String(value);
            var label = CanvasUi.Text(item.Raw(), 12, CanvasUi.Secondary, bold: true);
            var shown = new TextBlock { Text = text, FontFamily = new FontFamily("Consolas"), FontSize = 12, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            var copy = CanvasUi.Button($"Copy {item.Raw()}", "doc.on.doc", () =>
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            }, iconOnly: true);
            copy.Padding = new Thickness(6, 3, 6, 3);
            Grid.SetRow(label, row);
            Grid.SetRow(shown, row);
            Grid.SetColumn(shown, 1);
            Grid.SetRow(copy, row);
            Grid.SetColumn(copy, 2);
            all.Children.Add(label);
            all.Children.Add(shown);
            all.Children.Add(copy);
            row++;
        }
    }
}
