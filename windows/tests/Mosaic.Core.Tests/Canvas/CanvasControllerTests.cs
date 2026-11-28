using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Canvas;
using Mosaic.Core.Canvas.Interim;
using Mosaic.Core.Tests.Browser;
using Key = Mosaic.Core.Canvas.CanvasController.KeyDirection;
using Target = Mosaic.Core.Canvas.CanvasController.GradientTarget;

namespace Mosaic.Core.Tests.Canvas;

/// <summary>A controller over an interim host with its library in a throwaway folder.</summary>
internal sealed class CanvasHarness : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "mosaic-canvas-" + Guid.NewGuid().ToString("N"));
    public InterimLayoutHost Host { get; }
    public CanvasController Canvas { get; }
    public DateTime Now { get; set; } = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
    public List<string> Announcements { get; } = [];
    public List<string> Notices { get; } = [];
    public int Changes { get; private set; }

    public CanvasHarness(MosaicPresetID preset = MosaicPresetID.Chrome)
    {
        System.IO.Directory.CreateDirectory(Directory);
        Host = new InterimLayoutHost(preset);
        Canvas = new CanvasController(Host, LibraryPath, () => ("W-1", "Research")) { Clock = () => Now };
        Canvas.Announce += Announcements.Add;
        Canvas.Notice += Notices.Add;
        Canvas.Changed += () => Changes++;
    }

    public string LibraryPath => Path.Combine(Directory, "Canvas", "configurations.json");

    public MosaicLayout Layout => Canvas.Layout;

    public int Index(ShellRegion region, ShellComponent component) => Layout.Components(region).ToList().IndexOf(component);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
    }
}

public class CanvasControllerTests
{
    [Fact]
    public void EditsApplyLiveAndUndoRedoWalkTheHistory()
    {
        using var h = new CanvasHarness();
        var original = h.Layout;
        h.Canvas.Move(new(ShellRegion.Toolbar, 3), ShellRegion.TitleBar, 0);
        Assert.Equal(ShellComponent.Home, h.Host.Layout.Components(ShellRegion.TitleBar)[0]);
        Assert.Equal(new CanvasSlot(ShellRegion.TitleBar, 0), h.Canvas.Selection);
        Assert.True(h.Canvas.CanUndo);
        Assert.Contains("Home, Title Bar, 1 of 4", h.Announcements);
        h.Canvas.Undo();
        Assert.Equal(original, h.Layout);
        Assert.Equal("Undone.", h.Canvas.Message?.Text);
        Assert.True(h.Canvas.CanRedo);
        h.Canvas.Redo();
        Assert.Equal(ShellComponent.Home, h.Layout.Components(ShellRegion.TitleBar)[0]);
        Assert.True(h.Changes > 0);
    }

    [Fact]
    public void RefusedEditsChangeNothingAndSayWhy()
    {
        using var h = new CanvasHarness();
        var before = h.Layout;
        h.Canvas.Remove(new(ShellRegion.Toolbar, h.Index(ShellRegion.Toolbar, ShellComponent.AddressField)));
        Assert.Equal(before, h.Layout);
        Assert.True(h.Canvas.Message!.IsError);
        Assert.StartsWith("The address field is always part of the window", h.Canvas.Message.Text);
        Assert.False(h.Canvas.CanUndo);
    }

    [Fact]
    public void SliderDragsWithinASecondShareOneUndoStep()
    {
        using var h = new CanvasHarness();
        for (var i = 0; i < 5; i++)
        {
            h.Canvas.Set(l => l.SidebarWidth = 200 + i * 10, coalesce: "sidebarWidth");
            h.Now = h.Now.AddMilliseconds(200);
        }
        Assert.Equal(1, h.Canvas.UndoDepth);
        h.Now = h.Now.AddSeconds(2);
        h.Canvas.Set(l => l.SidebarWidth = 400, coalesce: "sidebarWidth");
        Assert.Equal(2, h.Canvas.UndoDepth);
        h.Canvas.Undo();
        h.Canvas.Undo();
        Assert.Equal(260, h.Layout.SidebarWidth);
    }

    [Fact]
    public void KeyboardPickUpMoveDropIsOneUndoStepAndEscapeCancels()
    {
        using var h = new CanvasHarness();
        var original = h.Layout;
        h.Canvas.MoveSelection(Key.Right);  // nothing selected: the first item
        Assert.Equal(new CanvasSlot(ShellRegion.TitleBar, 0), h.Canvas.Selection);
        h.Canvas.MoveSelection(Key.Down);
        Assert.Equal(new CanvasSlot(ShellRegion.Toolbar, 0), h.Canvas.Selection);
        h.Canvas.ToggleGrab();
        Assert.Equal(ShellComponent.Back, h.Canvas.Grab?.Component);
        h.Canvas.MoveSelected(Key.Right);
        h.Canvas.MoveSelected(Key.Right);
        Assert.Equal(ShellComponent.Back, h.Layout.Components(ShellRegion.Toolbar)[2]);
        Assert.False(h.Canvas.CanUndo, "nothing recorded while picked up");
        h.Canvas.ToggleGrab();
        Assert.Null(h.Canvas.Grab);
        Assert.Equal(1, h.Canvas.UndoDepth);
        h.Canvas.Undo();
        Assert.Equal(original, h.Layout);

        h.Canvas.Selection = new CanvasSlot(ShellRegion.Toolbar, 0);
        h.Canvas.ToggleGrab();
        h.Canvas.MoveSelected(Key.Up);
        Assert.Contains(ShellComponent.Back, h.Layout.Components(ShellRegion.TitleBar));
        h.Canvas.EndGrab(commit: false);
        Assert.Equal(original, h.Layout);
        Assert.Equal(new CanvasSlot(ShellRegion.Toolbar, 0), h.Canvas.Selection);
        Assert.Equal("Move cancelled.", h.Canvas.Message?.Text);
    }

    [Fact]
    public void DragAndDropFindsTheComponentAgainAndThePaletteRemoves()
    {
        using var h = new CanvasHarness();
        var item = CanvasController.DragItem.Placed(new(ShellRegion.Toolbar, 3), ShellComponent.Home);
        Assert.True(h.Canvas.Verdict(item, ShellRegion.Rail).IsAllowed);
        // The layout changes under the drag: the component is found again.
        h.Canvas.Move(new(ShellRegion.Toolbar, 0), ShellRegion.TitleBar, 0);
        h.Canvas.Drop(item, ShellRegion.Rail, 0);
        Assert.Equal([ShellComponent.Home], h.Layout.Components(ShellRegion.Rail));
        var field = CanvasController.DragItem.Placed(new(ShellRegion.Toolbar, h.Index(ShellRegion.Toolbar, ShellComponent.AddressField)), ShellComponent.AddressField);
        Assert.NotNull(h.Canvas.Verdict(field, ShellRegion.Rail).Message);
        Assert.NotNull(h.Canvas.PaletteVerdict(field).Message);
        h.Canvas.DropOnPalette(CanvasController.DragItem.Placed(new(ShellRegion.Rail, 0), ShellComponent.Home));
        Assert.DoesNotContain(ShellComponent.Home, h.Layout.Components(ShellRegion.Rail));
        Assert.Contains(ShellComponent.Home, LayoutCanvasRules.Palette(h.Layout));
        Assert.Equal("Home removed; it is in Available Items.", h.Canvas.Message?.Text);
        h.Canvas.Drop(CanvasController.DragItem.FromPalette(ShellComponent.Settings), ShellRegion.Toolbar, 0);
        Assert.Equal(ShellComponent.Settings, h.Layout.Components(ShellRegion.Toolbar)[0]);
    }

    [Fact]
    public void TabPlacementAndOptionsAreUndoable()
    {
        using var h = new CanvasHarness();
        h.Canvas.SetTabPlacement(TabPlacement.SidebarLeading);
        Assert.True(h.Layout.TabPlacement.IsVertical());
        Assert.Contains(ShellComponent.AddressField, h.Layout.Components(ShellRegion.SidebarMiddle));
        h.Canvas.Undo();
        Assert.Equal(TabPlacement.Top, h.Layout.TabPlacement);
        h.Canvas.ResetToPreset();
        Assert.Equal(MosaicLayout.PresetLayout(MosaicPresetID.Chrome), h.Layout);
    }

    [Fact]
    public void ConfigurationsAreSavedAppliedRenamedDeletedAndPersisted() => TestLoop.Run(async () =>
    {
        using var h = new CanvasHarness();
        h.Canvas.Add(ShellComponent.Settings);
        var saved = h.Canvas.SaveCurrent("Work")!;
        Assert.Equal("Work", h.Layout.Name);
        Assert.Equal(saved.Id, h.Canvas.ActiveConfiguration?.Id);
        h.Canvas.ResetToPreset();
        Assert.Null(h.Canvas.ActiveConfiguration);
        h.Host.ApplyPreset(MosaicPresetID.ZenArc);
        h.Canvas.ApplyConfiguration(saved.Id);
        Assert.Equal(MosaicPresetID.Chrome, h.Host.ActivePreset);
        Assert.Contains(ShellComponent.Settings, h.Layout.Components(ShellRegion.Toolbar));
        Assert.Equal(["Mosaic: Work"], h.Notices);
        h.Canvas.RenameConfiguration(saved.Id, "Focus");
        Assert.Equal("Focus", h.Layout.Name);  // the active one follows its name
        await h.Canvas.FlushAsync();
        var onDisk = ConfigurationLibrary.Load(h.LibraryPath);
        Assert.Equal(["Focus"], onDisk.Configurations.Select(c => c.Name));
        Assert.Equal(h.Layout, onDisk.Configurations[0].Layout);
        h.Canvas.DeleteConfiguration(saved.Id);
        await h.Canvas.FlushAsync();
        Assert.Empty(ConfigurationLibrary.Load(h.LibraryPath).Configurations);
        // A new controller reads the file.
        h.Canvas.SaveCurrent("Again");
        await h.Canvas.FlushAsync();
        var again = new CanvasController(new InterimLayoutHost(), h.LibraryPath);
        Assert.Equal("Again", again.Library.Configurations.Single().Name);
    });

    [Fact]
    public void ImportingAMacOSFileSavesAndAppliesIt() => TestLoop.Run(async () =>
    {
        using var h = new CanvasHarness();
        var outcome = await h.Canvas.ImportFileAsync(CanvasFixtures.Path_("macos-export-custom.json"));
        Assert.Null(outcome.Error);
        Assert.Equal("GX Aurora", outcome.Name);
        Assert.Empty(outcome.Messages);
        Assert.Equal(MosaicPresetID.Opera, h.Host.ActivePreset);
        Assert.Equal(GradientSpec.Kind.Mesh, h.Layout.Appearance.Background.Gradient.GradientKind);
        Assert.Equal("GX Aurora", h.Canvas.ActiveConfiguration?.Name);
        Assert.Equal("Imported “GX Aurora”.", h.Canvas.Message?.Text);

        var junk = Path.Combine(h.Directory, "junk.json");
        await File.WriteAllTextAsync(junk, "{\"recipe\":\"soup\"}");
        var before = h.Layout;
        var refused = await h.Canvas.ImportFileAsync(junk);
        Assert.Equal("The file doesn’t contain a Mosaic layout.", refused.Error);
        Assert.Equal(before, h.Layout);
        Assert.Single(h.Canvas.Library.Configurations);
        var missing = await h.Canvas.ImportFileAsync(Path.Combine(h.Directory, "missing.json"));
        Assert.Equal("The file couldn’t be opened.", missing.Error);
    });

    [Fact]
    public void ExportWritesTheMacOSFormat() => TestLoop.Run(async () =>
    {
        using var h = new CanvasHarness(MosaicPresetID.Opera);
        var path = Path.Combine(h.Directory, "out", "layout.json");
        await h.Canvas.ExportAsync(null, path);
        var file = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal("dev.mosaic.layout", (string)file["format"]!);
        Assert.Equal(1, (int)file["version"]!);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", (string)file["exported"]!);
        Assert.Equal(MosaicLayout.PresetLayout(MosaicPresetID.Opera), LayoutTransfer.ImportLayout(await File.ReadAllTextAsync(path)).Report!.Layout);
    });

    [Fact]
    public void RestoringAllPresetsKeepsConfigurations()
    {
        using var h = new CanvasHarness();
        h.Canvas.Add(ShellComponent.Settings);
        h.Canvas.SaveCurrent("Mine");
        h.Host.ApplyPreset(MosaicPresetID.Opera);
        h.Canvas.SetTabPlacement(TabPlacement.SidebarLeading);
        h.Host.ApplyPreset(MosaicPresetID.Chrome);
        h.Canvas.RestoreAllPresetDefaults();
        Assert.False(h.Host.IsCustomized(MosaicPresetID.Opera));
        Assert.False(h.Host.IsCustomized(MosaicPresetID.Chrome));
        Assert.Single(h.Canvas.Library.Configurations);
    }
}

public class GradientStudioTests
{
    [Fact]
    public void LiveEditsOfAGradientInUseShareOneUndoStepPerGesture()
    {
        using var h = new CanvasHarness(MosaicPresetID.ZenArc);  // its window already shows a gradient
        var studio = h.Canvas.Studio;
        studio.Load();
        Assert.True(studio.IsInUse);
        Assert.Equal(GradientSpec.Kind.Linear, studio.Gradient.GradientKind);
        for (var i = 1; i <= 5; i++)
        {
            studio.SetAngle(100 + i);
            h.Now = h.Now.AddMilliseconds(100);
        }
        Assert.Equal(105, h.Layout.Appearance.Background.Gradient.Angle);
        Assert.Equal(1, h.Canvas.UndoDepth);
        h.Canvas.Undo();
        Assert.Equal(125, h.Layout.Appearance.Background.Gradient.Angle);
        Assert.Equal(125, studio.Gradient.Angle);  // the studio follows undo
    }

    [Fact]
    public void ADragAppliesWhenItEnds()
    {
        using var h = new CanvasHarness(MosaicPresetID.ZenArc);
        var studio = h.Canvas.Studio;
        studio.Load();
        studio.ChangeKind(GradientSpec.Kind.Mesh);
        Assert.Equal(GradientSpec.Kind.Mesh, h.Layout.Appearance.Background.Gradient.GradientKind);
        var id = studio.Gradient.Sources[0].Id;
        var before = h.Layout;
        studio.BeginDrag();
        studio.MoveSource(id, 0.9, 0.9);
        studio.MoveSource(id, 0.8, 0.7);
        Assert.Equal(before, h.Layout);
        studio.EndDrag();
        var source = h.Layout.Appearance.Background.Gradient.Sources.Single(s => s.Id == id);
        Assert.Equal((0.8, 0.7), (source.X, source.Y));
        Assert.Equal(0, studio.SelectedSourceIndex);
    }

    [Fact]
    public void SourcesAndStopsAreAddedWithTheColourAlreadyThereAndRemoved()
    {
        using var h = new CanvasHarness();
        var studio = h.Canvas.Studio;
        studio.Load();  // Chrome's window has no gradient: the first preset, not in use
        Assert.False(studio.IsInUse);
        Assert.True(studio.IsMesh);
        var expected = studio.Gradient.MeshColor(0.4, 0.6, studio.Aspect).Hex;
        Assert.True(studio.AddSource(0.4, 0.6));
        Assert.Equal(expected, studio.Gradient.Sources[^1].Color);
        Assert.Equal(studio.Gradient.Sources.Count - 1, studio.SelectedSourceIndex);
        studio.SetSelectedColor(ColorValue.FromHex("#123456")!.Value);
        Assert.Equal("#123456", studio.Gradient.Sources[^1].Color);
        Assert.True(studio.Nudge(Key.Left, 0.1));
        Assert.Equal(0.3, studio.Gradient.Sources[^1].X, 9);
        studio.RemoveSelected();
        Assert.Equal(3, studio.Gradient.Sources.Count);
        Assert.Equal(MosaicLayout.PresetLayout(MosaicPresetID.Chrome), h.Layout);  // not in use: the window is untouched

        studio.ChangeKind(GradientSpec.Kind.Linear);
        Assert.True(studio.AddStop(0.5));
        Assert.Equal($"stop-{studio.Gradient.Stops.Count - 1}", studio.Selection);
        Assert.Equal(studio.Gradient.Clone().StopColor(0.5).Hex, studio.Gradient.Stops[^1].Color);
        studio.Use();
        Assert.True(studio.IsApplied);
        Assert.Equal(BackgroundSpec.Kind.Gradient, h.Layout.Appearance.Background.BackgroundKind);
    }

    [Fact]
    public void PresetsKeepTheChosenFinish()
    {
        using var h = new CanvasHarness();
        var studio = h.Canvas.Studio;
        studio.Load();
        studio.SetNoise(0.4);
        studio.SetRaw(true);
        Assert.True(studio.ApplyPreset("Ocean"));
        Assert.True(studio.Gradient.SameDesign(GradientPresets.Named("Ocean")!));
        Assert.Equal(0.4, studio.Gradient.Noise);
        Assert.True(studio.Gradient.Raw);
        Assert.False(studio.ApplyPreset("Nope"));
    }

    [Fact]
    public void NewTabAndWorkspaceTargetsGetTheirOwnBackgrounds()
    {
        using var h = new CanvasHarness();
        h.Canvas.Target = Target.NewTab;
        h.Canvas.Studio.Use();
        Assert.Equal(NewTabBackground.Mode.Custom, h.Layout.Appearance.NewTabBackground.BackgroundMode);
        Assert.True(h.Canvas.UsesGradient(Target.NewTab));
        Assert.False(h.Canvas.UsesGradient(Target.Window));

        h.Canvas.Target = Target.Workspace;
        h.Canvas.Studio.ApplyPreset("Forest");
        Assert.False(h.Canvas.UsesGradient(Target.Workspace));
        h.Canvas.Studio.Use();
        Assert.True(h.Canvas.UsesGradient(Target.Workspace));
        Assert.True(h.Layout.Appearance.WorkspaceBackgrounds["W-1"].Gradient.SameDesign(GradientPresets.Named("Forest")!));
        Assert.Equal(BackgroundSpec.Kind.System, h.Layout.Appearance.BackgroundForWorkspace("W-2").BackgroundKind);
        h.Canvas.ClearWorkspaceBackground();
        Assert.Empty(h.Layout.Appearance.WorkspaceBackgrounds);
    }
}

public class CanvasAutomationTests
{
    /// <summary>
    /// Sends commands through the canvas's registered handler exactly as the pipe's dispatcher
    /// does (Automation → AutomationRegistry handlers). The global registry itself is left
    /// alone so other tests' exact <c>dump</c> keys are unaffected.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public BrowserFixture Fixture { get; } = new();
        public CanvasHarness Canvas { get; } = new();

        public Harness() => Fixture.Model.UseCanvas(Canvas.Canvas);

        public async Task<JsonObject> SendAsync(string command, string? value = null, string? output = null)
        {
            var reply = await CanvasAutomation.HandleAsync(Fixture.Model, command, value ?? "", output);
            Assert.NotNull(reply);
            Assert.DoesNotContain('\n', reply!.Json);
            return JsonNode.Parse(reply.Json)!.AsObject();
        }

        public async Task OkAsync(string command, string? value = null, string? output = null)
        {
            var reply = await SendAsync(command, value, output);
            Assert.True((bool)reply["ok"]!, $"{command} {value}: {reply.ToJsonString()}");
        }

        public Task<JsonObject> StateAsync() =>
            Task.FromResult(JsonNode.Parse(CanvasAutomation.Dump(Canvas.Canvas).ToJsonString())!.AsObject());

        public void Dispose()
        {
            Canvas.Dispose();
            Fixture.Dispose();
        }
    }

    [Fact]
    public void LayoutCommandsDriveTheSameEditsAsTheEditor() => TestLoop.Run(async () =>
    {
        using var h = new Harness();
        await h.OkAsync("canvasMove", "home>titleBar:0");
        var state = await h.StateAsync();
        Assert.Equal("home", (string)state["layout"]!["titleBar"]![0]!);
        Assert.True((bool)state["canUndo"]!);
        Assert.Equal("titleBar", (string)state["selection"]!["region"]!);

        await h.OkAsync("canvasDrag", "addressField>rail");
        state = await h.StateAsync();
        Assert.True((bool)state["messageIsError"]!);
        Assert.Contains("too narrow", (string)state["message"]!);
        Assert.Contains("addressField", state["layout"]!["toolbar"]!.AsArray().Select(n => (string)n!));

        await h.OkAsync("canvasDrag", "back>rail:0");
        Assert.Equal("back", (string)(await h.StateAsync())["layout"]!["rail"]![0]!);
        await h.OkAsync("canvasDragToPalette", "rail:0");
        Assert.Contains("back", (await h.StateAsync())["palette"]!.AsArray().Select(n => (string)n!));
        await h.OkAsync("canvasDragToPalette", "addressField");
        Assert.True((bool)(await h.StateAsync())["messageIsError"]!);

        await h.OkAsync("canvasAdd", "settings>toolbar:0");
        await h.OkAsync("canvasSelect", "toolbar:0");
        await h.OkAsync("canvasKey", "alt-right");
        state = await h.StateAsync();
        Assert.Equal("settings", (string)state["layout"]!["toolbar"]![1]!);
        Assert.Equal(1, (int)state["selection"]!["index"]!);
        await h.OkAsync("canvasKey", "return");
        Assert.Equal("settings", (string)(await h.StateAsync())["grabbing"]!);
        await h.OkAsync("canvasKey", "right");
        await h.OkAsync("canvasKey", "escape");
        Assert.Equal("settings", (string)(await h.StateAsync())["layout"]!["toolbar"]![1]!);
        await h.OkAsync("canvasKey", "delete");
        Assert.DoesNotContain("settings", (await h.StateAsync())["layout"]!["toolbar"]!.AsArray().Select(n => (string)n!));
        await h.OkAsync("canvasUndo");
        await h.OkAsync("canvasRedo");
        await h.OkAsync("canvasTabs", "sidebarTrailing");
        Assert.Equal("sidebarTrailing", (string)(await h.StateAsync())["tabPlacement"]!);
        await h.OkAsync("canvasReset");
        Assert.Equal("top", (string)(await h.StateAsync())["tabPlacement"]!);

        var reply = await h.SendAsync("canvasMove", "teleporter>toolbar");
        Assert.False((bool)reply["ok"]!);
        Assert.Contains("teleporter", (string)reply["error"]!);
    });

    [Fact]
    public void GradientAndConfigurationCommands() => TestLoop.Run(async () =>
    {
        using var h = new Harness();
        var shown = new List<CanvasController.EditorMode>();
        h.Canvas.Canvas.ShowRequested += shown.Add;
        await h.OkAsync("canvasMode", "gradient");
        Assert.Equal([CanvasController.EditorMode.Gradient], shown);
        await h.OkAsync("gradientApply", "Aurora");
        var state = await h.StateAsync();
        Assert.True((bool)state["usesGradient"]!["window"]!);
        Assert.Equal("mesh", (string)state["studio"]!["kind"]!);
        await h.OkAsync("gradientSet", "noise=0.4");
        await h.OkAsync("gradientSet", "addSource=0.5,0.5,#FF0000");
        await h.OkAsync("gradientSelect", "4");
        state = await h.StateAsync();
        Assert.Equal(4, (int)state["studio"]!["selection"]!);
        Assert.Equal(0.4, (double)state["background"]!["gradient"]!["noise"]!);
        await h.OkAsync("gradientTarget", "newTab");
        await h.OkAsync("gradientSet", "kind=radial");
        Assert.Equal("custom", (string)(await h.StateAsync())["newTabBackground"]!);
        await h.OkAsync("newTabBackground", "window");
        Assert.False((bool)(await h.StateAsync())["usesGradient"]!["newTab"]!);
        var gradientFile = Path.Combine(h.Canvas.Directory, "g.json");
        await File.WriteAllTextAsync(gradientFile, """{"kind":"angular","stops":[{"color":"#000000","location":0},{"color":"#FFFFFF","location":1}]}""");
        await h.OkAsync("gradientTarget", "window");
        await h.OkAsync("gradientFile", gradientFile);
        Assert.Equal("angular", (string)(await h.StateAsync())["background"]!["gradient"]!["kind"]!);
        Assert.False((bool)(await h.SendAsync("gradientApply", "Nope"))["ok"]!);

        await h.OkAsync("configSave", "Studio");
        Assert.Equal("Studio", (string)(await h.StateAsync())["activeConfiguration"]!);
        var exported = Path.Combine(h.Canvas.Directory, "studio.json");
        await h.OkAsync("configExport", "Studio", exported);
        Assert.Equal("Studio", LayoutTransfer.ImportLayout(await File.ReadAllTextAsync(exported)).Report!.Name);
        await h.OkAsync("configRename", "Studio=Studio Two");
        await h.OkAsync("canvasReset");
        await h.OkAsync("configApply", "studio two");
        Assert.Equal("Studio Two", (string)(await h.StateAsync())["activeConfiguration"]!);
        await h.OkAsync("configImport", CanvasFixtures.Path_("macos-export-zenArc.json"));
        state = await h.StateAsync();
        Assert.Equal("Zen / Arc", (string)state["lastImport"]!["name"]!);
        Assert.Equal("zenArc", (string)state["activePreset"]!);
        Assert.Equal(2, state["configurations"]!.AsArray().Count);
        await h.OkAsync("configDelete", "Studio Two");
        await h.OkAsync("configRestoreAll");
        Assert.Single((await h.StateAsync())["configurations"]!.AsArray());
        Assert.False((bool)(await h.SendAsync("configApply", "nothing"))["ok"]!);
        await h.Canvas.Canvas.FlushAsync();
    });

    [Fact]
    public void UnrelatedCommandsAreLeftToOthers() => TestLoop.Run(async () =>
    {
        using var h = new Harness();
        Assert.Null(await CanvasAutomation.HandleAsync(h.Fixture.Model, "navigate", "x", null));
    });
}
