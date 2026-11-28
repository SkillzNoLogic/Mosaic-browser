using Mosaic.Core.Productivity;

namespace Mosaic.Core.Tests.Productivity;

public class ShortcutBindingsTests
{
    private static KeyCombo P(string text) => KeyCombo.Parse(text)!.Value;

    private readonly ShortcutCommand newTab = new("File/New Tab", "New Tab", "File", P("ctrl+t"));
    private readonly ShortcutCommand palette = new("Tools/Command Palette…", "Command Palette…", "Tools", P("ctrl+k"));
    private readonly ShortcutCommand reader = new("View/Reader View", "Reader View", "View", P("f9"));
    private readonly ShortcutCommand reload = new("View/Reload Page", "Reload Page", "View", P("ctrl+r"), P("f5"));
    private readonly ShortcutCommand stop = new("View/Stop", "Stop", "View");

    private ShortcutCommand[] All => [newTab, palette, reader, reload, stop];

    [Fact]
    public void DefaultsApplyUntilOverridden()
    {
        var bindings = new ShortcutBindings();
        Assert.Equal(P("ctrl+t"), bindings.Combo(newTab));
        Assert.Equal([P("ctrl+r"), P("f5")], bindings.Combos(reload));
        bindings.Set(P("ctrl+alt+shift+t"), newTab);
        Assert.Equal(P("ctrl+alt+shift+t"), bindings.Combo(newTab));
        Assert.True(bindings.IsCustomised(newTab));
    }

    [Fact]
    public void SettingTheDefaultRemovesTheOverride()
    {
        var bindings = new ShortcutBindings();
        bindings.Set(P("ctrl+j"), newTab);
        bindings.Set(P("ctrl+t"), newTab);
        Assert.Empty(bindings.Overrides);
        // With several defaults, one of them alone is a customisation.
        bindings.Set(P("f5"), reload);
        Assert.True(bindings.IsCustomised(reload));
        Assert.Equal([P("f5")], bindings.Combos(reload));
        bindings.Set(null, stop);
        Assert.False(bindings.IsCustomised(stop));
    }

    [Fact]
    public void ClearingStoresNoShortcut()
    {
        var bindings = new ShortcutBindings();
        bindings.Set(null, reader);
        Assert.Null(bindings.Combo(reader));
        Assert.True(bindings.IsCustomised(reader));
        bindings.Reset(reader);
        Assert.Equal(P("f9"), bindings.Combo(reader));
    }

    [Fact]
    public void DetectsConflictsWithEveryDefault()
    {
        var bindings = new ShortcutBindings();
        Assert.Equal(new ShortcutProblem.Conflict([newTab.Id]), bindings.Problem(P("ctrl+t"), palette.Id, All));
        Assert.Equal(new ShortcutProblem.Conflict([reload.Id]), bindings.Problem(P("f5"), palette.Id, All));
        // A command does not conflict with itself.
        Assert.Null(bindings.Problem(P("ctrl+t"), newTab.Id, All));
        Assert.Null(bindings.Problem(P("ctrl+j"), palette.Id, All));
    }

    [Fact]
    public void ConflictsFollowOverrides()
    {
        var bindings = new ShortcutBindings();
        bindings.Set(P("ctrl+j"), newTab);
        Assert.Null(bindings.Problem(P("ctrl+t"), palette.Id, All));
        Assert.Equal(new ShortcutProblem.Conflict([newTab.Id]), bindings.Problem(P("ctrl+j"), palette.Id, All));
    }

    [Fact]
    public void RejectsShortcutsWithoutModifierAndReservedOnes()
    {
        var bindings = new ShortcutBindings();
        Assert.Equal(new ShortcutProblem.NeedsModifier(), bindings.Problem(P("shift+k"), palette.Id, All));
        Assert.Equal(new ShortcutProblem.NeedsModifier(), bindings.Problem(P("k"), palette.Id, All));
        Assert.Equal(new ShortcutProblem.Reserved("closing the window"), bindings.Problem(P("alt+f4"), palette.Id, All));
        Assert.Equal(new ShortcutProblem.Reserved("text editing"), bindings.Problem(P("ctrl+c"), palette.Id, All));
    }

    [Fact]
    public void AssignTakesTheShortcutFromOthersAndKeepsTheirOtherKeys()
    {
        var bindings = new ShortcutBindings();
        bindings.Assign(P("ctrl+t"), palette, All);
        Assert.Equal(P("ctrl+t"), bindings.Combo(palette));
        Assert.Null(bindings.Combo(newTab));
        bindings.Assign(P("f5"), reader, All);
        Assert.Equal([P("ctrl+r")], bindings.Combos(reload));
        bindings.ResetAll();
        Assert.Empty(bindings.Overrides);
    }

    [Fact]
    public void JsonRoundTripsInTheMacFormat()
    {
        var bindings = new ShortcutBindings();
        bindings.Set(P("ctrl+alt+shift+k"), palette);
        bindings.Set(null, reader);
        var json = bindings.ToJson();
        Assert.Contains("\"combo\": \"ctrl+alt+shift+k\"", json);
        var loaded = ShortcutBindings.FromJson(json);
        Assert.Equal(bindings, loaded);
        Assert.Null(loaded!.Combo(reader));
        // macOS spellings read as their Windows equivalents.
        var mac = ShortcutBindings.FromJson("""{"overrides":{"View/Reader View":{"combo":"shift+cmd+r"},"Bad":{"combo":"hyper+x"}}}""");
        Assert.Equal(P("ctrl+shift+r"), mac!.Combo(reader));
        Assert.False(mac.Overrides.ContainsKey("Bad"));
    }

    [Fact]
    public void CorruptJsonIsNull()
    {
        Assert.Null(ShortcutBindings.FromJson("not json"));
        Assert.Null(ShortcutBindings.FromJson("[]"));
    }
}
