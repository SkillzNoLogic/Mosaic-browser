using Mosaic.Core.Canvas.Interim;

// State and actions of the layout canvas, the Gradient Studio and named configurations, ported
// from macOS (Sources/Mosaic/Features/Canvas/CanvasController.swift) into Core so the WinUI
// views, the keyboard and automation all drive the same, unit-tested code. Every change goes
// through ICanvasLayoutHost.ReplaceActiveLayout: the window updates live, nothing reloads.
// Single-threaded (the UI thread), like the macOS @MainActor class; file writes go to the
// thread pool. Owner: canvas.

namespace Mosaic.Core.Canvas;

public sealed class CanvasController
{
    /// <summary>The editor's modes (Presets is the shell's Mosaics page).</summary>
    public enum EditorMode { Presets, Layout, Gradient, Configurations }

    public enum KeyDirection { Left, Right, Up, Down }

    /// <summary>Which background the Gradient Studio edits.</summary>
    public enum GradientTarget { Window, NewTab, Workspace }

    /// <summary>What is being dragged (read by drop targets to accept or refuse).</summary>
    public sealed record DragItem(ShellComponent Component, CanvasSlot? From)
    {
        public static DragItem Placed(CanvasSlot slot, ShellComponent component) => new(component, slot);
        public static DragItem FromPalette(ShellComponent component) => new(component, null);
    }

    /// <summary>Keyboard "pick up and move": arrows move it until Return drops it or Esc puts everything back.</summary>
    public sealed record GrabState(MosaicLayout Start, ShellComponent Component);

    public sealed record StatusMessage(string Text, bool IsError);

    public sealed record ImportOutcome(string File, string? Name, IReadOnlyList<string> Messages, string? Error);

    private readonly ICanvasLayoutHost host;
    private readonly string libraryPath;
    private readonly Func<(string Id, string Name)> workspace;
    private readonly SynchronizationContext? context;
    private readonly Dictionary<MosaicPresetID, EditHistory<MosaicLayout>> histories = [];
    private (string Key, DateTime At)? lastRecord;
    private Task writes = Task.CompletedTask;
    private EditorMode mode = EditorMode.Presets;
    private GradientTarget gradientTarget = GradientTarget.Window;

    public CanvasController(ICanvasLayoutHost host, string libraryPath, Func<(string Id, string Name)>? workspace = null)
    {
        this.host = host;
        this.libraryPath = libraryPath;
        this.workspace = workspace ?? (() => ("default", "Workspace"));
        context = SynchronizationContext.Current;
        // Small (a few KB per configuration) and only read when the editor is first used.
        Library = ConfigurationLibrary.Load(libraryPath);
        Studio = new GradientStudio(this);
        host.LayoutChanged += OnHostLayoutChanged;
    }

    /// <summary>Anything the views show changed.</summary>
    public event Action? Changed;

    /// <summary>Text for screen readers (UI Automation notifications), as macOS posts announcements.</summary>
    public event Action<string>? Announce;

    /// <summary>A short notice in the window (the model's <c>Notice</c>).</summary>
    public event Action<string>? Notice;

    /// <summary>The editor should be shown in this mode (the app opens <c>mosaic://customize</c>, or Settings › Mosaics for Presets).</summary>
    public event Action<EditorMode>? ShowRequested;

    /// <summary>For tests: "now", used to coalesce slider drags into one undo step.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public ICanvasLayoutHost Host => host;
    public MosaicLayout Layout => host.Layout;
    public MosaicPresetID ActivePreset => host.ActivePreset;
    public GradientStudio Studio { get; }
    public ConfigurationLibrary Library { get; private set; }
    public ImportOutcome? LastImport { get; private set; }
    private CanvasSlot? selection;
    private StatusMessage? message;

    /// <summary>The selected component on the canvas (setting it notifies the views).</summary>
    public CanvasSlot? Selection
    {
        get => selection;
        set { if (selection != value) { selection = value; RaiseChanged(); } }
    }

    public GrabState? Grab { get; private set; }
    public DragItem? Dragging { get; set; }

    public StatusMessage? Message
    {
        get => message;
        set { if (message != value) { message = value; RaiseChanged(); } }
    }

    public EditorMode Mode
    {
        get => mode;
        set { if (mode != value) { mode = value; RaiseChanged(); } }
    }

    public GradientTarget Target
    {
        get => gradientTarget;
        set
        {
            if (gradientTarget == value) return;
            gradientTarget = value;
            Studio.Load();
            RaiseChanged();
        }
    }

    public void RequestShow(EditorMode editorMode)
    {
        Mode = editorMode;
        ShowRequested?.Invoke(editorMode);
    }

    private void RaiseChanged() => Changed?.Invoke();

    internal void Studio_Changed() => RaiseChanged();

    private void OnHostLayoutChanged()
    {
        Studio.LayoutChanged();
        RaiseChanged();
    }

    // MARK: History

    private EditHistory<MosaicLayout> History
    {
        get
        {
            if (!histories.TryGetValue(host.ActivePreset, out var history))
                histories[host.ActivePreset] = history = new EditHistory<MosaicLayout>(100);
            return history;
        }
    }

    public bool CanUndo => History.CanUndo;
    public bool CanRedo => History.CanRedo;
    public int UndoDepth => History.UndoStack.Count;

    /// <summary>
    /// Applies <paramref name="newLayout"/> (made usable), remembering the previous one for undo.
    /// Changes with the same <paramref name="coalesce"/> key within a second share one undo step.
    /// </summary>
    public void Apply(MosaicLayout newLayout, string? coalesce = null)
    {
        var current = Layout;
        var usable = LayoutCanvasRules.Usable(newLayout);
        if (usable == current) return;
        var now = Clock();
        if (!(coalesce is not null && lastRecord is { } last && last.Key == coalesce && (now - last.At).TotalSeconds < 1))
            History.Record(current);
        lastRecord = coalesce is null ? null : (coalesce, now);
        host.ReplaceActiveLayout(usable);
        RaiseChanged();
    }

    public void Undo()
    {
        EndGrab(true);
        if (!History.TryUndo(Layout, out var previous)) return;
        lastRecord = null;
        host.ReplaceActiveLayout(previous);
        ValidateSelection();
        Say("Undone.", false);
    }

    public void Redo()
    {
        EndGrab(true);
        if (!History.TryRedo(Layout, out var next)) return;
        lastRecord = null;
        host.ReplaceActiveLayout(next);
        ValidateSelection();
        Say("Redone.", false);
    }

    /// <summary>The active preset back to its factory layout and look (undoable).</summary>
    public void ResetToPreset()
    {
        EndGrab(true);
        var preset = host.ActivePreset;
        Apply(MosaicLayout.PresetLayout(preset));
        Selection = null;
        Say($"{preset.Title()} restored to its defaults. Undo brings your changes back.", false);
    }

    // MARK: Layout edits

    private void Commit(CanvasEdit edit, bool record = true)
    {
        if (edit.Layout is { } newLayout)
        {
            if (record) Apply(newLayout);
            else host.ReplaceActiveLayout(newLayout);
            Selection = edit.Selection;
            Message = null;
            AnnounceSelection();
            RaiseChanged();
        }
        else
        {
            Say(edit.Message ?? "That can’t be done.", true);
        }
    }

    public void Move(CanvasSlot slot, ShellRegion region, int before) =>
        Commit(LayoutCanvasRules.Move(slot, region, before, Layout));

    public void Add(ShellComponent component, ShellRegion? region = null, int? before = null)
    {
        if ((region ?? LayoutCanvasRules.SuggestedRegion(component, Layout)) is not { } target)
        {
            Say($"There is no row that can take the {component.Title().ToLowerInvariant()}.", true);
            return;
        }
        Commit(LayoutCanvasRules.Add(component, target, before, Layout));
    }

    public void Remove(CanvasSlot slot)
    {
        var component = ComponentAt(slot);
        var edit = LayoutCanvasRules.Remove(slot, Layout);
        Commit(edit);
        if (edit.Layout is not null && component is { } c) Say($"{c.Title()} removed; it is in Available Items.", false);
    }

    public ShellComponent? ComponentAt(CanvasSlot slot)
    {
        var list = Layout.Components(slot.Region);
        return slot.Index >= 0 && slot.Index < list.Count ? list[slot.Index] : null;
    }

    public CanvasSlot? Locate(ShellComponent component)
    {
        if (Layout.RegionOf(component) is not { } region) return null;
        return new CanvasSlot(region, Layout.Components(region).ToList().IndexOf(component));
    }

    private CanvasSlot? Current(DragItem item) =>
        item.From is { } slot && ComponentAt(slot) == item.Component ? slot : Locate(item.Component);

    /// <summary>Drops whatever is being dragged into <paramref name="region"/> before <paramref name="before"/>.</summary>
    public void Drop(DragItem item, ShellRegion region, int before)
    {
        if (item.From is not null)
        {
            // The layout may have changed since the drag began; find the component again.
            if (Current(item) is { } slot) Move(slot, region, before);
        }
        else
        {
            Add(item.Component, region, before);
        }
        Dragging = null;
    }

    /// <summary>Dropped on the palette: back out of the layout.</summary>
    public void DropOnPalette(DragItem item)
    {
        if (item.From is not null && Current(item) is { } slot) Remove(slot);
        Dragging = null;
    }

    public CanvasVerdict Verdict(DragItem item, ShellRegion region) =>
        item.From is { } from
            ? LayoutCanvasRules.VerdictPlacing(item.Component, region, Layout, from)
            : LayoutCanvasRules.VerdictPlacing(item.Component, region, Layout);

    /// <summary>Whether dropping on the palette (removing) is allowed, with the reason when not.</summary>
    public CanvasVerdict PaletteVerdict(DragItem item) =>
        item.From is not null && Current(item) is { } slot
            ? LayoutCanvasRules.VerdictRemoving(slot, Layout)
            : CanvasVerdict.Rejected("It is already in Available Items.");

    // MARK: Keyboard

    /// <summary>←/→: previous or next component (across rows); ↑/↓: the nearest in the row above or below.</summary>
    public void MoveSelection(KeyDirection direction)
    {
        var layout = Layout;
        var slots = LayoutCanvasRules.RegionOrder
            .SelectMany(r => Enumerable.Range(0, layout.Components(r).Count).Select(i => new CanvasSlot(r, i))).ToList();
        if (slots.Count == 0) return;
        if (Selection is not { } current || slots.IndexOf(current) is var position && position < 0)
        {
            Selection = slots[0];
            AnnounceSelection();
            RaiseChanged();
            return;
        }
        switch (direction)
        {
            case KeyDirection.Left: Selection = slots[Math.Max(position - 1, 0)]; break;
            case KeyDirection.Right: Selection = slots[Math.Min(position + 1, slots.Count - 1)]; break;
            default:
                var order = LayoutCanvasRules.RegionOrder;
                var r = order.ToList().IndexOf(current.Region);
                if (r < 0) return;
                do { r += direction == KeyDirection.Up ? -1 : 1; }
                while (r >= 0 && r < order.Count && layout.Components(order[r]).Count == 0);
                if (r >= 0 && r < order.Count)
                    Selection = new CanvasSlot(order[r], Math.Min(current.Index, layout.Components(order[r]).Count - 1));
                break;
        }
        AnnounceSelection();
        RaiseChanged();
    }

    /// <summary>Moves the selected component (Alt+arrows, or arrows while picked up).</summary>
    public void MoveSelected(KeyDirection direction)
    {
        if (Selection is not { } slot)
        {
            Say("Select an item first (click it, or use the arrow keys).", true);
            return;
        }
        var edit = direction switch
        {
            KeyDirection.Left => LayoutCanvasRules.Shift(slot, -1, Layout),
            KeyDirection.Right => LayoutCanvasRules.Shift(slot, 1, Layout),
            KeyDirection.Up => LayoutCanvasRules.MoveToAdjacentRegion(slot, -1, Layout),
            _ => LayoutCanvasRules.MoveToAdjacentRegion(slot, 1, Layout),
        };
        // While picked up, the whole move is one undo step (recorded on drop).
        Commit(edit, record: Grab is null);
    }

    public void RemoveSelected()
    {
        if (Selection is not { } slot) return;
        EndGrab(true);
        Remove(slot);
    }

    /// <summary>Return/Space: pick up the selected component, or drop it.</summary>
    public void ToggleGrab()
    {
        if (Grab is not null)
        {
            EndGrab(true);
        }
        else if (Selection is { } slot && ComponentAt(slot) is { } component)
        {
            Grab = new GrabState(Layout, component);
            Say($"Moving {component.Title()}. Arrow keys move it, Enter drops it, Esc cancels.", false);
        }
    }

    /// <summary>Ends a keyboard move: keep it (one undo step) or put the layout back.</summary>
    public void EndGrab(bool commit)
    {
        if (Grab is not { } grab) return;
        Grab = null;
        if (commit)
        {
            if (grab.Start != Layout)
            {
                History.Record(grab.Start);
                lastRecord = null;
            }
            Say($"{grab.Component.Title()} dropped.", false);
        }
        else
        {
            host.ReplaceActiveLayout(grab.Start);
            Selection = Locate(grab.Component);
            Say("Move cancelled.", false);
        }
    }

    private void ValidateSelection()
    {
        if (Selection is { } slot && ComponentAt(slot) is null) Selection = null;
    }

    // MARK: Options (also undoable)

    public void SetTabPlacement(TabPlacement placement)
    {
        Apply(Layout.WithTabPlacement(placement));
        ValidateSelection();
        RaiseChanged();
    }

    /// <summary>Changes a copy of the layout and applies it (undoable).</summary>
    public void Set(Action<MosaicLayout> change, string? coalesce = null)
    {
        var copy = Layout.Clone();
        change(copy);
        Apply(copy, coalesce);
        ValidateSelection();
        RaiseChanged();
    }

    // MARK: Messages

    public void Say(string text, bool error)
    {
        Message = new StatusMessage(text, error);
        Announce?.Invoke(text);
        RaiseChanged();
    }

    private void AnnounceSelection()
    {
        if (Selection is not { } slot || ComponentAt(slot) is not { } component) return;
        Announce?.Invoke($"{component.Title()}, {slot.Region.Title()}, {slot.Index + 1} of {Layout.Components(slot.Region).Count}");
    }

    // MARK: Gradient Studio targets

    public string WorkspaceID => workspace().Id;
    public string WorkspaceName => workspace().Name;

    /// <summary>The background the target shows now.</summary>
    public BackgroundSpec Background(GradientTarget target)
    {
        var a = Layout.Appearance;
        return target switch
        {
            GradientTarget.Window => a.Background,
            GradientTarget.NewTab => a.NewTabBackground.BackgroundMode == NewTabBackground.Mode.Window
                ? a.BackgroundForWorkspace(WorkspaceID) : a.NewTabBackground.Custom,
            _ => a.BackgroundForWorkspace(WorkspaceID),
        };
    }

    /// <summary>Whether the target shows a gradient of its own (the studio then edits it live).</summary>
    public bool UsesGradient(GradientTarget target)
    {
        var a = Layout.Appearance;
        return target switch
        {
            GradientTarget.Window => a.Background.BackgroundKind == BackgroundSpec.Kind.Gradient,
            GradientTarget.NewTab => a.NewTabBackground.BackgroundMode == NewTabBackground.Mode.Custom
                && a.NewTabBackground.Custom.BackgroundKind == BackgroundSpec.Kind.Gradient,
            _ => a.WorkspaceBackgrounds.TryGetValue(WorkspaceID, out var spec) && spec.BackgroundKind == BackgroundSpec.Kind.Gradient,
        };
    }

    public void UseGradient(GradientSpec gradient, GradientTarget target, string? coalesce = null)
    {
        var copy = Layout.Clone();
        var clean = gradient.Clamped();
        var a = copy.Appearance;
        switch (target)
        {
            case GradientTarget.Window:
                a.Background.BackgroundKind = BackgroundSpec.Kind.Gradient;
                a.Background.Gradient = clean;
                break;
            case GradientTarget.NewTab:
                a.NewTabBackground.BackgroundMode = NewTabBackground.Mode.Custom;
                a.NewTabBackground.Custom.BackgroundKind = BackgroundSpec.Kind.Gradient;
                a.NewTabBackground.Custom.Gradient = clean;
                break;
            default:
                var spec = (a.WorkspaceBackgrounds.TryGetValue(WorkspaceID, out var own) ? own : a.Background).Clone();
                spec.BackgroundKind = BackgroundSpec.Kind.Gradient;
                spec.Gradient = clean;
                a.WorkspaceBackgrounds[WorkspaceID] = spec;
                break;
        }
        Apply(copy, coalesce);
    }

    /// <summary>The workspace goes back to the window's background.</summary>
    public void ClearWorkspaceBackground()
    {
        var copy = Layout.Clone();
        copy.Appearance.WorkspaceBackgrounds.Remove(WorkspaceID);
        Apply(copy);
    }

    // MARK: Configurations

    /// <summary>The saved configuration the window shows unchanged, if any.</summary>
    public LayoutConfiguration? ActiveConfiguration =>
        Library.Configurations.FirstOrDefault(c => c.Layout == Layout && host.ActivePreset == c.Layout.Preset);

    public LayoutConfiguration? SaveCurrent(string name)
    {
        if (Library.Save(name, Layout) is not { } saved)
        {
            Say($"You have {ConfigurationLibrary.MaxConfigurations} configurations; delete one first.", true);
            return null;
        }
        Apply(saved.Layout);  // the window now shows it under its name
        Persist();
        Say($"Saved “{saved.Name}”.", false);
        return saved;
    }

    /// <summary>Switches to a saved configuration: its preset becomes active with its layout.</summary>
    public void ApplyConfiguration(string id)
    {
        if (Library.Configuration(id) is not { } configuration) return;
        EndGrab(true);
        if (host.ActivePreset != configuration.Layout.Preset) host.ApplyPreset(configuration.Layout.Preset);
        Apply(configuration.Layout);
        Library.ActiveID = id;
        Selection = null;
        Persist();
        Notice?.Invoke($"Mosaic: {configuration.Name}");
        Say($"Switched to “{configuration.Name}”.", false);
    }

    public void UpdateConfiguration(string id)
    {
        if (!Library.Update(id, Layout)) return;
        if (Library.Configuration(id) is { } updated) Apply(updated.Layout);
        Persist();
        Say($"Updated “{Library.Configuration(id)?.Name}” with the current layout.", false);
    }

    public void RenameConfiguration(string id, string name)
    {
        var wasActive = ActiveConfiguration?.Id == id;
        if (!Library.Rename(id, name))
        {
            Say("A configuration needs a name.", true);
            return;
        }
        if (wasActive && Library.Configuration(id) is { } renamed) Apply(renamed.Layout);
        Persist();
        RaiseChanged();
    }

    public void DeleteConfiguration(string id)
    {
        var name = Library.Configuration(id)?.Name ?? "";
        if (!Library.Delete(id)) return;
        Persist();
        Say($"Deleted “{name}”.", false);
    }

    /// <summary>Writes a configuration (or, with null, the current layout) to a .json file.</summary>
    public async Task ExportAsync(string? id, string path)
    {
        var configuration = id is null ? null : Library.Configuration(id);
        var text = LayoutTransfer.Export(configuration?.Name ?? Layout.Name, configuration?.Layout ?? Layout);
        try
        {
            await Task.Run(() => ConfigurationLibrary.WriteFile(path, text));
            Say($"Exported to {Path.GetFileName(path)}.", false);
        }
        catch (Exception error)
        {
            Say($"Couldn’t export: {error.Message}", true);
        }
    }

    /// <summary>Reads a layout file, saves it as a configuration and switches to it; problems are reported.</summary>
    public async Task<ImportOutcome> ImportFileAsync(string path)
    {
        var file = Path.GetFileName(path);
        byte[]? data = null;
        string? readError = null;
        await Task.Run(() =>
        {
            try
            {
                var size = new FileInfo(path).Length;
                if (size > LayoutTransfer.MaxFileSize) readError = new LayoutTransfer.ImportError(LayoutTransfer.ImportErrorKind.TooLarge, size).Description;
                else data = File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                readError = "The file couldn’t be opened.";
            }
        });
        ImportOutcome outcome;
        if (data is null)
        {
            outcome = new ImportOutcome(file, null, [], readError ?? "The file couldn’t be opened.");
        }
        else
        {
            var result = LayoutTransfer.ImportLayout(data);
            if (result.Report is not { } report)
            {
                outcome = new ImportOutcome(file, null, [], result.Error!.Description);
            }
            else if (Library.Save(report.Name, report.Layout) is { } saved)
            {
                Persist();
                ApplyConfiguration(saved.Id);
                outcome = new ImportOutcome(file, saved.Name, report.Messages, null);
            }
            else
            {
                outcome = new ImportOutcome(file, null, [], $"You have {ConfigurationLibrary.MaxConfigurations} configurations; delete one first.");
            }
        }
        LastImport = outcome;
        if (outcome.Error is { } error) Say($"Couldn’t import {outcome.File}: {error}", true);
        else Say($"Imported “{outcome.Name ?? outcome.File}”" + (outcome.Messages.Count == 0 ? "." : $" with {outcome.Messages.Count} repair{(outcome.Messages.Count == 1 ? "" : "s")}."), false);
        return outcome;
    }

    public void DismissImportReport()
    {
        LastImport = null;
        RaiseChanged();
    }

    /// <summary>Every preset back to its factory layout (saved configurations are kept).</summary>
    public void RestoreAllPresetDefaults()
    {
        EndGrab(true);
        foreach (var preset in RawValues.All<MosaicPresetID>().Where(p => p != host.ActivePreset)) host.RestoreDefaults(preset);
        ResetToPreset();
        Say("All presets restored to their defaults. Your saved configurations are kept.", false);
    }

    /// <summary>Saves the library on the thread pool, in order; the snapshot is taken now.</summary>
    private void Persist()
    {
        var snapshot = Library.Copy();
        var path = libraryPath;
        writes = writes.ContinueWith(_ =>
        {
            try
            {
                ConfigurationLibrary.WriteFile(path, snapshot.ToJson());
            }
            catch (Exception error)
            {
                void Report() => Say($"Couldn’t save configurations: {error.Message}", true);
                if (context is not null) context.Post(_ => Report(), null);
                else Report();
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Waits for pending library writes (tests, shutdown).</summary>
    public Task FlushAsync() => writes;
}
