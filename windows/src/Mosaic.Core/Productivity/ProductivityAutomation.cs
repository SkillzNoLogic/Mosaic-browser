using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;

namespace Mosaic.Core.Productivity;

/// <summary>
/// Automation commands for the productivity area, with the macOS names
/// (<c>Sources/Mosaic/Features/Productivity/Productivity+Automation.swift</c>):
/// <code>
/// palette            open | close | toggle
/// paletteQuery       &lt;query&gt; [out]        sets the query; writes the results to out
/// palettePerform     &lt;action id&gt; | selection
/// reader             toggle | on | off    for the selected tab
/// readerSettings     {"fontSize":7,"theme":"sepia",…}
/// setShortcut        &lt;command id&gt;=&lt;ctrl+shift+k&gt; [out]   fails on a conflict (reason in out); end with
///                    "!" to take the shortcut from the other command; "&lt;id&gt;=" removes the shortcut
/// resetShortcut      &lt;command id&gt; | all
/// performShortcut    &lt;command id&gt; | key:&lt;ctrl+k&gt;   runs the command (key: runs whatever the keys are bound to)
/// speedDial          {"op":"add","title":"…","url":"…"} · {"op":"edit","url":"…","title":"…","newURL":"…"}
///                    {"op":"remove","url":"…"} · {"op":"move","url":"…","to":0} · {"op":"hide","url":"…"}
///                    {"op":"restoreHidden"} · {"op":"showTopSites","value":false} · {"op":"showIcons","value":false}
/// showShortcutsSettings                   opens Settings › Shortcuts &amp; Gestures
/// geckoForwarding    [out]                pushes the table to Gecko again; writes it to out
/// </code>
/// Replies carry the result too (<c>{"ok":true,"result":"…"}</c>), so scripts need no out file.
/// The state (palette, reader per tab, shortcuts, Gecko forwarding table, Speed Dial) is in
/// <c>dump</c> under "productivity".
/// </summary>
public static class ProductivityAutomation
{
    private static int registered;

    /// <summary>Registers the commands and the dump key; safe to call more than once.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1) return;
        AutomationRegistry.Register(HandleAsync);
        AutomationRegistry.RegisterState("productivity", model => State(model));
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static async Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output)
    {
        var p = model.Productivity;
        switch (command)
        {
            case "palette":
                await p.StartAsync();
                switch (value)
                {
                    case "close": p.Palette.Dismiss(restoringFocus: false); break;
                    case "toggle": p.Palette.Toggle(); break;
                    default: if (!p.Palette.IsPresented) p.Palette.Present(); break;
                }
                return Reply(new JsonObject { ["presented"] = p.Palette.IsPresented }, output);
            case "paletteQuery":
                await p.StartAsync();
                p.Palette.Query = value;
                return Reply(PaletteState(p.Palette, CommandPalette.ResultLimit), output);
            case "palettePerform":
            {
                await p.StartAsync();
                if (value == "selection")
                {
                    p.Palette.PerformSelection();
                    return AutomationRegistry.Ok();
                }
                if (p.Palette.Perform(value)) return AutomationRegistry.Ok();
                if (output is not null) await File.WriteAllTextAsync(output, new JsonObject { ["error"] = $"no action {value}" }.ToJsonString());
                return AutomationRegistry.Error($"no action {value}");
            }
            case "reader":
            {
                await p.StartAsync();
                if (model.SelectedTab is not { } tab) return AutomationRegistry.Error("no tab");
                switch (value)
                {
                    case "on": p.Reader.SetActive(true, tab); break;
                    case "off": p.Reader.SetActive(false, tab); break;
                    default: p.Reader.Toggle(tab); break;
                }
                return AutomationRegistry.Ok();
            }
            case "readerSettings":
            {
                await p.StartAsync();
                JsonObject? changes;
                try { changes = JsonNode.Parse(value) as JsonObject; }
                catch (JsonException) { changes = null; }
                if (changes is null) return AutomationRegistry.Error("expected a JSON object");
                p.Reader.Update(p.Reader.Settings.Merging(changes));
                return Reply(new JsonObject { ["settings"] = p.Reader.Settings.ToJsonObject() }, output);
            }
            case "setShortcut":
                await p.StartAsync();
                return Reply(new JsonObject { ["result"] = SetShortcut(p.Shortcuts, value) }, output);
            case "resetShortcut":
                await p.StartAsync();
                if (value == "all") p.Shortcuts.ResetAll();
                else if (p.Shortcuts.Command(value) is { } reset) p.Shortcuts.Reset(reset);
                else return AutomationRegistry.Error($"no command {value}");
                return AutomationRegistry.Ok();
            case "performShortcut":
            {
                await p.StartAsync();
                bool ran;
                if (value.StartsWith("key:", StringComparison.Ordinal))
                {
                    if (KeyCombo.Parse(value[4..]) is not { } combo) return AutomationRegistry.Error($"cannot parse {value[4..]}");
                    ran = p.Shortcuts.Perform(combo);
                }
                else
                {
                    ran = p.Shortcuts.Perform(value);
                }
                return Reply(new JsonObject { ["result"] = ran ? "ok" : "not performed" }, output);
            }
            case "speedDial":
                await p.StartAsync();
                return Reply(new JsonObject { ["result"] = p.SpeedDial.Edit(value) }, output);
            case "showShortcutsSettings":
                if (ProductivityIntegration.OpenSettings is not { } open) return AutomationRegistry.Error("Settings are not available yet");
                open(model, SettingsCategory.Shortcuts);
                return AutomationRegistry.Ok();
            case "geckoForwarding":
                await p.StartAsync();
                p.Shortcuts.PushToGecko(force: true);
                return Reply(new JsonObject { ["table"] = ForwardingTable(p.Shortcuts) }, output);
            default:
                return null;
        }
    }

    /// <summary>{"ok":true, …fields}; the fields are also written to <paramref name="output"/> when given.</summary>
    private static AutomationReply Reply(JsonObject fields, string? output)
    {
        if (output is not null) File.WriteAllText(output, fields.ToJsonString(Indented));
        var reply = new JsonObject { ["ok"] = true };
        foreach (var (key, node) in fields) reply[key] = node?.DeepClone();
        return new AutomationReply(reply.ToJsonString());
    }

    internal static string SetShortcut(ShortcutManager shortcuts, string value)
    {
        var separator = value.LastIndexOf('=');
        // "ctrl+=": the '=' key itself; the separator is the one before it.
        if (separator > 0 && separator == value.Length - 1 && value[separator - 1] == '+')
        {
            separator = value.LastIndexOf('=', separator - 1);
        }
        if (separator < 0) return "expected <id>=<shortcut>";
        var id = value[..separator];
        var text = value[(separator + 1)..];
        var replace = text.Length > 1 && text.EndsWith('!');
        if (replace) text = text[..^1];
        if (shortcuts.Command(id) is not { } command) return $"no command {id}";
        if (text.Length == 0)
        {
            shortcuts.Clear(command);
            return "cleared";
        }
        if (KeyCombo.Parse(text) is not { } combo) return $"cannot parse {text}";
        switch (shortcuts.Problem(combo, command))
        {
            case null:
                shortcuts.Assign(combo, command);
                return "ok";
            case ShortcutProblem.Conflict conflict when replace:
                shortcuts.Assign(combo, command);
                return $"ok, replaced {string.Join(", ", conflict.CommandIds)}";
            case ShortcutProblem.Conflict conflict:
                return $"conflict: {string.Join(", ", conflict.CommandIds)}";
            case ShortcutProblem.NeedsModifier:
                return "needs a modifier";
            case ShortcutProblem.Reserved reserved:
                return $"reserved by Windows for {reserved.Reason}";
            default:
                return "refused";
        }
    }

    // ---- dump ----

    public static JsonObject State(BrowserModel model)
    {
        var p = model.Productivity;
        var shortcuts = new JsonArray();
        foreach (var command in p.Shortcuts.Commands)
        {
            shortcuts.Add(new JsonObject
            {
                ["id"] = command.Id,
                ["group"] = command.Group,
                ["title"] = command.Title,
                ["default"] = string.Join(", ", command.Defaults.Select(combo => combo.DisplayString)),
                ["current"] = string.Join(", ", p.Shortcuts.Combos(command).Select(combo => combo.DisplayString)),
                ["customised"] = p.Shortcuts.IsCustomised(command),
            });
        }
        return new JsonObject
        {
            ["started"] = p.IsStarted,
            ["palette"] = PaletteState(p.Palette, 15),
            ["paletteProviders"] = new JsonArray(PaletteRegistry.ProviderIds.Select(id => (JsonNode)id).ToArray()),
            ["reader"] = p.Reader.AutomationState(),
            ["shortcuts"] = shortcuts,
            ["shortcutsSuspended"] = p.Shortcuts.IsSuspended,
            ["accelerators"] = new JsonArray(p.Shortcuts.ActiveBindings.Select(b => (JsonNode)new JsonArray(b.Combo.DisplayString, b.CommandId)).ToArray()),
            ["geckoForwarding"] = ForwardingTable(p.Shortcuts),
            ["speedDial"] = p.SpeedDial.AutomationState(),
            ["saveErrors"] = new JsonArray(new[] { p.Shortcuts.SaveError, p.SpeedDial.SaveError }.OfType<string>().Select(e => (JsonNode)e).ToArray()),
        };
    }

    private static JsonArray ForwardingTable(ShortcutManager shortcuts) =>
        new(shortcuts.GeckoForwardingPairs().Select(pair => (JsonNode)new JsonArray(pair.Chord, pair.EventType)).ToArray());

    private static JsonObject PaletteState(CommandPalette palette, int limit)
    {
        var results = new JsonArray();
        foreach (var action in palette.Results.Take(limit))
        {
            results.Add(new JsonObject
            {
                ["id"] = action.Id,
                ["title"] = action.Title,
                ["subtitle"] = action.Subtitle ?? "",
                ["kind"] = action.Kind.Id(),
                ["shortcut"] = action.Shortcut?.DisplayString ?? "",
            });
        }
        return new JsonObject
        {
            ["presented"] = palette.IsPresented,
            ["query"] = palette.Query,
            ["selection"] = palette.Selection,
            ["count"] = palette.Results.Count,
            ["results"] = results,
        };
    }
}
