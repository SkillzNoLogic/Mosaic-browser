using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.App.Features.System;
using Mosaic.Core.Canvas;
using Mosaic.Core.Canvas.Interim;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Colors = Microsoft.UI.Colors;
using Key = Mosaic.Core.Canvas.CanvasController.KeyDirection;
using Target = Mosaic.Core.Canvas.CanvasController.GradientTarget;

namespace Mosaic.App.Features.Canvas;

/// <summary>
/// The Gradient Studio (macOS GradientStudioView, from Eminence): a canvas the shape of the
/// window where colour sources are placed and dragged (blending live), or a linear, radial or
/// angular gradient with stops; each colour has the full colour chart; grain, strength and Raw;
/// presets. Applies to the window, the New Tab page or the current workspace. The studio's state
/// lives in Core (<see cref="GradientStudio"/>); the canvas surface and stop bar persist and are
/// updated in place, so a drag keeps its pointer while the window follows live.
/// </summary>
internal sealed class GradientStudioView : UserControl, ICanvasView
{
    private readonly CanvasController canvas;
    private readonly GradientStudio studio;

    // Persistent pieces (updated in place).
    private readonly Grid surface = new() { CornerRadius = new CornerRadius(18) };
    private readonly GradientBackground surfaceFill;
    private readonly Microsoft.UI.Xaml.Controls.Canvas handles = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly ContentControl surfaceHost;
    private readonly Grid stopBar = new() { Height = 34 };
    private readonly GradientFill stopFill;
    private readonly Microsoft.UI.Xaml.Controls.Canvas stopHandles = new() { Background = new SolidColorBrush(Colors.Transparent) };

    // Rebuilt when the structure changes.
    private readonly ContentControl toolbarHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl presetsHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl inspectorHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly List<(Border Frame, GradientSpec Preset)> presetFrames = [];
    private ColorChartEditor? chart;
    private Slider? accessorySlider;
    private TextBlock? accessoryValue, angleValue, grainValue, strengthValue;
    private Slider? blendSlider, angleSlider, grainSlider, strengthSlider;
    private Button? useButton;
    private string structure = "";
    private bool updating;
    private string? dragging;  // a source ID, "stop-n", "angle" or "centre"

    public GradientStudioView(CanvasController canvas)
    {
        this.canvas = canvas;
        studio = canvas.Studio;
        studio.Aspect = WindowAspect();
        studio.Load();

        surfaceFill = new GradientBackground(new BackgroundSpec(BackgroundKind.Gradient, gradient: studio.Gradient));
        surface.Children.Add(surfaceFill);
        surface.Children.Add(handles);
        surface.BorderBrush = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
        surface.BorderThickness = new Thickness(1);
        surfaceHost = new ContentControl { Content = surface, IsTabStop = true, UseSystemFocusVisuals = true, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(surfaceHost, "Gradient canvas");
        AutomationProperties.SetAutomationId(surfaceHost, "gradient-canvas");
        surfaceHost.KeyDown += OnKey;
        WireSurface();

        stopFill = new GradientFill(studio.Gradient, 256) { Height = 22, Margin = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(6) };
        stopBar.Children.Add(stopFill);
        stopBar.Children.Add(stopHandles);
        WireStopBar();

        var editor = new StackPanel { Spacing = 14, Padding = new Thickness(20) };
        editor.Children.Add(toolbarHost);
        var sizer = new Grid();
        sizer.Children.Add(surfaceHost);
        sizer.SizeChanged += (_, e) => SizeSurface(e.NewSize.Width);
        editor.Children.Add(sizer);
        editor.Children.Add(stopBar);
        editor.Children.Add(presetsHost);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        root.Children.Add(new ScrollViewer { Content = editor });
        var inspector = new ScrollViewer { Content = new Border { Padding = new Thickness(20), Child = inspectorHost }, BorderBrush = CanvasUi.CardStroke, BorderThickness = new Thickness(1, 0, 0, 0) };
        Grid.SetColumn(inspector, 1);
        root.Children.Add(inspector);
        Content = root;
    }

    private static double WindowAspect()
    {
        if (CanvasMounts.Window?.Bounds is { Height: > 0 } bounds) return Math.Clamp(bounds.Width / bounds.Height, 0.6, 3);
        return 16.0 / 10;
    }

    private void SizeSurface(double available)
    {
        if (available <= 0) return;
        var height = Math.Min(360, available / studio.Aspect);
        surface.Height = height;
        surface.Width = height * studio.Aspect;
        PlaceHandles();
    }

    // MARK: Refresh

    public void Refresh()
    {
        var g = studio.Gradient;
        var a = canvas.Layout.Appearance;
        var key = $"{g.Kind}|{studio.Selection}|{g.Sources.Count}|{g.Stops.Count}|{canvas.Target}|{a.NewTabBackground.Mode}|{a.WorkspaceBackgrounds.ContainsKey(canvas.WorkspaceID)}|{canvas.WorkspaceName}";
        if (key != structure)
        {
            structure = key;
            toolbarHost.Content = Toolbar();
            presetsHost.Content = Presets();
            inspectorHost.Content = Inspector();
        }
        updating = true;
        try
        {
            surfaceFill.Spec = new BackgroundSpec(BackgroundKind.Gradient, gradient: g);
            stopBar.Visibility = studio.IsMesh ? Visibility.Collapsed : Visibility.Visible;
            stopFill.Gradient = new GradientSpec(GradientKind.Linear, 0, [.. g.NormalizedStops]);
            if (useButton is not null)
            {
                useButton.IsEnabled = !studio.IsApplied;
                useButton.Content = CanvasUi.Row(6, Icons.Make(studio.IsInUse ? "checkmark" : "paintbrush", 14),
                    new TextBlock { Text = studio.IsInUse ? (studio.IsApplied ? "In Use" : "Apply Changes") : "Use as Background" });
            }
            if (blendSlider is not null) blendSlider.Value = g.Sharpness;
            if (angleSlider is not null) angleSlider.Value = g.Angle;
            if (angleValue is not null) angleValue.Text = $"{(int)Math.Round(g.Angle)}°";
            if (grainSlider is not null) grainSlider.Value = g.Noise;
            if (grainValue is not null) grainValue.Text = $"{(int)Math.Round(g.Noise * 100)}%";
            if (strengthSlider is not null) strengthSlider.Value = g.Opacity;
            if (strengthValue is not null) strengthValue.Text = $"{(int)Math.Round(g.Opacity * 100)}%";
            if (studio.SelectedColor is { } colour) chart?.SetColor(colour);
            if (studio.SelectedSourceIndex is { } si)
            {
                if (accessorySlider is not null) accessorySlider.Value = g.Sources[si].Spread;
                if (accessoryValue is not null) accessoryValue.Text = $"{g.Sources[si].Spread:0.0}×";
            }
            else if (studio.SelectedStopIndex is { } ti)
            {
                if (accessorySlider is not null) accessorySlider.Value = g.Stops[ti].Location;
                if (accessoryValue is not null) accessoryValue.Text = $"{(int)Math.Round(g.Stops[ti].Location * 100)}%";
            }
            foreach (var (frame, preset) in presetFrames)
            {
                var chosen = g.SameDesign(preset);
                frame.BorderBrush = chosen ? CanvasUi.Accent : new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
                frame.BorderThickness = new Thickness(chosen ? 2.5 : 1);
            }
            PlaceHandles();
            PlaceStopHandles();
        }
        finally
        {
            updating = false;
        }
    }

    // MARK: Toolbar

    private UIElement Toolbar()
    {
        var g = studio.Gradient;
        var title = CanvasUi.Column(2, CanvasUi.Title("Gradient Studio"), CanvasUi.Caption(studio.IsMesh
            ? "Double-click to add a colour · drag to move · Delete removes"
            : "Click the bar to add a stop · drag stops and handles · Delete removes"));
        Target[] targets = [Target.Window, Target.NewTab, Target.Workspace];
        string[] names = ["Window", "New Tab Page", $"Workspace “{canvas.WorkspaceName}”"];
        var target = new ComboBox { SelectedIndex = Array.IndexOf(targets, canvas.Target) };
        foreach (var n in names) target.Items.Add(n);
        target.SelectionChanged += (_, _) => { if (target.SelectedIndex >= 0) canvas.Target = targets[target.SelectedIndex]; };
        useButton = CanvasUi.Accented(CanvasUi.Button("Use as Background", "paintbrush", studio.Use));
        var top = new Grid { ColumnSpacing = 10 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(title);
        var right = CanvasUi.Row(8, CanvasUi.Labelled("Apply to", target), useButton);
        right.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(right, 1);
        top.Children.Add(right);

        var kind = CanvasUi.Segmented("Gradient kind",
            [(GradientKind.Mesh, "Free-form"), (GradientKind.Linear, "Linear"), (GradientKind.Radial, "Radial"), (GradientKind.Angular, "Angular")],
            g.Kind, studio.ChangeKind);
        var raw = new ToggleSwitch { Header = null, OnContent = "Raw", OffContent = "Raw", IsOn = g.Raw };
        AutomationProperties.SetName(raw, "Raw");
        ToolTipService.SetToolTip(raw, "Show the gradient exactly as it is in light and dark mode, instead of dimming or lifting it to keep the toolbar legible");
        raw.Toggled += (_, _) => { if (!updating) studio.SetRaw(raw.IsOn); };
        var second = new Grid();
        second.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        second.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        second.Children.Add(kind);
        Grid.SetColumn(raw, 1);
        second.Children.Add(raw);

        var actions = CanvasUi.Row(10);
        blendSlider = angleSlider = null;
        angleValue = null;
        if (studio.IsMesh)
        {
            var add = CanvasUi.Button("Add Colour", "plus.circle", () => studio.AddSource(0.5, 0.5));
            add.IsEnabled = g.Sources.Count < GradientSpec.MaxSources;
            var remove = CanvasUi.Button("Remove", "minus.circle", studio.RemoveSelected);
            remove.IsEnabled = studio.CanRemoveSelected;
            blendSlider = CanvasUi.Slider("Blend", Gradients.SharpnessRange.Min, Gradients.SharpnessRange.Max, g.Sharpness, v => { if (!updating) studio.SetSharpness(v); });
            ToolTipService.SetToolTip(blendSlider, "How sharply the colours meet: soft to defined");
            actions.Children.Add(add);
            actions.Children.Add(remove);
            actions.Children.Add(CanvasUi.Labelled("Blend", blendSlider));
        }
        else
        {
            var add = CanvasUi.Button("Add Stop", "plus.circle", () => studio.AddStop(0.5));
            add.IsEnabled = g.Stops.Count < GradientSpec.MaxStops;
            var remove = CanvasUi.Button("Remove", "minus.circle", studio.RemoveSelected);
            remove.IsEnabled = studio.CanRemoveSelected;
            actions.Children.Add(add);
            actions.Children.Add(remove);
            if (g.Kind != GradientKind.Radial)
            {
                angleSlider = CanvasUi.Slider("Angle", 0, 360, g.Angle, v => { if (!updating) studio.SetAngle(Math.Round(v)); }, 140, 1);
                angleValue = CanvasUi.Text($"{(int)Math.Round(g.Angle)}°", 13, CanvasUi.Secondary, wrap: false);
                angleValue.VerticalAlignment = VerticalAlignment.Center;
                actions.Children.Add(CanvasUi.Labelled("Angle", angleSlider));
                actions.Children.Add(angleValue);
            }
        }
        return CanvasUi.Column(12, top, second, actions);
    }

    // MARK: Surface (sources, angle knob, centre)

    private Point ToSurface(double x, double y) => new(x * surface.ActualWidth, y * surface.ActualHeight);

    private (double X, double Y) FromSurface(Point p) =>
        (Math.Clamp(p.X / Math.Max(surface.ActualWidth, 1), 0, 1), Math.Clamp(p.Y / Math.Max(surface.ActualHeight, 1), 0, 1));

    private Point Origin()
    {
        var g = studio.Gradient;
        return g.Kind == GradientKind.Linear ? ToSurface(0.5, 0.5) : ToSurface(g.CenterX, g.CenterY);
    }

    private Point Knob()
    {
        var origin = Origin();
        var radians = studio.Gradient.Angle * Math.PI / 180;
        var reach = Math.Min(surface.ActualWidth, surface.ActualHeight) * 0.36;
        return new Point(origin.X + Math.Cos(radians) * reach, origin.Y + Math.Sin(radians) * reach);
    }

    private static double Distance(Point a, Point b) => double.Hypot(a.X - b.X, a.Y - b.Y);

    /// <summary>What a press at <paramref name="p"/> picks: the nearest handle within reach.</summary>
    private string? HitTest(Point p)
    {
        var g = studio.Gradient;
        if (studio.IsMesh)
        {
            string? best = null;
            var bestDistance = 24.0;
            foreach (var source in g.Sources)
            {
                var d = Distance(p, ToSurface(source.X, source.Y));
                if (d < bestDistance) (best, bestDistance) = (source.Id, d);
            }
            return best;
        }
        if (g.Kind != GradientKind.Radial && Distance(p, Knob()) < 20) return "angle";
        if (g.Kind != GradientKind.Linear && Distance(p, Origin()) < 22) return "centre";
        return null;
    }

    private void WireSurface()
    {
        ColorChartEditor.Drag(handles, p =>
        {
            if (dragging is null)
            {
                dragging = HitTest(p) ?? "";
                if (dragging.Length > 0)
                {
                    studio.BeginDrag();
                    if (studio.IsMesh) studio.Select(dragging);
                }
                surfaceHost.Focus(FocusState.Pointer);
            }
            var (x, y) = FromSurface(p);
            switch (dragging)
            {
                case "": break;
                case "angle":
                    var origin = Origin();
                    var degrees = Math.Atan2(p.Y - origin.Y, p.X - origin.X) * 180 / Math.PI;
                    if (degrees < 0) degrees += 360;
                    studio.SetAngle(Math.Round(degrees));
                    break;
                case "centre": studio.SetCenter(x, y); break;
                default: studio.MoveSource(dragging, x, y); break;
            }
        }, () =>
        {
            if (dragging is { Length: > 0 }) studio.EndDrag();
            else if (dragging == "") studio.Select(null);
            dragging = null;
        });
        handles.DoubleTapped += (_, e) =>
        {
            if (!studio.IsMesh) return;
            var (x, y) = FromSurface(e.GetPosition(handles));
            studio.AddSource(x, y);
            e.Handled = true;
        };
    }

    private void PlaceHandles()
    {
        if (surface.ActualWidth <= 0) return;
        handles.Children.Clear();
        var g = studio.Gradient;
        var white = new SolidColorBrush(Colors.White);
        if (studio.IsMesh)
        {
            for (var i = 0; i < g.Sources.Count; i++)
            {
                var source = g.Sources[i];
                var selected = studio.Selection == source.Id;
                var size = selected ? 34.0 : 28.0;
                var dot = new Ellipse
                {
                    Width = size, Height = size, Fill = CanvasUi.ColorBrush(source.Color), Stroke = white, StrokeThickness = selected ? 4 : 2.5,
                    Translation = new global::System.Numerics.Vector3(0, 0, 8),
                };
                var at = ToSurface(source.X, source.Y);
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(dot, at.X - size / 2);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(dot, at.Y - size / 2);
                AutomationProperties.SetName(dot, $"Colour {i + 1}, {source.Color}");
                AutomationProperties.SetItemStatus(dot, $"{(int)(source.X * 100)} percent across, {(int)(source.Y * 100)} percent down");
                handles.Children.Add(dot);
            }
            return;
        }
        var origin = Origin();
        if (g.Kind != GradientKind.Radial)
        {
            var knob = Knob();
            var back = new Point(2 * origin.X - knob.X, 2 * origin.Y - knob.Y);
            handles.Children.Add(new Line { X1 = back.X, Y1 = back.Y, X2 = knob.X, Y2 = knob.Y, Stroke = white, StrokeThickness = 2, StrokeDashArray = [5, 4], Opacity = 0.85 });
            var dot = new Ellipse { Width = 18, Height = 18, Fill = white };
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(dot, knob.X - 9);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(dot, knob.Y - 9);
            AutomationProperties.SetName(dot, $"Angle, {(int)g.Angle} degrees");
            handles.Children.Add(dot);
        }
        if (g.Kind != GradientKind.Linear)
        {
            var centre = new Ellipse { Width = 28, Height = 28, Fill = new SolidColorBrush(Color.FromArgb(77, 255, 255, 255)), Stroke = white, StrokeThickness = 2.5 };
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(centre, origin.X - 14);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(centre, origin.Y - 14);
            AutomationProperties.SetName(centre, "Centre");
            handles.Children.Add(centre);
        }
    }

    private static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var step = Down(VirtualKey.Shift) ? 0.1 : 0.02;
        var handled = e.Key switch
        {
            VirtualKey.Delete or VirtualKey.Back => Do(studio.RemoveSelected),
            VirtualKey.Left => studio.Nudge(Key.Left, step),
            VirtualKey.Right => studio.Nudge(Key.Right, step),
            VirtualKey.Up => studio.Nudge(Key.Up, step),
            VirtualKey.Down => studio.Nudge(Key.Down, step),
            _ => false,
        };
        e.Handled = handled;
    }

    private static bool Do(Action action)
    {
        action();
        return true;
    }

    // MARK: Stop bar

    private void WireStopBar()
    {
        var width = () => Math.Max(stopBar.ActualWidth - 16, 1);
        ColorChartEditor.Drag(stopHandles, p =>
        {
            var t = Math.Clamp((p.X - 8) / width(), 0, 1);
            if (dragging is null)
            {
                var g = studio.Gradient;
                var nearest = -1;
                var best = 12.0;
                for (var i = 0; i < g.Stops.Count; i++)
                {
                    var d = Math.Abs(8 + Math.Clamp(g.Stops[i].Location, 0, 1) * width() - p.X);
                    if (d < best) (nearest, best) = (i, d);
                }
                if (nearest < 0)
                {
                    studio.AddStop(t);  // a click on the bar adds a stop there
                    dragging = "";
                    return;
                }
                dragging = $"stop-{nearest}";
                studio.BeginDrag();
                studio.Select(dragging);
            }
            if (dragging is { Length: > 5 } && int.TryParse(dragging.AsSpan(5), out var index)) studio.SetStopLocation(index, t);
        }, () =>
        {
            if (dragging is { Length: > 0 }) studio.EndDrag();
            dragging = null;
        });
        stopBar.SizeChanged += (_, _) => PlaceStopHandles();
    }

    private void PlaceStopHandles()
    {
        stopHandles.Children.Clear();
        if (studio.IsMesh || stopBar.ActualWidth <= 0) return;
        var width = Math.Max(stopBar.ActualWidth - 16, 1);
        var g = studio.Gradient;
        for (var i = 0; i < g.Stops.Count; i++)
        {
            var stop = g.Stops[i];
            var selected = studio.Selection == $"stop-{i}";
            var handle = new Rectangle { Width = 16, Height = 30, RadiusX = 4, RadiusY = 4, Fill = CanvasUi.ColorBrush(stop.Color), Stroke = new SolidColorBrush(Colors.White), StrokeThickness = selected ? 3 : 2 };
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(handle, 8 + Math.Clamp(stop.Location, 0, 1) * width - 8);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(handle, 2);
            AutomationProperties.SetName(handle, $"Stop {i + 1}, {stop.Color}");
            AutomationProperties.SetItemStatus(handle, $"{(int)(stop.Location * 100)} percent");
            stopHandles.Children.Add(handle);
        }
    }

    // MARK: Presets

    private UIElement Presets()
    {
        presetFrames.Clear();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(0, 0, 0, 6) };
        foreach (var (name, preset) in GradientPresets.All)
        {
            var thumbnail = new GradientFill(preset, GradientRaster.ThumbnailWidth) { Width = 96, Height = 96 / Math.Max(studio.Aspect, 0.5) };
            var frame = new Border { Child = thumbnail, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
            presetFrames.Add((frame, preset));
            var button = new Button { Content = CanvasUi.Column(5, frame, CanvasUi.Caption(name)), Padding = new Thickness(0), Background = null, BorderThickness = new Thickness(0) };
            var chosen = name;
            button.Click += (_, _) => studio.ApplyPreset(chosen);
            AutomationProperties.SetName(button, $"{name} preset");
            ToolTipService.SetToolTip(button, $"{name}: {preset.Colors.Count} colours");
            row.Children.Add(button);
        }
        return CanvasUi.Column(8, CanvasUi.Heading("Presets"), new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
        });
    }

    // MARK: Inspector

    private UIElement Inspector()
    {
        chart = null;
        accessorySlider = null;
        accessoryValue = null;
        var g = studio.Gradient;
        var column = new StackPanel { Spacing = 18 };
        if (studio.SelectedSourceIndex is { } si)
        {
            var id = g.Sources[si].Id;
            accessoryValue = CanvasUi.Text($"{g.Sources[si].Spread:0.0}×", 13, CanvasUi.Secondary, wrap: false);
            accessorySlider = CanvasUi.Slider("Reach", Gradients.SpreadRange.Min, Gradients.SpreadRange.Max, g.Sources[si].Spread,
                v => { if (!updating) studio.SetSpread(id, v); }, 320);
            chart = new ColorChartEditor($"Colour {si + 1} of {g.Sources.Count}", g.Sources[si].ColorValue(),
                c => studio.SetSourceColor(id, c), Accessory("Reach", accessoryValue, accessorySlider));
            column.Children.Add(chart);
        }
        else if (studio.SelectedStopIndex is { } ti)
        {
            accessoryValue = CanvasUi.Text($"{(int)Math.Round(g.Stops[ti].Location * 100)}%", 13, CanvasUi.Secondary, wrap: false);
            accessorySlider = CanvasUi.Slider("Position", 0, 1, g.Stops[ti].Location, v => { if (!updating) studio.SetStopLocation(ti, v); }, 320);
            chart = new ColorChartEditor($"Stop {ti + 1} of {g.Stops.Count}", ColorValue.FromHex(g.Stops[ti].Color) ?? ColorValue.Fallback,
                c => studio.SetStopColor(ti, c), Accessory("Position", accessoryValue, accessorySlider));
            column.Children.Add(chart);
        }
        else
        {
            column.Children.Add(ColourList());
        }
        column.Children.Add(new Border { Height = 1, Background = CanvasUi.CardStroke });
        column.Children.Add(Finish());
        if (TargetOptions() is { } options) column.Children.Add(options);
        return column;
    }

    private static UIElement Accessory(string label, TextBlock value, Slider slider)
    {
        var header = new Grid();
        header.Children.Add(CanvasUi.Text(label, 14));
        value.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(value);
        slider.Width = double.NaN;
        slider.HorizontalAlignment = HorizontalAlignment.Stretch;
        return CanvasUi.Column(4, header, slider);
    }

    private UIElement ColourList()
    {
        var g = studio.Gradient;
        var column = new StackPanel { Spacing = 12 };
        column.Children.Add(CanvasUi.Heading(studio.IsMesh ? "Pick a colour source" : "Pick a stop"));
        column.Children.Add(CanvasUi.Caption(studio.IsMesh
            ? $"Click a circle on the canvas to change its colour in any encoding (HEX, RGB, HSB, HSL, CMYK, LAB or OKLCH) and how far it reaches. Double-click empty space to add another, up to {GradientSpec.MaxSources}. Colours fade into each other and update as you drag."
            : "Click a stop under the canvas to change its colour in any encoding and where it sits. Click the bar to add a stop where you clicked."));
        var colours = studio.IsMesh
            ? g.Sources.Select((s, i) => (Id: s.Id, Hex: s.Color, Label: $"Colour {i + 1}")).ToList()
            : g.Stops.Select((s, i) => (Id: $"stop-{i}", Hex: s.Color, Label: $"Stop {i + 1}")).ToList();
        column.Children.Add(CanvasUi.Text($"{colours.Count} colours", 14, bold: true));
        foreach (var (id, hex, label) in colours)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Ellipse { Width = 18, Height = 18, Fill = CanvasUi.ColorBrush(hex), Stroke = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)), StrokeThickness = 1 });
            var name = CanvasUi.Text(label, 14);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var code = new TextBlock { Text = hex, FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = CanvasUi.Secondary, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(code, 2);
            row.Children.Add(code);
            var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = null, BorderThickness = new Thickness(0) };
            var chosen = id;
            button.Click += (_, _) => studio.Select(chosen);
            AutomationProperties.SetName(button, $"{label}, {hex}");
            column.Children.Add(button);
        }
        return column;
    }

    /// <summary>Grain, strength and Raw: how the gradient sits in the window.</summary>
    private UIElement Finish()
    {
        var g = studio.Gradient;
        grainValue = CanvasUi.Text($"{(int)Math.Round(g.Noise * 100)}%", 13, CanvasUi.Secondary, wrap: false);
        grainSlider = CanvasUi.Slider("Grain", 0, 1, g.Noise, v => { if (!updating) studio.SetNoise(v); }, 320);
        strengthValue = CanvasUi.Text($"{(int)Math.Round(g.Opacity * 100)}%", 13, CanvasUi.Secondary, wrap: false);
        strengthSlider = CanvasUi.Slider("Strength", Gradients.OpacityRange.Min, Gradients.OpacityRange.Max, g.Opacity, v => { if (!updating) studio.SetOpacity(v); }, 320);
        return CanvasUi.Column(12,
            CanvasUi.Heading("Finish"),
            Accessory("Grain", grainValue, grainSlider),
            Accessory("Strength", strengthValue, strengthSlider),
            CanvasUi.Caption("Lower values let the system window colour, which follows light and dark mode, show through."),
            CanvasUi.Caption(g.Raw
                ? "Raw: shown exactly as chosen in light and dark mode."
                : "Light colours are dimmed in dark mode and dark ones lifted in light mode, so toolbar text stays legible. Turn on Raw to keep them exact."));
    }

    private UIElement? TargetOptions()
    {
        var a = canvas.Layout.Appearance;
        switch (canvas.Target)
        {
            case Target.NewTab:
                return CanvasUi.Column(8, CanvasUi.Heading("New Tab Page"),
                    CanvasUi.Segmented("New Tab background",
                        [(NewTabBackgroundMode.Plain, "Plain"), (NewTabBackgroundMode.Window, "Same as Window"), (NewTabBackgroundMode.Custom, "Own Background")],
                        a.NewTabBackground.Mode, m => canvas.Set(l => l.Appearance.NewTabBackground.Mode = m)),
                    CanvasUi.Caption("“Use as Background” gives the New Tab page its own gradient."));
            case Target.Workspace:
                var own = a.WorkspaceBackgrounds.ContainsKey(canvas.WorkspaceID);
                var clear = CanvasUi.Button("Use the Window’s Background", null, canvas.ClearWorkspaceBackground);
                clear.IsEnabled = own;
                return CanvasUi.Column(8, CanvasUi.Heading($"Workspace “{canvas.WorkspaceName}”"),
                    CanvasUi.Caption(own
                        ? "Has its own background; other workspaces keep the window’s."
                        : "Uses the window’s background. “Use as Background” gives it its own; switching workspaces then switches backgrounds."),
                    clear);
            default:
                return null;
        }
    }
}
