using System.Globalization;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Canvas.Interim;

// Automation commands and `dump` fields for the canvas, with the macOS names and syntax
// (Sources/Mosaic/Features/Canvas/CanvasAutomation.swift). Every command goes through the same
// CanvasController methods as the UI, so a script exercises exactly what a drag, a key or a
// click does (real mouse drags can't run on CI; `canvasDrag` takes the drag-and-drop path:
// verdict, refusal message, drop position). Owner: canvas.
//
//     canvasMode presets|layout|gradient|configurations   open the editor in that mode
//     canvasMove <from>><region>[:<index>]                 from = component or region:index
//     canvasDrag <from>><region>[:<index>]                 the same through the drag-and-drop path
//     canvasDragToPalette <from>                           drag a placed item onto Available Items
//     canvasAdd <component>[><region>[:<index>]]           from the palette (no region: suggested row)
//     canvasRemove <component | region:index>
//     canvasSelect <component | region:index | none>
//     canvasKey left|right|up|down|alt-left|alt-right|alt-up|alt-down|return|space|escape|delete|undo|redo
//     canvasUndo / canvasRedo / canvasReset
//     canvasTabs top|sidebarLeading|sidebarTrailing
//     gradientTarget window|newTab|workspace
//     gradientApply <preset name>
//     gradientSet <key>=<value>     kind, angle, sharpness, noise, opacity, raw=on|off, centerX, centerY,
//                                   addSource=x,y,#hex, stops=#hex@0,#hex@1
//     gradientSelect <n>            select colour n (0-based) in the studio
//     gradientFile <path.json>      a GradientSpec as JSON
//     newTabBackground plain|window|custom
//     configSave <name> / configApply <name|id> / configUpdate <name|id> / configDelete <name|id>
//     configRename <old>=<new>
//     configExport <name|id|current>  (out: the file to write)
//     configImport <path>
//     configRestoreAll

namespace Mosaic.Core.Canvas;

public static class CanvasAutomation
{
    private static int registered;

    /// <summary>Adds the canvas's commands and its <c>canvas</c> dump key (once; cheap: stores delegates only).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1) return;
        AutomationRegistry.Register(HandleAsync);
        // Until the canvas is first used (any canvas command, or opening the editor) it doesn't
        // exist, and `dump` doesn't create it: the key then says so.
        AutomationRegistry.RegisterState("canvas", model => model.HasCanvas ? Dump(model.Canvas) : new JsonObject { ["created"] = false });
    }

    public static async Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output)
    {
        if (!IsCanvasCommand(command)) return null;
        var error = await RunAsync(model.Canvas, command, value, output);
        return error is null ? AutomationRegistry.Ok() : AutomationRegistry.Error(error);
    }

    private static readonly HashSet<string> commands =
    [
        "canvasMode", "canvasMove", "canvasDrag", "canvasDragToPalette", "canvasAdd", "canvasRemove", "canvasSelect", "canvasKey",
        "canvasUndo", "canvasRedo", "canvasReset", "canvasTabs", "gradientTarget", "gradientApply", "gradientSet",
        "gradientSelect", "gradientFile", "newTabBackground", "configSave", "configApply", "configUpdate", "configDelete",
        "configRename", "configExport", "configImport", "configRestoreAll",
    ];

    public static bool IsCanvasCommand(string command) => commands.Contains(command);

    private static string Unreadable(CanvasController canvas, string value)
    {
        var text = $"Automation: can’t read “{value}”.";
        canvas.Say(text, true);
        return text;
    }

    /// <summary>Runs one command; returns an error for input it can't read (refused edits are reported in <c>dump</c>, as on macOS).</summary>
    public static async Task<string?> RunAsync(CanvasController canvas, string command, string value, string? output)
    {
        switch (command)
        {
            case "canvasMode":
                if (Parse<CanvasController.EditorMode>(value) is not { } mode) return Unreadable(canvas, value);
                canvas.RequestShow(mode);
                break;
            case "canvasMove":
            case "canvasDrag":
            {
                if (ParseMove(value, canvas) is not var (from, region, index)) return Unreadable(canvas, value);
                var target = index ?? canvas.Layout.Components(region).Count;
                if (command == "canvasDrag" && canvas.ComponentAt(from) is { } component)
                {
                    var item = CanvasController.DragItem.Placed(from, component);
                    canvas.Dragging = item;
                    if (canvas.Verdict(item, region).Message is { } message)
                    {
                        canvas.Say(message, true);
                        canvas.Dragging = null;
                    }
                    else
                    {
                        canvas.Drop(item, region, target);
                    }
                }
                else
                {
                    canvas.Move(from, region, target);
                }
                break;
            }
            case "canvasDragToPalette":
            {
                if (ParseSlot(value, canvas) is not { } from || canvas.ComponentAt(from) is not { } component) return Unreadable(canvas, value);
                var item = CanvasController.DragItem.Placed(from, component);
                if (canvas.PaletteVerdict(item).Message is { } message) canvas.Say(message, true);
                else canvas.DropOnPalette(item);
                canvas.Dragging = null;
                break;
            }
            case "canvasAdd":
            {
                var parts = value.Split('>', 2);
                if (JsonEnum.Parse<ShellComponent>(parts[0]) is not { } component) return Unreadable(canvas, value);
                if (parts.Length > 1)
                {
                    if (ParseRegion(parts[1]) is not var (region, index)) return Unreadable(canvas, value);
                    canvas.Add(component, region, index);
                }
                else
                {
                    canvas.Add(component);
                }
                break;
            }
            case "canvasRemove":
                if (ParseSlot(value, canvas) is not { } slot) return Unreadable(canvas, value);
                canvas.Remove(slot);
                break;
            case "canvasSelect":
                if (value == "none") canvas.Selection = null;
                else if (ParseSlot(value, canvas) is { } selected) canvas.Selection = selected;
                else return Unreadable(canvas, value);
                canvas.Message = null;
                break;
            case "canvasKey":
                if (!Key(value, canvas)) return Unreadable(canvas, value);
                break;
            case "canvasUndo": canvas.Undo(); break;
            case "canvasRedo": canvas.Redo(); break;
            case "canvasReset": canvas.ResetToPreset(); break;
            case "canvasTabs":
                if (JsonEnum.Parse<TabPlacement>(value) is not { } placement) return Unreadable(canvas, value);
                canvas.SetTabPlacement(placement);
                break;
            case "gradientTarget":
                if (JsonEnum.Parse<CanvasController.GradientTarget>(value) is not { } gradientTarget) return Unreadable(canvas, value);
                canvas.Target = gradientTarget;
                break;
            case "gradientApply":
            {
                if (GradientPresets.Named(value) is not { } preset)
                {
                    var text = $"Automation: no gradient preset “{value}”.";
                    canvas.Say(text, true);
                    return text;
                }
                var current = canvas.Background(canvas.Target).Gradient;
                preset.Noise = current.Noise;
                preset.Opacity = current.Opacity;
                preset.Raw = current.Raw;
                canvas.UseGradient(preset, canvas.Target);
                canvas.Studio.Load();
                break;
            }
            case "gradientSet":
                if (!SetGradient(value, canvas)) return Unreadable(canvas, value);
                break;
            case "gradientSelect":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || !canvas.Studio.SelectIndex(n))
                    return Unreadable(canvas, value);
                break;
            case "gradientFile":
            {
                string? text = null;
                try { text = await File.ReadAllTextAsync(value); } catch (Exception) { }
                if (text is null || ReadGradient(text) is not { } spec)
                {
                    var message = $"Automation: couldn’t read a gradient from {value}.";
                    canvas.Say(message, true);
                    return message;
                }
                canvas.UseGradient(spec, canvas.Target);
                canvas.Studio.Load();
                break;
            }
            case "newTabBackground":
                if (JsonEnum.Parse<NewTabBackgroundMode>(value) is not { } newTabMode) return Unreadable(canvas, value);
                canvas.Set(l => l.Appearance.NewTabBackground.Mode = newTabMode);
                break;
            case "configSave":
                canvas.SaveCurrent(value);
                break;
            case "configApply":
            case "configUpdate":
            case "configDelete":
            {
                if (canvas.Library.Find(value)?.Id is not { } id) return $"No configuration “{value}”.";
                if (command == "configApply") canvas.ApplyConfiguration(id);
                else if (command == "configUpdate") canvas.UpdateConfiguration(id);
                else canvas.DeleteConfiguration(id);
                break;
            }
            case "configRename":
            {
                var parts = value.Split('=', 2);
                if (parts.Length != 2) return Unreadable(canvas, value);
                if (canvas.Library.Find(parts[0])?.Id is not { } id) return $"No configuration “{parts[0]}”.";
                canvas.RenameConfiguration(id, parts[1]);
                break;
            }
            case "configExport":
            {
                if (output is null) return "configExport needs \"out\": the file to write.";
                string? id = null;
                if (value != "current" && value.Length > 0)
                {
                    if (canvas.Library.Find(value)?.Id is not { } found) return $"No configuration “{value}”.";
                    id = found;
                }
                await canvas.ExportAsync(id, output);
                break;
            }
            case "configImport":
                await canvas.ImportFileAsync(value);
                break;
            case "configRestoreAll":
                canvas.RestoreAllPresetDefaults();
                break;
        }
        return null;
    }

    private static T? Parse<T>(string value) where T : struct, Enum => JsonEnum.Parse<T>(value);

    /// <summary>A gradient file (a GradientSpec as JSON), or null when it isn't one.</summary>
    internal static GradientSpec? ReadGradient(string json)
    {
        try { return GradientSpec.FromJson(JsonNode.Parse(json)); }
        catch (Exception) { return null; }
    }

    // MARK: Parsing

    /// <summary>"toolbar" or "toolbar:3".</summary>
    private static (ShellRegion Region, int? Index)? ParseRegion(string text)
    {
        var parts = text.Split(':');
        if (JsonEnum.Parse<ShellRegion>(parts[0]) is not { } region) return null;
        if (parts.Length > 1)
        {
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) return null;
            return (region, index);
        }
        return (region, null);
    }

    /// <summary>"region:index", or a component's name (its first occurrence).</summary>
    private static CanvasSlot? ParseSlot(string text, CanvasController canvas)
    {
        if (ParseRegion(text) is var (region, index) && index is { } i) return new CanvasSlot(region, i);
        return JsonEnum.Parse<ShellComponent>(text) is { } component ? canvas.Locate(component) : null;
    }

    private static (CanvasSlot From, ShellRegion Region, int? Index)? ParseMove(string text, CanvasController canvas)
    {
        var parts = text.Split('>', 2);
        if (parts.Length != 2 || ParseSlot(parts[0], canvas) is not { } from || ParseRegion(parts[1]) is not var (region, index)) return null;
        return (from, region, index);
    }

    /// <summary>The same calls the canvas's key handler makes.</summary>
    private static bool Key(string name, CanvasController canvas)
    {
        void Arrow(CanvasController.KeyDirection d)
        {
            if (canvas.Grab is not null) canvas.MoveSelected(d);
            else canvas.MoveSelection(d);
        }
        switch (name)
        {
            case "left": Arrow(CanvasController.KeyDirection.Left); break;
            case "right": Arrow(CanvasController.KeyDirection.Right); break;
            case "up": Arrow(CanvasController.KeyDirection.Up); break;
            case "down": Arrow(CanvasController.KeyDirection.Down); break;
            case "alt-left": canvas.MoveSelected(CanvasController.KeyDirection.Left); break;
            case "alt-right": canvas.MoveSelected(CanvasController.KeyDirection.Right); break;
            case "alt-up": canvas.MoveSelected(CanvasController.KeyDirection.Up); break;
            case "alt-down": canvas.MoveSelected(CanvasController.KeyDirection.Down); break;
            case "return" or "space": canvas.ToggleGrab(); break;
            case "escape": canvas.EndGrab(false); break;
            case "delete": canvas.RemoveSelected(); break;
            case "undo": canvas.Undo(); break;
            case "redo": canvas.Redo(); break;
            default: return false;
        }
        return true;
    }

    private static bool SetGradient(string assignment, CanvasController canvas)
    {
        var eq = assignment.IndexOf('=');
        if (eq < 0) return false;
        var (key, value) = (assignment[..eq], assignment[(eq + 1)..]);
        var target = canvas.Target;
        var g = canvas.Background(target).Gradient.Clone();
        double? number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        switch (key)
        {
            case "kind":
                if (JsonEnum.Parse<GradientKind>(value) is not { } kind) return false;
                g = g.Converted(kind);
                break;
            case "angle": g.Angle = number ?? g.Angle; break;
            case "sharpness": g.Sharpness = number ?? g.Sharpness; break;
            case "noise": g.Noise = number ?? g.Noise; break;
            case "opacity": g.Opacity = number ?? g.Opacity; break;
            case "centerX": g.CenterX = number ?? g.CenterX; break;
            case "centerY": g.CenterY = number ?? g.CenterY; break;
            case "raw": g.Raw = value is "on" or "true"; break;
            case "addSource":
            {
                var parts = value.Split(',');
                if (parts.Length != 3 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
                if (g.Sources.Count == 0) g.Sources = [.. g.MeshSources];
                g.Sources.Add(Gradients.NewSource(x, y, parts[2]));
                break;
            }
            case "stops":
                g.Stops = [.. value.Split(',').Select(item => item.Split('@')).Where(p => p.Length == 2)
                    .Select(p => (p[0], ok: double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var l), l))
                    .Where(p => p.ok).Select(p => new GradientStop(p.Item1, p.l))];
                break;
            default:
                return false;
        }
        canvas.UseGradient(g, target);
        canvas.Studio.Load();
        return true;
    }

    // MARK: Dump

    public static JsonObject Dump(CanvasController canvas)
    {
        var layout = canvas.Layout;
        var studio = canvas.Studio;
        var usesGradient = new JsonObject();
        foreach (var target in Enum.GetValues<CanvasController.GradientTarget>()) usesGradient[target.Raw()] = canvas.UsesGradient(target);
        var fields = new JsonObject
        {
            ["created"] = true,
            ["mode"] = canvas.Mode.Raw(),
            ["activePreset"] = canvas.ActivePreset.Raw(),
            ["canUndo"] = canvas.CanUndo,
            ["canRedo"] = canvas.CanRedo,
            ["undoDepth"] = canvas.UndoDepth,
            ["grabbing"] = canvas.Grab?.Component.Raw() ?? "",
            ["message"] = canvas.Message?.Text ?? "",
            ["messageIsError"] = canvas.Message?.IsError ?? false,
            ["palette"] = new JsonArray([.. LayoutCanvasRules.Palette(layout).Select(c => (JsonNode)c.Raw())]),
            ["hasSettingsAccess"] = LayoutCanvasRules.HasSettingsAccess(layout),
            ["layout"] = layout.ToJsonObject()["regions"]!.DeepClone(),
            ["tabPlacement"] = layout.TabPlacement.Raw(),
            ["gradientTarget"] = canvas.Target.Raw(),
            ["usesGradient"] = usesGradient,
            ["workspaceID"] = canvas.WorkspaceID,
            ["meshImagesRendered"] = GradientRaster.RenderCount,
            ["background"] = canvas.Background(canvas.Target).ToJson(),
            ["newTabBackground"] = layout.Appearance.NewTabBackground.Mode.Raw(),
            ["studio"] = new JsonObject
            {
                ["kind"] = studio.Gradient.Kind.Raw(),
                ["selection"] = studio.SelectedSourceIndex ?? studio.SelectedStopIndex ?? -1,
                ["colors"] = new JsonArray([.. studio.Gradient.Colors.Select(c => (JsonNode)c)]),
                ["inUse"] = studio.IsInUse,
                ["applied"] = studio.IsApplied,
            },
            ["configurations"] = new JsonArray([.. canvas.Library.Configurations.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["preset"] = c.Layout.Preset.Raw(),
            })]),
            ["activeConfiguration"] = canvas.ActiveConfiguration?.Name ?? "",
        };
        if (canvas.Selection is { } slot)
        {
            fields["selection"] = new JsonObject
            {
                ["region"] = slot.Region.Raw(),
                ["index"] = slot.Index,
                ["component"] = canvas.ComponentAt(slot)?.Raw() ?? "",
            };
        }
        if (canvas.LastImport is { } outcome)
        {
            fields["lastImport"] = new JsonObject
            {
                ["file"] = outcome.File,
                ["name"] = outcome.Name ?? "",
                ["messages"] = new JsonArray([.. outcome.Messages.Select(m => (JsonNode)m)]),
                ["error"] = outcome.Error ?? "",
            };
        }
        return fields;
    }
}
