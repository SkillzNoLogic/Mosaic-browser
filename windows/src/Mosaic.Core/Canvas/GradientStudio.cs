using Mosaic.Core.Canvas.Interim;

// The Gradient Studio's working state and edits (macOS keeps these in GradientStudioView's
// @State; here they live in Core so the WinUI view stays thin and automation and tests can
// drive the studio exactly as clicks do). While the target already shows a gradient, edits
// apply live, coalesced into one undo step per gesture; a drag applies when it ends.
// Owner: canvas.

namespace Mosaic.Core.Canvas;

public sealed class GradientStudio
{
    private readonly CanvasController canvas;
    private bool loaded;

    internal GradientStudio(CanvasController canvas)
    {
        this.canvas = canvas;
        Gradient = GradientPresets.MeshPresets[0].Gradient;
    }

    /// <summary>The gradient being edited (a copy; applied to the target live when it is in use).</summary>
    public GradientSpec Gradient { get; private set; }

    /// <summary>A source ID (mesh) or "stop-&lt;index&gt;".</summary>
    public string? Selection { get; private set; }

    /// <summary>Width ÷ height of the main window, so the canvas is Mosaic's shape.</summary>
    public double Aspect { get; set; } = 16.0 / 10;

    /// <summary>While a handle is dragged, the window waits for the drop.</summary>
    public bool Dragging { get; private set; }

    public CanvasController.GradientTarget Target => canvas.Target;

    public bool IsInUse => canvas.UsesGradient(Target);

    /// <summary>The target already shows exactly this gradient ("In Use").</summary>
    public bool IsApplied => IsInUse && Gradients.Same(canvas.Background(Target).Gradient, Gradient.Clamped());

    public bool IsMesh => Gradient.Kind == GradientKind.Mesh;

    public int? SelectedSourceIndex
    {
        get
        {
            if (!IsMesh) return null;
            var index = Gradient.Sources.FindIndex(s => s.Id == Selection);
            return index < 0 ? null : index;
        }
    }

    public int? SelectedStopIndex =>
        !IsMesh && Selection is { } s && s.StartsWith("stop-", StringComparison.Ordinal)
        && int.TryParse(s.AsSpan(5), out var index) && index >= 0 && index < Gradient.Stops.Count ? index : null;

    /// <summary>The target's gradient (or the first preset when it has none), selection cleared.</summary>
    public void Load()
    {
        var current = canvas.Background(Target);
        if (canvas.UsesGradient(Target) || current.Kind == BackgroundKind.Gradient) Gradient = current.Gradient.Clone();
        else if (!loaded) Gradient = GradientPresets.MeshPresets[0].Gradient;
        Selection = null;
        loaded = true;
    }

    /// <summary>Undo, a preset switch or a configuration changed the target's gradient.</summary>
    internal void LayoutChanged()
    {
        if (!loaded || !IsInUse || Dragging) return;
        var target = canvas.Background(Target).Gradient;
        if (!Gradients.Same(target, Gradient.Clamped()))
        {
            Gradient = target.Clone();
            if (SelectedSourceIndex is null && SelectedStopIndex is null) Selection = null;
        }
    }

    private void Edited()
    {
        if (IsInUse && !Dragging) canvas.UseGradient(Gradient, Target, coalesce: "gradient");
        canvas.Studio_Changed();
    }

    private void Change(Action<GradientSpec> change)
    {
        var copy = Gradient.Clone();
        change(copy);
        Gradient = copy;
        Edited();
    }

    /// <summary>"Use as Background": applies to the target (one undo step).</summary>
    public void Use() => canvas.UseGradient(Gradient, Target);

    public void Select(string? selection)
    {
        Selection = selection;
        canvas.Studio_Changed();
    }

    /// <summary>Selects colour <paramref name="index"/> (source or stop), as a click does.</summary>
    public bool SelectIndex(int index)
    {
        if (IsMesh && index >= 0 && index < Gradient.Sources.Count) Select(Gradient.Sources[index].Id);
        else if (!IsMesh && index >= 0 && index < Gradient.Stops.Count) Select($"stop-{index}");
        else return false;
        return true;
    }

    public void ChangeKind(GradientKind kind)
    {
        var next = Gradient.Clone();
        if (next.Kind != GradientKind.Mesh && next.Stops.Count < 2) next.Stops = next.NormalizedStops;
        next = next.Converted(kind);
        if (kind != GradientKind.Mesh && next.Stops.Count < 2) next.Stops = next.NormalizedStops;
        Gradient = next;
        Selection = null;
        Edited();
    }

    /// <summary>Uses a preset's design, keeping the finish (grain, strength, Raw) the user chose.</summary>
    public bool ApplyPreset(string name)
    {
        if (GradientPresets.Named(name) is not { } preset) return false;
        preset.Noise = Gradient.Noise;
        preset.Opacity = Gradient.Opacity;
        preset.Raw = Gradient.Raw;
        Gradient = preset;
        Selection = null;
        Edited();
        return true;
    }

    public void BeginDrag() => Dragging = true;

    /// <summary>A drag ended: apply it now (live edits waited for the drop).</summary>
    public void EndDrag()
    {
        if (!Dragging) return;
        Dragging = false;
        Edited();
    }

    public void MoveSource(string id, double x, double y)
    {
        var i = Gradient.Sources.FindIndex(s => s.Id == id);
        if (i < 0) return;
        Selection = id;
        Change(g => { g.Sources[i].X = Math.Clamp(x, 0, 1); g.Sources[i].Y = Math.Clamp(y, 0, 1); });
    }

    /// <summary>Adds a source showing the colour already at that point (nothing jumps) and selects it.</summary>
    public bool AddSource(double x, double y)
    {
        if (Gradient.Sources.Count >= GradientSpec.MaxSources) return false;
        var colour = Gradient.MeshColor(x, y, Aspect);
        var source = Gradients.NewSource(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), colour.Hex);
        Selection = source.Id;
        Change(g =>
        {
            if (g.Sources.Count == 0) g.Sources = [.. g.MeshSources];
            g.Sources.Add(source);
        });
        return true;
    }

    public bool AddStop(double location)
    {
        if (Gradient.Stops.Count >= GradientSpec.MaxStops) return false;
        var t = Math.Clamp(double.IsFinite(location) ? location : 0.5, 0, 1);
        Change(g =>
        {
            if (g.Stops.Count < 2) g.Stops = g.NormalizedStops;
            g.Stops.Add(new GradientStop(g.StopColor(t).Hex, t));
            Selection = $"stop-{g.Stops.Count - 1}";
        });
        return true;
    }

    public bool CanRemoveSelected =>
        (SelectedSourceIndex is not null && Gradient.Sources.Count > 1) || (SelectedStopIndex is not null && Gradient.NormalizedStops.Count > 2);

    public void RemoveSelected()
    {
        if (SelectedSourceIndex is { } source && Gradient.Sources.Count > 1)
        {
            Selection = null;
            Change(g => g.Sources.RemoveAt(source));
        }
        else if (SelectedStopIndex is { } stop && Gradient.NormalizedStops.Count > 2)
        {
            Selection = null;
            Change(g => g.Stops.RemoveAt(stop));
        }
    }

    /// <summary>Arrow keys move the selected source (or stop) by <paramref name="step"/> of the canvas.</summary>
    public bool Nudge(CanvasController.KeyDirection key, double step)
    {
        if (SelectedSourceIndex is { } i)
        {
            Change(g =>
            {
                var s = g.Sources[i];
                switch (key)
                {
                    case CanvasController.KeyDirection.Left: s.X = Math.Max(s.X - step, 0); break;
                    case CanvasController.KeyDirection.Right: s.X = Math.Min(s.X + step, 1); break;
                    case CanvasController.KeyDirection.Up: s.Y = Math.Max(s.Y - step, 0); break;
                    default: s.Y = Math.Min(s.Y + step, 1); break;
                }
            });
            return true;
        }
        if (SelectedStopIndex is { } j && key is CanvasController.KeyDirection.Left or CanvasController.KeyDirection.Right)
        {
            var delta = key == CanvasController.KeyDirection.Left ? -step : step;
            Change(g => g.Stops[j].Location = Math.Clamp(g.Stops[j].Location + delta, 0, 1));
            return true;
        }
        return false;
    }

    public void SetSourceColor(string id, ColorValue color)
    {
        var i = Gradient.Sources.FindIndex(s => s.Id == id);
        if (i < 0 || string.Equals(Gradient.Sources[i].Color, color.Hex, StringComparison.OrdinalIgnoreCase)) return;
        Change(g => g.Sources[i].Color = color.Hex);
    }

    public void SetSpread(string id, double spread)
    {
        var i = Gradient.Sources.FindIndex(s => s.Id == id);
        if (i >= 0) Change(g => g.Sources[i].Spread = spread);
    }

    public void SetStopColor(int index, ColorValue color)
    {
        if (index < 0 || index >= Gradient.Stops.Count || string.Equals(Gradient.Stops[index].Color, color.Hex, StringComparison.OrdinalIgnoreCase)) return;
        Change(g => g.Stops[index].Color = color.Hex);
    }

    public void SetStopLocation(int index, double location)
    {
        if (index < 0 || index >= Gradient.Stops.Count) return;
        Selection = $"stop-{index}";
        Change(g => g.Stops[index].Location = Math.Clamp(location, 0, 1));
    }

    /// <summary>The selected colour (source or stop), if any.</summary>
    public ColorValue? SelectedColor =>
        SelectedSourceIndex is { } i ? Gradient.Sources[i].ColorValue()
        : SelectedStopIndex is { } j ? ColorValue.FromHex(Gradient.Stops[j].Color) ?? ColorValue.Fallback
        : null;

    public void SetSelectedColor(ColorValue color)
    {
        if (SelectedSourceIndex is { } i) SetSourceColor(Gradient.Sources[i].Id, color);
        else if (SelectedStopIndex is { } j) SetStopColor(j, color);
    }

    public void SetSharpness(double value) => Change(g => g.Sharpness = value);
    public void SetAngle(double value) => Change(g => g.Angle = value);
    public void SetCenter(double x, double y) => Change(g => { g.CenterX = Math.Clamp(x, 0, 1); g.CenterY = Math.Clamp(y, 0, 1); });
    public void SetNoise(double value) => Change(g => g.Noise = value);
    public void SetOpacity(double value) => Change(g => g.Opacity = value);
    public void SetRaw(bool value) => Change(g => g.Raw = value);

    /// <summary>Replaces the whole gradient (a gradient file, automation's <c>gradientSet</c>).</summary>
    public void Replace(GradientSpec gradient)
    {
        Gradient = gradient.Clone();
        if (SelectedSourceIndex is null && SelectedStopIndex is null) Selection = null;
        Edited();
    }
}
