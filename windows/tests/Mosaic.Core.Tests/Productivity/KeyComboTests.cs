using System.Text.Json;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;

namespace Mosaic.Core.Tests.Productivity;

public class KeyComboTests
{
    private static KeyCombo Parse(string text) => KeyCombo.Parse(text) ?? throw new Xunit.Sdk.XunitException($"cannot parse {text}");

    [Fact]
    public void DisplayUsesWindowsMenuOrderAndNames()
    {
        var combo = new KeyCombo(KeyCombo.Left, ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift);
        Assert.Equal("Ctrl+Alt+Shift+Left", combo.DisplayString);
        Assert.Equal("F5", KeyCombo.Plain(KeyCombo.FunctionKey(5)).DisplayString);
        Assert.Equal("Ctrl+Page Down", KeyCombo.Ctrl(KeyCombo.PageDown).DisplayString);
        Assert.Equal("Ctrl+Plus", Parse("ctrl+plus").DisplayString);
        Assert.Equal("Ctrl+K", KeyCombo.Ctrl('K').DisplayString);
        Assert.Equal("Control Shift K", KeyCombo.CtrlShift('K').SpokenString);
        Assert.Equal("Alt Left Arrow", KeyCombo.Alt(KeyCombo.Left).SpokenString);
    }

    [Fact]
    public void TextFormRoundTrips()
    {
        foreach (var text in new[] { "ctrl+k", "ctrl+shift+r", "ctrl+alt+f12", "alt+left", "f5", "ctrl+tab", "ctrl+pagedown",
                     "ctrl+plus", "ctrl+minus", "ctrl+0", "ctrl+shift+delete", "ctrl+;", "ctrl+]", "num5", "ctrl+space", "ctrl+add" })
        {
            var combo = Parse(text);
            Assert.Equal(combo, Parse(combo.StringValue));
        }
        Assert.Equal("ctrl+shift+k", Parse("Shift+Ctrl+K").StringValue);
    }

    [Fact]
    public void ParsingAcceptsMacSpellingsAndShiftedCharacters()
    {
        Assert.Equal(KeyCombo.CtrlShift('R'), Parse("shift+cmd+r"));
        Assert.Equal(new KeyCombo('K', ShortcutModifiers.Alt | ShortcutModifiers.Control), Parse("opt+accel+k"));
        // An upper-case letter means Shift, as on macOS.
        Assert.Equal(KeyCombo.CtrlShift('K'), Parse("ctrl+K"));
        // "}" is Shift+]; "++" is the plus sign (Shift+=).
        Assert.Equal(KeyCombo.CtrlShift(KeyCombo.OemCloseBracket), Parse("ctrl+}"));
        Assert.Equal(KeyCombo.CtrlShift(KeyCombo.OemPlus), Parse("ctrl++"));
        Assert.Equal(KeyCombo.Ctrl(KeyCombo.OemPlus), Parse("ctrl+="));
        Assert.Equal(KeyCombo.Plain(KeyCombo.Escape), Parse("esc"));
    }

    [Fact]
    public void RefusesWhatIsNotAShortcut()
    {
        Assert.Null(KeyCombo.Parse(""));
        Assert.Null(KeyCombo.Parse("win+d"));
        Assert.Null(KeyCombo.Parse("hyper+k"));
        Assert.Null(KeyCombo.Parse("ctrl+banana"));
        Assert.Null(KeyCombo.Parse("ctrl+f25"));
        Assert.Null(KeyCombo.Parse("ctrl+"));
    }

    [Fact]
    public void StoredAsText()
    {
        var json = JsonSerializer.Serialize(new[] { KeyCombo.CtrlShift('R') },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Equal("[\"ctrl+shift+r\"]", json);
        Assert.Equal([KeyCombo.CtrlShift('R')], JsonSerializer.Deserialize<KeyCombo[]>(json)!);
    }

    [Fact]
    public void RequiredModifier()
    {
        Assert.True(KeyCombo.Ctrl('K').HasRequiredModifier);
        Assert.True(KeyCombo.Alt('D').HasRequiredModifier);
        Assert.True(KeyCombo.Plain(KeyCombo.FunctionKey(3)).HasRequiredModifier);
        Assert.False(new KeyCombo('K', ShortcutModifiers.Shift).HasRequiredModifier);
        Assert.False(KeyCombo.Plain('K').HasRequiredModifier);
        Assert.False(KeyCombo.Plain(KeyCombo.Escape).HasRequiredModifier);
    }

    [Fact]
    public void ReservedByWindowsAndTextEditing()
    {
        Assert.Equal("closing the window", Parse("alt+f4").ReservedReason);
        Assert.Equal("switching windows", Parse("alt+tab").ReservedReason);
        Assert.Equal("Task Manager", Parse("ctrl+shift+esc").ReservedReason);
        Assert.Equal("text editing", Parse("ctrl+c").ReservedReason);
        Assert.Equal("text editing", Parse("ctrl+v").ReservedReason);
        Assert.Equal("text editing", Parse("ctrl+left").ReservedReason);
        // Ctrl+Alt+character is AltGr on many layouts.
        Assert.Equal("typing characters with AltGr", Parse("ctrl+alt+q").ReservedReason);
        Assert.Null(Parse("ctrl+alt+shift+b").ReservedReason);
        Assert.Null(Parse("ctrl+alt+f5").ReservedReason);
        Assert.Null(Parse("ctrl+k").ReservedReason);
    }

    [Theory]
    [InlineData("ctrl+k", "accel+k")]
    [InlineData("ctrl+shift+k", "accel+shift+k")]
    [InlineData("f5", "F5")]
    [InlineData("alt+left", "alt+ArrowLeft")]
    [InlineData("ctrl+tab", "accel+Tab")]
    [InlineData("ctrl+shift+tab", "accel+shift+Tab")]
    [InlineData("ctrl+pagedown", "accel+PageDown")]
    [InlineData("ctrl+shift+1", "accel+!")]
    [InlineData("ctrl++", "accel++")]
    [InlineData("ctrl+plus", "accel+=")]
    [InlineData("ctrl+alt+shift+b", "accel+alt+shift+b")]
    [InlineData("alt+shift+1", "alt+shift+1")]
    [InlineData("ctrl+space", "accel+ ")]
    public void GeckoChordsMatchTheAgent(string combo, string chord) => Assert.Equal(chord, Parse(combo).GeckoChord);
}
