using System.Text.Json;
using System.Text.Json.Serialization;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Productivity;

/// <summary>
/// A keyboard shortcut on Windows: a virtual-key code plus Ctrl, Alt and Shift. The Windows
/// counterpart of macOS's <c>KeyCombo</c> (<c>Sources/MosaicCore/Productivity/KeyCombo.swift</c>).
/// </summary>
/// <remarks>
/// Virtual keys, because every source of key presses speaks them: WinUI's
/// <c>KeyboardAccelerator</c> and key events, and WebView2's accelerator keys. Shortcuts pressed
/// inside Gecko arrive from the agent as chords (<see cref="GeckoChord"/>). The Windows key is
/// not part of a combo: Windows keeps Win+… for itself.
/// <para>Text form, for automation and the settings file: <c>"ctrl+shift+k"</c>, <c>"alt+left"</c>,
/// <c>"f5"</c>, <c>"ctrl+plus"</c>. macOS spellings are accepted too (<c>cmd</c>/<c>accel</c> read
/// as Ctrl, <c>opt</c> as Alt), so the same scripts run on both platforms.</para>
/// </remarks>
[JsonConverter(typeof(KeyComboJsonConverter))]
public readonly record struct KeyCombo(int Key, ShortcutModifiers Modifiers)
{
    // Virtual-key codes (winuser.h).
    public const int Backspace = 0x08, Tab = 0x09, Enter = 0x0D, Escape = 0x1B, Space = 0x20;
    public const int PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24;
    public const int Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28;
    public const int PrintScreen = 0x2C, Insert = 0x2D, Delete = 0x2E;
    public const int NumPad0 = 0x60, Multiply = 0x6A, Add = 0x6B, Subtract = 0x6D, Decimal = 0x6E, Divide = 0x6F;
    public const int F1 = 0x70, F24 = 0x87;
    public const int OemSemicolon = 0xBA, OemPlus = 0xBB, OemComma = 0xBC, OemMinus = 0xBD, OemPeriod = 0xBE;
    public const int OemSlash = 0xBF, OemTilde = 0xC0, OemOpenBracket = 0xDB, OemBackslash = 0xDC;
    public const int OemCloseBracket = 0xDD, OemQuote = 0xDE;

    public static KeyCombo Ctrl(int key) => new(key, ShortcutModifiers.Control);
    public static KeyCombo CtrlShift(int key) => new(key, ShortcutModifiers.Control | ShortcutModifiers.Shift);
    public static KeyCombo Alt(int key) => new(key, ShortcutModifiers.Alt);
    public static KeyCombo Plain(int key) => new(key, ShortcutModifiers.None);
    public static int FunctionKey(int number) => F1 + number - 1;

    public bool HasControl => Modifiers.HasFlag(ShortcutModifiers.Control);
    public bool HasAlt => Modifiers.HasFlag(ShortcutModifiers.Alt);
    public bool HasShift => Modifiers.HasFlag(ShortcutModifiers.Shift);

    /// <summary>1…24 for F1…F24, else null.</summary>
    public int? FunctionNumber => Key is >= F1 and <= F24 ? Key - F1 + 1 : null;

    public bool IsFunctionKey => FunctionNumber is not null;

    public bool IsLetter => Key is >= 'A' and <= 'Z';
    public bool IsDigit => Key is >= '0' and <= '9';

    /// <summary>A key that types a character (letter, digit, punctuation), as opposed to F5, arrows, Tab…</summary>
    public bool IsCharacterKey => IsLetter || IsDigit || Symbols.ContainsKey(Key);

    /// <summary>
    /// A shortcut needs Ctrl or Alt (Shift alone types capitals), unless it is a function key.
    /// </summary>
    public bool HasRequiredModifier => IsFunctionKey || HasControl || HasAlt;

    // ---- Names ----

    /// <summary>Key names: (text form, as Windows menus print it, words for a screen reader, DOM KeyboardEvent.key).</summary>
    private static readonly Dictionary<int, (string Word, string Display, string Spoken, string Dom)> Named = new()
    {
        [Backspace] = ("backspace", "Backspace", "Backspace", "Backspace"),
        [Tab] = ("tab", "Tab", "Tab", "Tab"),
        [Enter] = ("enter", "Enter", "Enter", "Enter"),
        [Escape] = ("escape", "Esc", "Escape", "Escape"),
        [Space] = ("space", "Space", "Space", " "),
        [PageUp] = ("pageup", "Page Up", "Page Up", "PageUp"),
        [PageDown] = ("pagedown", "Page Down", "Page Down", "PageDown"),
        [End] = ("end", "End", "End", "End"),
        [Home] = ("home", "Home", "Home", "Home"),
        [Left] = ("left", "Left", "Left Arrow", "ArrowLeft"),
        [Up] = ("up", "Up", "Up Arrow", "ArrowUp"),
        [Right] = ("right", "Right", "Right Arrow", "ArrowRight"),
        [Down] = ("down", "Down", "Down Arrow", "ArrowDown"),
        [PrintScreen] = ("printscreen", "PrtScn", "Print Screen", "PrintScreen"),
        [Insert] = ("insert", "Ins", "Insert", "Insert"),
        [Delete] = ("delete", "Del", "Delete", "Delete"),
        [Multiply] = ("multiply", "Num *", "Numpad Multiply", "*"),
        [Add] = ("add", "Num +", "Numpad Plus", "+"),
        [Subtract] = ("subtract", "Num -", "Numpad Minus", "-"),
        [Decimal] = ("decimal", "Num .", "Numpad Decimal", "."),
        [Divide] = ("divide", "Num /", "Numpad Divide", "/"),
        [OemPlus] = ("plus", "Plus", "Plus", "="),
        [OemMinus] = ("minus", "Minus", "Minus", "-"),
    };

    /// <summary>Punctuation keys (US layout): the character typed without and with Shift.</summary>
    private static readonly Dictionary<int, (char Plain, char Shifted)> Symbols = new()
    {
        [OemSemicolon] = (';', ':'), [OemPlus] = ('=', '+'), [OemComma] = (',', '<'), [OemMinus] = ('-', '_'),
        [OemPeriod] = ('.', '>'), [OemSlash] = ('/', '?'), [OemTilde] = ('`', '~'), [OemOpenBracket] = ('[', '{'),
        [OemBackslash] = ('\\', '|'), [OemCloseBracket] = (']', '}'), [OemQuote] = ('\'', '"'),
    };

    /// <summary>Digits with Shift on a US layout, as a page sees them (<c>KeyboardEvent.key</c>).</summary>
    private const string ShiftedDigits = ")!@#$%^&*(";

    /// <summary>The key alone, as Windows menus print it ("K", "F5", "Page Down", "Plus").</summary>
    public string KeyDisplay
    {
        get
        {
            if (FunctionNumber is { } number) return $"F{number}";
            if (Named.TryGetValue(Key, out var name)) return name.Display;
            if (Symbols.TryGetValue(Key, out var symbol)) return symbol.Plain.ToString();
            if (Key is >= NumPad0 and <= NumPad0 + 9) return $"Num {Key - NumPad0}";
            if (IsLetter || IsDigit) return ((char)Key).ToString();
            return $"0x{Key:X2}";
        }
    }

    /// <summary>"Ctrl+Shift+K": modifiers in the order Windows menus use.</summary>
    public string DisplayString
    {
        get
        {
            var text = "";
            if (HasControl) text += "Ctrl+";
            if (HasAlt) text += "Alt+";
            if (HasShift) text += "Shift+";
            return text + KeyDisplay;
        }
    }

    /// <summary>Words for Narrator ("Control Shift K").</summary>
    public string SpokenString
    {
        get
        {
            var words = new List<string>();
            if (HasControl) words.Add("Control");
            if (HasAlt) words.Add("Alt");
            if (HasShift) words.Add("Shift");
            words.Add(Named.TryGetValue(Key, out var name) ? name.Spoken : KeyDisplay);
            return string.Join(' ', words);
        }
    }

    public override string ToString() => DisplayString;

    // ---- Text form ----

    /// <summary>Inverse of <see cref="Parse"/>: "ctrl+alt+shift+k".</summary>
    public string StringValue
    {
        get
        {
            var parts = new List<string>();
            if (HasControl) parts.Add("ctrl");
            if (HasAlt) parts.Add("alt");
            if (HasShift) parts.Add("shift");
            parts.Add(KeyWord);
            return string.Join('+', parts);
        }
    }

    private string KeyWord
    {
        get
        {
            if (FunctionNumber is { } number) return $"f{number}";
            if (Named.TryGetValue(Key, out var name)) return name.Word;
            if (Symbols.TryGetValue(Key, out var symbol)) return symbol.Plain.ToString();
            if (Key is >= NumPad0 and <= NumPad0 + 9) return $"num{Key - NumPad0}";
            if (IsLetter || IsDigit) return char.ToLowerInvariant((char)Key).ToString();
            return $"vk{Key}";
        }
    }

    /// <summary>
    /// Parses "ctrl+shift+k", "alt+left", "f5", "ctrl+plus", "ctrl++" (the plus key),
    /// "ctrl+}" (Shift implied by the character). Case-insensitive. Null when it is not a shortcut.
    /// </summary>
    public static KeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        var tokens = text.Split('+').ToList();
        // "ctrl++": the last key is the plus sign.
        if (text.EndsWith("++", StringComparison.Ordinal))
        {
            tokens.RemoveRange(tokens.Count - 2, 2);
            tokens.Add("+");
        }
        else if (text == "+")
        {
            tokens = ["+"];
        }
        var last = tokens[^1];
        if (last.Length == 0) return null;

        var modifiers = ShortcutModifiers.None;
        foreach (var word in tokens.Take(tokens.Count - 1))
        {
            switch (word.Trim().ToLowerInvariant())
            {
                case "ctrl" or "control" or "cmd" or "command" or "accel" or "⌘" or "⌃": modifiers |= ShortcutModifiers.Control; break;
                case "alt" or "opt" or "option" or "⌥": modifiers |= ShortcutModifiers.Alt; break;
                case "shift" or "⇧": modifiers |= ShortcutModifiers.Shift; break;
                default: return null;  // "win+…" included: Windows keeps those
            }
        }
        if (KeyFromWord(last.Trim()) is not { } key) return null;
        return new KeyCombo(key.Key, modifiers | (key.Shift ? ShortcutModifiers.Shift : ShortcutModifiers.None));
    }

    private static (int Key, bool Shift)? KeyFromWord(string word)
    {
        if (word.Length == 1)
        {
            var c = word[0];
            if (c is >= 'a' and <= 'z') return (char.ToUpperInvariant(c), false);
            // An upper-case letter means Shift, as on macOS.
            if (c is >= 'A' and <= 'Z') return (c, true);
            if (c is >= '0' and <= '9') return (c, false);
            var digit = ShiftedDigits.IndexOf(c);
            if (digit >= 0) return ('0' + digit, true);
            foreach (var (vk, symbol) in Symbols)
            {
                if (symbol.Plain == c) return (vk, false);
                if (symbol.Shifted == c) return (vk, true);
            }
            return null;
        }
        var lower = word.ToLowerInvariant();
        foreach (var (vk, name) in Named)
        {
            if (name.Word == lower) return (vk, false);
        }
        switch (lower)
        {
            case "esc": return (Escape, false);
            case "return": return (Enter, false);
            case "del" or "forwarddelete": return (Delete, false);
            case "ins": return (Insert, false);
            case "pgup": return (PageUp, false);
            case "pgdn": return (PageDown, false);
            case "equal" or "equals": return (OemPlus, false);
        }
        if (lower.StartsWith('f') && int.TryParse(lower.AsSpan(1), out var number) && number is >= 1 and <= 24)
        {
            return (FunctionKey(number), false);
        }
        if (lower.StartsWith("num", StringComparison.Ordinal) && lower.Length == 4 && char.IsAsciiDigit(lower[3]))
        {
            return (NumPad0 + (lower[3] - '0'), false);
        }
        if (lower.StartsWith("vk", StringComparison.Ordinal) && int.TryParse(lower.AsSpan(2), out var vkCode) && vkCode is > 0 and < 256)
        {
            return (vkCode, false);
        }
        return null;
    }

    // ---- Gecko ----

    /// <summary>
    /// The chord the Gecko agent computes for this key press on Windows (<c>chordOf</c> in
    /// <c>Resources/gecko/agent.sys.mjs</c>): "accel" (Ctrl), "alt", "shift", then
    /// <c>KeyboardEvent.key</c> (letters lower-case; Shift dropped when the character implies it).
    /// US layout for the characters Shift produces.
    /// </summary>
    public string GeckoChord
    {
        get
        {
            var key = DomKey;
            var shiftImplied = false;
            if (HasShift && !HasAlt)
            {
                // With Shift the page sees the shifted character ("!" for Shift+1, "+" for Shift+=).
                if (IsDigit) { key = ShiftedDigits[Key - '0'].ToString(); shiftImplied = true; }
                else if (Symbols.TryGetValue(Key, out var symbol)) { key = symbol.Shifted.ToString(); shiftImplied = true; }
            }
            var parts = new List<string>();
            if (HasControl) parts.Add("accel");
            if (HasAlt) parts.Add("alt");
            if (HasShift && !shiftImplied) parts.Add("shift");
            parts.Add(key.Length == 1 ? key.ToLowerInvariant() : key);
            return string.Join('+', parts);
        }
    }

    /// <summary>The key as a DOM <c>KeyboardEvent.key</c> value without Shift (letters lower-case).</summary>
    public string DomKey
    {
        get
        {
            if (FunctionNumber is { } number) return $"F{number}";
            if (Named.TryGetValue(Key, out var name)) return name.Dom;
            if (Symbols.TryGetValue(Key, out var symbol)) return symbol.Plain.ToString();
            if (Key is >= NumPad0 and <= NumPad0 + 9) return ((char)('0' + Key - NumPad0)).ToString();
            if (IsLetter || IsDigit) return char.ToLowerInvariant((char)Key).ToString();
            return "Unidentified";
        }
    }

    // ---- Reserved ----

    /// <summary>
    /// Shortcuts Windows (or text editing everywhere) keeps for itself: assigning one would never
    /// reach Mosaic, or would take copy and paste away from the address bar and pages.
    /// </summary>
    public static IReadOnlyDictionary<KeyCombo, string> SystemReserved { get; } = BuildReserved();

    private static Dictionary<KeyCombo, string> BuildReserved()
    {
        var alt = ShortcutModifiers.Alt;
        var ctrl = ShortcutModifiers.Control;
        var shift = ShortcutModifiers.Shift;
        var reserved = new Dictionary<KeyCombo, string>
        {
            [new(FunctionKey(4), alt)] = "closing the window",
            [new(FunctionKey(4), ctrl)] = "closing the window",
            [new(Tab, alt)] = "switching windows",
            [new(Tab, alt | shift)] = "switching windows",
            [new(Tab, ctrl | alt)] = "switching windows",
            [new(Escape, alt)] = "switching windows",
            [new(Escape, alt | shift)] = "switching windows",
            [new(Escape, ctrl)] = "the Start menu",
            [new(Escape, ctrl | shift)] = "Task Manager",
            [new(Delete, ctrl | alt)] = "the security screen",
            [new(Space, alt)] = "the window menu",
            [new(PrintScreen, alt)] = "screenshots",
            [new(PrintScreen, ctrl)] = "screenshots",
            [new(PrintScreen, shift)] = "screenshots",
            [new(Space, ctrl)] = "switching input languages",
            [new(Space, ctrl | shift)] = "switching input languages",
        };
        foreach (var key in new[] { 'C', 'X', 'V', 'Z', 'Y', 'A' })
        {
            reserved[new(key, ctrl)] = "text editing";
        }
        reserved[new('Z', ctrl | shift)] = "text editing";
        foreach (var key in new[] { Left, Right, Backspace, Delete, Insert })
        {
            reserved[new(key, ctrl)] = "text editing";
        }
        reserved[new(Insert, shift)] = "text editing";
        reserved[new(Delete, shift)] = "text editing";
        return reserved;
    }

    /// <summary>
    /// Why Windows would never deliver this as a shortcut, or null. Besides
    /// <see cref="SystemReserved"/>: Ctrl+Alt with a character key is how AltGr arrives, which
    /// types characters (@, €, {) on many keyboard layouts.
    /// </summary>
    public string? ReservedReason
    {
        get
        {
            if (SystemReserved.TryGetValue(this, out var reason)) return reason;
            if (HasControl && HasAlt && !HasShift && IsCharacterKey) return "typing characters with AltGr";
            return null;
        }
    }
}

/// <summary>Stores a <see cref="KeyCombo"/> as its text form.</summary>
public sealed class KeyComboJsonConverter : JsonConverter<KeyCombo>
{
    public override KeyCombo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        KeyCombo.Parse(reader.GetString()) ?? throw new JsonException($"Not a shortcut: {reader.GetString()}");

    public override void Write(Utf8JsonWriter writer, KeyCombo value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.StringValue);
}
