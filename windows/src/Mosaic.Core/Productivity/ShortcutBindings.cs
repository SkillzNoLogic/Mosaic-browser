using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Productivity;

/// <summary>Why a shortcut cannot simply be given to a command.</summary>
public abstract record ShortcutProblem
{
    /// <summary>Needs Ctrl or Alt (or a function key).</summary>
    public sealed record NeedsModifier : ShortcutProblem;

    /// <summary>Windows (or text editing) uses it for <paramref name="Reason"/>.</summary>
    public sealed record Reserved(string Reason) : ShortcutProblem;

    /// <summary>Already assigned to these commands (ids).</summary>
    public sealed record Conflict(IReadOnlyList<string> CommandIds) : ShortcutProblem
    {
        public bool Equals(Conflict? other) => other is not null && CommandIds.SequenceEqual(other.CommandIds);
        public override int GetHashCode() => CommandIds.Count;
    }
}

/// <summary>
/// The user's changes to the default shortcuts, and the rules for making them. Port of
/// <c>ShortcutBindings</c> (<c>Sources/MosaicCore/Productivity/ShortcutBindings.swift</c>).
/// </summary>
/// <remarks>
/// An override is one shortcut, or none (<c>null</c>): it replaces all of a command's defaults.
/// Stored as macOS stores it: <c>{"overrides": {"File/New Tab": {"combo": "ctrl+t"}, "View/Stop": {}}}</c>.
/// </remarks>
public sealed class ShortcutBindings : IEquatable<ShortcutBindings>
{
    private readonly Dictionary<string, KeyCombo?> overrides;

    public ShortcutBindings(IReadOnlyDictionary<string, KeyCombo?>? overrides = null)
    {
        this.overrides = overrides is null ? [] : new Dictionary<string, KeyCombo?>(overrides);
    }

    /// <summary>Command id → its shortcut (null: no shortcut).</summary>
    public IReadOnlyDictionary<string, KeyCombo?> Overrides => overrides;

    public ShortcutBindings Copy() => new(overrides);

    /// <summary>Every shortcut in effect for <paramref name="command"/>, the main one first.</summary>
    public IReadOnlyList<KeyCombo> Combos(ShortcutCommand command)
    {
        if (overrides.TryGetValue(command.Id, out var combo)) return combo is { } value ? [value] : [];
        return command.Defaults;
    }

    /// <summary>The main shortcut in effect (what menus and the palette show).</summary>
    public KeyCombo? Combo(ShortcutCommand command)
    {
        var combos = Combos(command);
        return combos.Count > 0 ? combos[0] : null;
    }

    public bool IsCustomised(ShortcutCommand command) => overrides.ContainsKey(command.Id);

    /// <summary>Commands other than <paramref name="commandId"/> that currently use <paramref name="combo"/>.</summary>
    public IReadOnlyList<ShortcutCommand> Conflicts(KeyCombo combo, string commandId, IEnumerable<ShortcutCommand> commands) =>
        commands.Where(command => command.Id != commandId && Combos(command).Contains(combo)).ToList();

    /// <summary>Why <paramref name="combo"/> cannot simply be given to <paramref name="commandId"/>, or null if it can.</summary>
    public ShortcutProblem? Problem(KeyCombo combo, string commandId, IEnumerable<ShortcutCommand> commands)
    {
        if (!combo.HasRequiredModifier) return new ShortcutProblem.NeedsModifier();
        if (combo.ReservedReason is { } reason) return new ShortcutProblem.Reserved(reason);
        var clashing = Conflicts(combo, commandId, commands);
        return clashing.Count == 0 ? null : new ShortcutProblem.Conflict(clashing.Select(command => command.Id).ToList());
    }

    /// <summary>
    /// Gives <paramref name="combo"/> (null: none) to <paramref name="command"/>. Setting a
    /// command back to exactly its defaults removes the override rather than storing a copy.
    /// </summary>
    public void Set(KeyCombo? combo, ShortcutCommand command)
    {
        var isDefault = combo is { } value
            ? command.Defaults.Count == 1 && command.Defaults[0] == value
            : command.Defaults.Count == 0;
        if (isDefault) overrides.Remove(command.Id);
        else overrides[command.Id] = combo;
    }

    /// <summary>Gives <paramref name="combo"/> to <paramref name="command"/> and takes it from every other command using it.</summary>
    public void Assign(KeyCombo combo, ShortcutCommand command, IEnumerable<ShortcutCommand> commands)
    {
        foreach (var other in Conflicts(combo, command.Id, commands))
        {
            // What the other command keeps: the rest of its shortcuts, of which one can be stored.
            var remaining = Combos(other).Where(c => c != combo).ToList();
            Set(remaining.Count > 0 ? remaining[0] : null, other);
        }
        Set(combo, command);
    }

    public void Reset(ShortcutCommand command) => overrides.Remove(command.Id);

    public void Reset(string commandId) => overrides.Remove(commandId);

    public void ResetAll() => overrides.Clear();

    // ---- Stored form ----

    public string ToJson()
    {
        var map = new JsonObject();
        foreach (var (id, combo) in overrides.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var entry = new JsonObject();
            if (combo is { } value) entry["combo"] = value.StringValue;
            map[id] = entry;
        }
        return new JsonObject { ["overrides"] = map }.ToJsonString(ProductivityFiles.JsonOptions);
    }

    /// <summary>Reads <see cref="ToJson"/>'s form; entries it cannot read are skipped. Null when it is not that form at all.</summary>
    public static ShortcutBindings? FromJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["overrides"] is not JsonObject map) return null;
            var result = new Dictionary<string, KeyCombo?>();
            foreach (var (id, node) in map)
            {
                if (node is not JsonObject entry) continue;
                var text = entry["combo"] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
                if (text is null) result[id] = null;
                else if (KeyCombo.Parse(text) is { } combo) result[id] = combo;
            }
            return new ShortcutBindings(result);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool Equals(ShortcutBindings? other) =>
        other is not null && overrides.Count == other.overrides.Count
        && overrides.All(pair => other.overrides.TryGetValue(pair.Key, out var combo) && combo == pair.Value);

    public override bool Equals(object? obj) => Equals(obj as ShortcutBindings);

    public override int GetHashCode() => overrides.Count;
}
