using System.Text.Json.Nodes;

namespace Mosaic.Core.WindowsIntegration;

/// <summary>
/// The system's appearance settings that Mosaic follows: app mode (light/dark), high contrast,
/// accent colours, transparency and animation effects, text scaling.
/// </summary>
/// <param name="Accent">Accent colours as <c>#RRGGBB</c>: "accent", "light1".."light3", "dark1".."dark3".</param>
/// <param name="HighContrastScheme">The active high-contrast theme's name ("High Contrast Black", "Night sky", …), or null.</param>
public sealed record SystemAppearance(
    bool Dark,
    bool HighContrast,
    string? HighContrastScheme,
    IReadOnlyDictionary<string, string> Accent,
    bool TransparencyEnabled,
    bool AnimationsEnabled,
    double TextScale)
{
    public static readonly SystemAppearance Default = new(
        false, false, null, new Dictionary<string, string> { ["accent"] = "#0078D4" }, true, true, 1);

    public string AccentColor => Accent.TryGetValue("accent", out var color) ? color : "#0078D4";

    public JsonObject ToJson()
    {
        var accent = new JsonObject();
        foreach (var (name, color) in Accent) accent[name] = color;
        return new JsonObject
        {
            ["dark"] = Dark,
            ["highContrast"] = HighContrast,
            ["highContrastScheme"] = HighContrastScheme,
            ["accent"] = accent,
            ["transparency"] = TransparencyEnabled,
            ["animations"] = AnimationsEnabled,
            ["textScale"] = TextScale,
        };
    }

    /// <summary>"#RRGGBB" from colour components.</summary>
    public static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}

/// <summary>
/// The current <see cref="SystemAppearance"/> and its changes, for any area (Core or app):
/// <code>
/// SystemAppearanceMonitor.Changed += appearance =&gt; ApplyAccent(appearance.AccentColor);
/// </code>
/// The app's listener (UISettings / AccessibilitySettings) starts on first subscription or
/// first read of <see cref="Current"/> and raises <see cref="Changed"/> on the UI thread, only
/// when something actually changed.
/// </summary>
public static class SystemAppearanceMonitor
{
    private static readonly object gate = new();
    private static SystemAppearance current = SystemAppearance.Default;
    private static Action<SystemAppearance>? changed;
    private static Action? starter;
    private static bool started;

    /// <summary>Set by the app: starts listening and calls <see cref="Update"/> with the first reading.</summary>
    public static Action? Starter
    {
        set { lock (gate) { starter = value; started = false; } }
    }

    public static SystemAppearance Current
    {
        get
        {
            EnsureStarted();
            lock (gate) return current;
        }
    }

    public static event Action<SystemAppearance> Changed
    {
        add
        {
            lock (gate) changed += value;
            EnsureStarted();
        }
        remove { lock (gate) changed -= value; }
    }

    /// <summary>Raised count, for <c>dump</c>.</summary>
    public static int ChangeCount { get; private set; }

    /// <summary>Called by the app's listener; raises <see cref="Changed"/> when the value differs.</summary>
    public static void Update(SystemAppearance appearance)
    {
        Action<SystemAppearance>? handlers;
        lock (gate)
        {
            if (Same(current, appearance)) return;
            current = appearance;
            ChangeCount++;
            handlers = changed;
        }
        handlers?.Invoke(appearance);
    }

    private static bool Same(SystemAppearance a, SystemAppearance b) =>
        a.Dark == b.Dark && a.HighContrast == b.HighContrast && a.HighContrastScheme == b.HighContrastScheme
        && a.TransparencyEnabled == b.TransparencyEnabled && a.AnimationsEnabled == b.AnimationsEnabled
        && a.TextScale.Equals(b.TextScale)
        && a.Accent.Count == b.Accent.Count && a.Accent.All(kv => b.Accent.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static void EnsureStarted()
    {
        Action? start;
        lock (gate)
        {
            if (started || starter is null) return;
            started = true;
            start = starter;
        }
        start();
    }

    internal static void Reset()
    {
        lock (gate)
        {
            current = SystemAppearance.Default;
            changed = null;
            starter = null;
            started = false;
            ChangeCount = 0;
        }
    }
}
