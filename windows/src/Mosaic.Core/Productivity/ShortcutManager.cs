using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Productivity;

/// <summary>
/// Customisable keyboard shortcuts for every command in <see cref="ShortcutCommands"/>: the
/// user's changes (<see cref="ShortcutBindings"/>, saved in the profile), conflict rules, and
/// the forwarding table for Gecko pages. The app turns <see cref="ActiveBindings"/> into the
/// window's <c>KeyboardAccelerator</c>s and rebuilds them on <see cref="Changed"/>.
/// Port of <c>Sources/Mosaic/Features/Productivity/ShortcutManager.swift</c>.
/// </summary>
/// <remarks>
/// A Gecko page is another process's window, so key presses there never reach WinUI. The agent
/// forwards the chords Mosaic sends it (<c>setForwardedShortcuts</c>) as snapshot events
/// "productivity.shortcut:&lt;command id&gt;", which <see cref="ProductivityServices"/> runs here.
/// UI thread only.
/// </remarks>
public sealed class ShortcutManager
{
    /// <summary>Event type prefix the agent queues for a forwarded shortcut.</summary>
    public const string GeckoEventPrefix = "productivity.shortcut:";

    /// <summary>Event type for a built-in agent chord the user moved elsewhere: swallowed, nothing runs.</summary>
    public const string GeckoSwallowed = "productivity.none";

    /// <summary>
    /// Chords the agent forwards by itself on Windows (<c>FORWARDED</c>, <c>FORWARDED_WITH_SHIFT</c>
    /// in <c>Resources/gecko/agent.sys.mjs</c>) and that the Windows model acts on, with the
    /// command each stands for. (The agent's Ctrl+Shift+B, "toggleBookmarksBar", is not acted on
    /// by the Windows model, so whatever command has Ctrl+Shift+B is forwarded through the table.)
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> AgentBuiltIns = new Dictionary<string, string>
    {
        ["accel+l"] = ShortcutCommands.OpenLocation,
        ["accel+t"] = ShortcutCommands.NewTab,
        ["accel+n"] = ShortcutCommands.NewTab,
        ["accel+w"] = ShortcutCommands.CloseTab,
        ["accel+shift+e"] = ShortcutCommands.ToggleEngine,
    };

    private readonly BrowserModel model;
    private readonly ProductivityFiles files;
    private ShortcutBindings bindings = new();
    private bool suspended;
    private string? lastPushed;

    internal ShortcutManager(BrowserModel model, ProductivityFiles files)
    {
        this.model = model;
        this.files = files;
    }

    /// <summary>Raised whenever the shortcuts in effect may have changed (bindings, catalogue, suspension).</summary>
    public event Action? Changed;

    public IReadOnlyList<ShortcutCommand> Commands => ShortcutCommands.All;

    public ShortcutBindings Bindings => bindings;

    /// <summary>The last save error, for the settings page; null after a successful save.</summary>
    public string? SaveError { get; private set; }

    /// <summary>
    /// While true (the settings page is recording a new shortcut) no shortcut runs and
    /// <see cref="ActiveBindings"/> is empty, so the keys reach the recorder.
    /// </summary>
    public bool IsSuspended
    {
        get => suspended;
        set
        {
            if (suspended == value) return;
            suspended = value;
            Changed?.Invoke();
        }
    }

    // ---- Loading ----

    /// <summary>Reads the saved bindings (off the UI thread) and applies them.</summary>
    internal async Task LoadAsync()
    {
        var text = await Task.Run(() => files.Read(ProductivityFiles.ShortcutsFile));
        if (text is not null && ShortcutBindings.FromJson(text) is { } loaded)
        {
            bindings = loaded;
        }
        Refresh();
    }

    // ---- Queries ----

    public ShortcutCommand? Command(string id) => Commands.FirstOrDefault(command => command.Id == id);

    public IReadOnlyList<KeyCombo> Combos(ShortcutCommand command) => bindings.Combos(command);

    public KeyCombo? Combo(ShortcutCommand command) => bindings.Combo(command);

    /// <summary>The main shortcut of the command with <paramref name="commandId"/>, for hints in the palette.</summary>
    public KeyCombo? Combo(string commandId) => Command(commandId) is { } command ? bindings.Combo(command) : null;

    public bool IsCustomised(ShortcutCommand command) => bindings.IsCustomised(command);

    public ShortcutProblem? Problem(KeyCombo combo, ShortcutCommand command) => bindings.Problem(combo, command.Id, Commands);

    /// <summary>"Group › Title", or the id of an unknown command.</summary>
    public string TitleOf(string commandId) => Command(commandId) is { } command ? $"{command.Group} › {command.Title}" : commandId;

    /// <summary>
    /// Every shortcut in effect and its command, for the window's accelerators. A combo appears
    /// once (the first command in catalogue order wins). Empty while <see cref="IsSuspended"/>.
    /// </summary>
    public IReadOnlyList<(KeyCombo Combo, string CommandId)> ActiveBindings
    {
        get
        {
            if (suspended) return [];
            var seen = new HashSet<KeyCombo>();
            var result = new List<(KeyCombo, string)>();
            foreach (var command in Commands)
            {
                foreach (var combo in bindings.Combos(command))
                {
                    if (seen.Add(combo)) result.Add((combo, command.Id));
                }
            }
            return result;
        }
    }

    /// <summary>The command a key press triggers, or null (also while suspended).</summary>
    public string? Match(KeyCombo combo)
    {
        foreach (var (active, id) in ActiveBindings)
        {
            if (active == combo) return id;
        }
        return null;
    }

    // ---- Changes ----

    /// <summary>Gives <paramref name="combo"/> to <paramref name="command"/>, taking it from any command that had it.</summary>
    public void Assign(KeyCombo combo, ShortcutCommand command)
    {
        bindings.Assign(combo, command, Commands);
        Save();
    }

    /// <summary>Removes the command's shortcuts.</summary>
    public void Clear(ShortcutCommand command)
    {
        bindings.Set(null, command);
        Save();
    }

    public void Reset(ShortcutCommand command)
    {
        bindings.Reset(command);
        Save();
    }

    public void ResetAll()
    {
        bindings.ResetAll();
        Save();
    }

    /// <summary>Runs a command as if its shortcut had been pressed. False when it is unknown, cannot run now, or shortcuts are suspended.</summary>
    public bool Perform(string commandId) => !suspended && ShortcutCommands.Perform(commandId, model);

    /// <summary>Runs whatever <paramref name="combo"/> is bound to. False when nothing ran.</summary>
    public bool Perform(KeyCombo combo) => Match(combo) is { } id && Perform(id);

    /// <summary>The catalogue changed (an area registered a command): tell listeners and Gecko.</summary>
    public void Refresh()
    {
        PushToGecko();
        Changed?.Invoke();
    }

    private void Save()
    {
        var json = bindings.Overrides.Count == 0 ? null : bindings.ToJson();
        SaveError = null;
        files.Write(ProductivityFiles.ShortcutsFile, json, error => SaveError = $"Shortcuts could not be saved: {error.Message}");
        Refresh();
    }

    // ---- Gecko ----

    /// <summary>
    /// The forwarding table for the agent, as [[chord, event type], …] sorted by chord: every
    /// shortcut in effect, minus what the agent already forwards correctly; built-in chords the
    /// user moved elsewhere are swallowed.
    /// </summary>
    public IReadOnlyList<(string Chord, string EventType)> GeckoForwardingPairs()
    {
        var commands = Commands;
        var pairs = new Dictionary<string, string>();
        foreach (var command in commands)
        {
            foreach (var combo in bindings.Combos(command))
            {
                var chord = combo.GeckoChord;
                if (AgentBuiltIns.TryGetValue(chord, out var builtIn) && builtIn == command.Id) continue;
                pairs.TryAdd(chord, GeckoEventPrefix + command.Id);
            }
        }
        foreach (var (chord, builtIn) in AgentBuiltIns)
        {
            if (pairs.ContainsKey(chord)) continue;
            // Ctrl+N has no command of its own in Mosaic; the agent maps it to New Tab, keep that.
            if (chord == "accel+n") continue;
            // A command nobody registered (yet) keeps the agent's behaviour.
            if (commands.FirstOrDefault(c => c.Id == builtIn) is not { } command) continue;
            var stillBound = bindings.Combos(command).Any(combo => combo.GeckoChord == chord);
            if (!stillBound) pairs[chord] = GeckoSwallowed;
        }
        return pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)).ToList();
    }

    /// <summary>Sends the forwarding table to Gecko when it runs (again after each start; skipped when unchanged).</summary>
    internal void PushToGecko(bool force = false)
    {
        if (model.Gecko.Status != GeckoStatus.Running) return;
        var table = new JsonArray();
        foreach (var (chord, type) in GeckoForwardingPairs())
        {
            table.Add(new JsonArray(chord, type));
        }
        var text = table.ToJsonString();
        if (!force && text == lastPushed) return;
        lastPushed = text;
        model.Gecko.Send("setForwardedShortcuts", table);
    }

    /// <summary>Gecko started (again): its table is empty.</summary>
    internal void GeckoStarted()
    {
        lastPushed = null;
        PushToGecko(force: true);
    }

    /// <summary>An event from the agent; true when it was one of this area's.</summary>
    internal bool HandleGeckoEvent(string type)
    {
        if (type == GeckoSwallowed) return true;
        if (!type.StartsWith(GeckoEventPrefix, StringComparison.Ordinal)) return false;
        Perform(type[GeckoEventPrefix.Length..]);
        return true;
    }
}
