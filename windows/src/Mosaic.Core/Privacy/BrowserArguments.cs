namespace Mosaic.Core.Privacy;

/// <summary>Building WebView2's <c>AdditionalBrowserArguments</c> from several areas' contributions.</summary>
public static class BrowserArguments
{
    /// <summary>
    /// <paramref name="arguments"/> with <paramref name="features"/> added to its
    /// <c>--enable-features</c> list. Chromium honours only the last <c>--enable-features</c>
    /// switch, so every <c>--enable-features</c> already in <paramref name="arguments"/> is folded
    /// into one, at the end. A feature already listed (by name, before any ':' parameters) is
    /// replaced by the new entry.
    /// </summary>
    public static string Merge(string? arguments, IEnumerable<string> features)
    {
        const string Switch = "--enable-features=";
        List<string> others = [];
        List<string> enabled = [];
        foreach (var part in (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(Switch, StringComparison.Ordinal))
            {
                enabled.AddRange(part[Switch.Length..].Trim('"').Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
            else
            {
                others.Add(part);
            }
        }
        static string Name(string feature) => feature.Split(':', 2)[0].Split('<', 2)[0];
        foreach (var feature in features.Where(feature => feature.Length > 0))
        {
            enabled.RemoveAll(existing => Name(existing) == Name(feature));
            enabled.Add(feature);
        }
        if (enabled.Count > 0) others.Add(Switch + string.Join(',', enabled));
        return string.Join(' ', others);
    }
}
