using System.Text.Json.Nodes;

namespace Mosaic.Core.Browser;

/// <summary>
/// Where feature areas plug their automation commands and <c>dump</c> fields in, without
/// editing <see cref="Automation"/>. Register once at start-up (from the area's
/// <c>BrowserModel.&lt;Area&gt;.cs</c> initialiser or the app's feature wiring).
/// </summary>
/// <remarks>
/// Command names follow <c>Sources/Mosaic/App/Automation.swift</c> and the macOS areas'
/// <c>*Automation.swift</c> files, so the same scripts' vocabulary works on both platforms.
/// </remarks>
public static class AutomationRegistry
{
    /// <summary>
    /// A command handler: returns null when <c>command</c> is not one of its commands,
    /// otherwise the reply (use <see cref="Ok"/> / <see cref="Error"/>).
    /// Arguments: model, command, value, out path (may be null).
    /// </summary>
    public delegate Task<AutomationReply?> Handler(BrowserModel model, string command, string value, string? output);

    private static readonly List<Handler> handlers = [];
    private static readonly List<(string Key, Func<BrowserModel, JsonNode?> State)> states = [];

    public static IReadOnlyList<Handler> Handlers => handlers;

    public static void Register(Handler handler)
    {
        lock (handlers) handlers.Add(handler);
    }

    /// <summary>Adds a top-level <c>dump</c> key (an area's own key, e.g. "tabs2", "library").</summary>
    public static void RegisterState(string key, Func<BrowserModel, JsonNode?> state)
    {
        lock (states)
        {
            states.RemoveAll(s => s.Key == key);
            states.Add((key, state));
        }
    }

    public static AutomationReply Ok() => new("{\"ok\":true}");

    public static AutomationReply Error(string message) =>
        new(new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString());

    internal static JsonObject WithFeatureState(this JsonObject state, BrowserModel model)
    {
        lock (states)
        {
            foreach (var (key, provider) in states)
            {
                if (state.ContainsKey(key)) continue;  // built-in keys are part of the contract
                try { state[key] = provider(model); }
                catch (Exception error) { state[key] = new JsonObject { ["error"] = error.Message }; }
            }
        }
        return state;
    }
}
