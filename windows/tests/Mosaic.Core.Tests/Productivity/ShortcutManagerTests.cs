using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Mosaic.Core.Tests.Browser;
using static Mosaic.Core.Tests.Browser.Snapshots;
using static Mosaic.Core.Tests.Productivity.ProductivityTestSupport;

namespace Mosaic.Core.Tests.Productivity;

public class ShortcutManagerTests
{
    [Fact]
    public void WindowsDefaultsRunTheirCommands() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        var shortcuts = model.Productivity.Shortcuts;
        await model.Productivity.StartAsync();

        Assert.True(shortcuts.Perform(Key("ctrl+t")));
        Assert.Equal(2, model.Tabs.Count);
        Assert.True(shortcuts.Perform(Key("ctrl+1")));
        Assert.Same(model.Tabs[0], model.SelectedTab);
        Assert.True(shortcuts.Perform(Key("ctrl+9")));
        Assert.Same(model.Tabs[^1], model.SelectedTab);
        Assert.True(shortcuts.Perform(Key("ctrl+pageup")));
        Assert.Same(model.Tabs[0], model.SelectedTab);
        Assert.True(shortcuts.Perform(Key("ctrl+k")));
        Assert.True(model.Productivity.Palette.IsPresented);
        var focusRequests = fixture.AddressFocusRequests;
        Assert.True(shortcuts.Perform(Key("alt+d")));
        Assert.Equal(focusRequests + 1, fixture.AddressFocusRequests);
        // Nothing to go back to: like a disabled menu item.
        Assert.False(shortcuts.Perform(Key("alt+left")));
        Assert.False(shortcuts.Perform(Key("ctrl+alt+shift+z")));
        Assert.True(shortcuts.Perform(Key("ctrl+w")));
        Assert.Single(model.Tabs);
    });

    [Fact]
    public void ActiveBindingsListEveryComboOnceAndNoneWhileRecording() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var shortcuts = fixture.Model.Productivity.Shortcuts;
        await fixture.Model.Productivity.StartAsync();
        var active = shortcuts.ActiveBindings;
        Assert.Equal(active.Count, active.Select(b => b.Combo).Distinct().Count());
        Assert.Contains((Key("f5"), ShortcutCommands.Reload), active);
        Assert.Contains((Key("ctrl+r"), ShortcutCommands.Reload), active);
        Assert.Contains((Key("f9"), ShortcutCommands.ReaderView), active);
        Assert.Equal(ShortcutCommands.CommandPalette, shortcuts.Match(Key("ctrl+k")));

        var changes = 0;
        shortcuts.Changed += () => changes++;
        shortcuts.IsSuspended = true;
        Assert.Empty(shortcuts.ActiveBindings);
        Assert.False(shortcuts.Perform(Key("ctrl+t")));
        shortcuts.IsSuspended = false;
        Assert.Equal(2, changes);
    });

    [Fact]
    public void ChangesApplyLiveAndPersist() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var shortcuts = fixture.Model.Productivity.Shortcuts;
        await fixture.Model.Productivity.StartAsync();
        var palette = shortcuts.Command(ShortcutCommands.CommandPalette)!;
        var newTab = shortcuts.Command(ShortcutCommands.NewTab)!;

        Assert.Equal(new ShortcutProblem.Conflict([ShortcutCommands.NewTab]), shortcuts.Problem(Key("ctrl+t"), palette));
        shortcuts.Assign(Key("ctrl+t"), palette);
        Assert.Equal(ShortcutCommands.CommandPalette, shortcuts.Match(Key("ctrl+t")));
        Assert.Null(shortcuts.Combo(newTab));
        Assert.Null(shortcuts.Match(Key("ctrl+k")));

        await fixture.Model.Productivity.Files.Flush();
        using var second = new BrowserFixture();
        second.Model.UseProductivityOptions(new ProductivityOptions { Directory = fixture.PathFor("productivity"), ReaderResources = ReaderResources });
        await second.Model.Productivity.StartAsync();
        Assert.Equal(ShortcutCommands.CommandPalette, second.Model.Productivity.Shortcuts.Match(Key("ctrl+t")));

        shortcuts.ResetAll();
        await fixture.Model.Productivity.Files.Flush();
        Assert.False(File.Exists(fixture.Model.Productivity.Files.PathOf(ProductivityFiles.ShortcutsFile)));
        Assert.Equal(ShortcutCommands.NewTab, shortcuts.Match(Key("ctrl+t")));
    });

    [Fact]
    public void CorruptFileFallsBackToDefaults() => TestLoop.Run(async () =>
    {
        using var fixture = Create(f =>
        {
            Directory.CreateDirectory(f.PathFor("productivity"));
            File.WriteAllText(Path.Combine(f.PathFor("productivity"), ProductivityFiles.ShortcutsFile), "not json");
        });
        await fixture.Model.Productivity.StartAsync();
        Assert.Equal(ShortcutCommands.NewTab, fixture.Model.Productivity.Shortcuts.Match(Key("ctrl+t")));
    });

    [Fact]
    public void ForwardingTableLeavesTheAgentsOwnChordsAlone() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var shortcuts = fixture.Model.Productivity.Shortcuts;
        await fixture.Model.Productivity.StartAsync();
        var table = shortcuts.GeckoForwardingPairs().ToDictionary(p => p.Chord, p => p.EventType);

        Assert.Equal("productivity.shortcut:" + ShortcutCommands.CommandPalette, table["accel+k"]);
        Assert.Equal("productivity.shortcut:" + ShortcutCommands.Reload, table["F5"]);
        Assert.Equal("productivity.shortcut:" + ShortcutCommands.Back, table["alt+ArrowLeft"]);
        Assert.Equal("productivity.shortcut:" + ShortcutCommands.NextTab, table["accel+Tab"]);
        Assert.Equal("productivity.shortcut:" + ShortcutCommands.ReaderView, table["F9"]);
        // The agent forwards these itself and the Windows model acts on them.
        foreach (var chord in new[] { "accel+t", "accel+n", "accel+w", "accel+l", "accel+shift+e" }) Assert.False(table.ContainsKey(chord), chord);
        Assert.Equal(table.Keys.Order(StringComparer.Ordinal), shortcuts.GeckoForwardingPairs().Select(p => p.Chord));
    });

    [Fact]
    public void MovingABuiltInChordForwardsTheNewOneAndSwallowsTheOld() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var shortcuts = fixture.Model.Productivity.Shortcuts;
        await fixture.Model.Productivity.StartAsync();
        shortcuts.Assign(Key("ctrl+j"), shortcuts.Command(ShortcutCommands.NewTab)!);
        var table = shortcuts.GeckoForwardingPairs().ToDictionary(p => p.Chord, p => p.EventType);
        Assert.Equal("productivity.shortcut:" + ShortcutCommands.NewTab, table["accel+j"]);
        Assert.Equal("productivity.none", table["accel+t"]);
        // Ctrl+N stays the agent's New Tab.
        Assert.False(table.ContainsKey("accel+n"));

        // Giving the old chord to another command forwards it to that command.
        shortcuts.Assign(Key("ctrl+t"), shortcuts.Command(ShortcutCommands.CommandPalette)!);
        table = shortcuts.GeckoForwardingPairs().ToDictionary(p => p.Chord, p => p.EventType);
        Assert.Equal("productivity.shortcut:" + ShortcutCommands.CommandPalette, table["accel+t"]);
    });

    [Fact]
    public void TheTableIsSentWhenGeckoStartsAndWhenShortcutsChange() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var shortcuts = fixture.Model.Productivity.Shortcuts;
        await fixture.Model.Productivity.StartAsync();
        Assert.Empty(fixture.Gecko.CallsTo("setForwardedShortcuts"));

        fixture.OpenGeckoTab("https://example.com/");
        var sent = Assert.Single(fixture.Gecko.CallsTo("setForwardedShortcuts"));
        Assert.Contains(Chords(sent[0]), chord => chord == "accel+k");

        shortcuts.Assign(Key("ctrl+j"), shortcuts.Command(ShortcutCommands.CommandPalette)!);
        Assert.Equal(2, fixture.Gecko.CallsTo("setForwardedShortcuts").Count);
        Assert.Contains(Chords(fixture.Gecko.CallsTo("setForwardedShortcuts")[1][0]), chord => chord == "accel+j");

        // Gecko restarts with an empty table: sent again even though nothing changed.
        fixture.Gecko.Crash();
        fixture.Model.Reload();
        await TestLoop.Settle();
        Assert.Equal(3, fixture.Gecko.CallsTo("setForwardedShortcuts").Count);
    });

    [Fact]
    public void ShortcutsPressedInAGeckoPageRunTheirCommands() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        var tab = fixture.OpenGeckoTab("https://example.com/");
        var state = Tab(tab.GeckoId!.Value, "https://example.com/", selected: true);

        fixture.Gecko.ReportEvents([state], "productivity.shortcut:" + ShortcutCommands.CommandPalette);
        Assert.True(model.Productivity.Palette.IsPresented);
        fixture.Gecko.ReportEvents([state], "productivity.none", "productivity.shortcut:Nope/Unknown");
        Assert.True(model.Productivity.Palette.IsPresented);
        fixture.Gecko.ReportEvents([state], "productivity.shortcut:" + ShortcutCommands.NewTab);
        Assert.Equal(3, model.Tabs.Count);
    });

    private static IEnumerable<string> Chords(object? table) =>
        ((System.Text.Json.Nodes.JsonArray)table!).Select(pair => pair![0]!.GetValue<string>());
}
