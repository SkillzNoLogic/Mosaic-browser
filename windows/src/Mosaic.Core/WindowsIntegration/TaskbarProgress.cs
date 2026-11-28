using System.Text.Json.Nodes;

namespace Mosaic.Core.WindowsIntegration;

/// <summary>The taskbar button's progress states (ITaskbarList3's TBPF_* flags).</summary>
public enum TaskbarProgressState
{
    None,
    Indeterminate,
    Normal,
    Error,
    Paused,
}

/// <summary>
/// Progress on Mosaic's taskbar button, combined from several sources (downloads, the Gecko
/// runtime installer, updates). Each source sets its own state; the button shows:
/// an error if any source has one, otherwise determinate progress (the mean of the sources
/// that have a fraction; paused only when every such source is paused), otherwise
/// indeterminate if any source is, otherwise nothing.
/// <code>
/// TaskbarProgress.Set("downloads", TaskbarProgressState.Normal, received / (double)total);
/// TaskbarProgress.Clear("downloads");
/// </code>
/// The app applies <see cref="Current"/> to the window with ITaskbarList3 whenever
/// <see cref="Changed"/> fires (any thread; it marshals).
/// </summary>
public static class TaskbarProgress
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, (TaskbarProgressState State, double Fraction)> sources = new(StringComparer.Ordinal);

    public static event Action? Changed;

    public static void Set(string source, TaskbarProgressState state, double fraction = 0)
    {
        if (state == TaskbarProgressState.None)
        {
            Clear(source);
            return;
        }
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        bool changed;
        lock (gate)
        {
            changed = !sources.TryGetValue(source, out var old) || old != (state, fraction);
            sources[source] = (state, fraction);
        }
        if (changed) Changed?.Invoke();
    }

    public static void Clear(string source)
    {
        bool changed;
        lock (gate) changed = sources.Remove(source);
        if (changed) Changed?.Invoke();
    }

    public static (TaskbarProgressState State, double Value) Current
    {
        get
        {
            lock (gate) return Combine(sources.Values);
        }
    }

    internal static (TaskbarProgressState State, double Value) Combine(IEnumerable<(TaskbarProgressState State, double Fraction)> all)
    {
        var list = all.ToList();
        if (list.Count == 0) return (TaskbarProgressState.None, 0);
        var determinate = list.Where(s => s.State is TaskbarProgressState.Normal or TaskbarProgressState.Paused or TaskbarProgressState.Error).ToList();
        var mean = determinate.Count == 0 ? 0 : determinate.Average(s => s.Fraction);
        if (list.Any(s => s.State == TaskbarProgressState.Error)) return (TaskbarProgressState.Error, determinate.Count == 0 ? 1 : mean);
        if (determinate.Count > 0)
            return (determinate.All(s => s.State == TaskbarProgressState.Paused) ? TaskbarProgressState.Paused : TaskbarProgressState.Normal, mean);
        return (TaskbarProgressState.Indeterminate, 0);
    }

    public static JsonObject State()
    {
        lock (gate)
        {
            var (state, value) = Combine(sources.Values);
            var each = new JsonObject();
            foreach (var (name, (s, f)) in sources) each[name] = new JsonObject { ["state"] = s.ToString().ToLowerInvariant(), ["value"] = f };
            return new JsonObject
            {
                ["state"] = state.ToString().ToLowerInvariant(),
                ["value"] = Math.Round(value, 4),
                ["sources"] = each,
            };
        }
    }

    internal static void Reset()
    {
        lock (gate) sources.Clear();
    }

    public static TaskbarProgressState? ParseState(string text) => text.Trim().ToLowerInvariant() switch
    {
        "none" or "off" or "" => TaskbarProgressState.None,
        "indeterminate" => TaskbarProgressState.Indeterminate,
        "normal" => TaskbarProgressState.Normal,
        "error" => TaskbarProgressState.Error,
        "paused" => TaskbarProgressState.Paused,
        _ => null,
    };
}
