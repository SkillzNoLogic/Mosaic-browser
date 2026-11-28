namespace Mosaic.Core.WindowsIntegration;

/// <summary>
/// Turns something dropped on Mosaic's window (files, a link, text) into the pages to open, one
/// tab each. Pages themselves handle drops inside the web content; this is for Mosaic's own UI
/// (tab strip, toolbar, start page, empty areas).
/// </summary>
public static class DropTargets
{
    /// <summary>More than this many items at once is almost certainly a mistake; the rest are ignored.</summary>
    public const int MaxTabs = 20;

    /// <summary>Search text longer than this is cut (a dropped paragraph becomes a search for its start).</summary>
    public const int MaxSearchLength = 512;

    /// <param name="files">Full paths of dropped files and folders (Explorer drags).</param>
    /// <param name="link">A dropped link (a WebLink/URI from another browser or app).</param>
    /// <param name="text">Dropped plain text.</param>
    public static IReadOnlyList<string> From(IEnumerable<string>? files, string? link, string? text)
    {
        var targets = new List<string>();
        foreach (var file in files ?? [])
        {
            if (targets.Count == MaxTabs) break;
            if (WindowsPath.IsRooted(file) && WindowsPath.Combine(@"C:\", file) is { } full) targets.Add(WindowsPath.ToFileUri(full));
        }
        if (targets.Count > 0) return targets;

        if (!string.IsNullOrWhiteSpace(link)
            && ActivationRequest.Classify(link, @"C:\", _ => false) is (true, { } url) && url.Contains(':'))
            return [url];

        if (string.IsNullOrWhiteSpace(text)) return [];
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Several lines that are all links: one tab each (a dragged list of URLs).
        if (lines.Length > 1 && lines.All(IsLink))
            return [.. lines.Take(MaxTabs).Select(line => ActivationRequest.Classify(line, @"C:\", _ => false).Target!)];
        if (lines.Length == 1 && IsLink(lines[0])) return [ActivationRequest.Classify(lines[0], @"C:\", _ => false).Target!];

        // Anything else is searched for (or opened, if it is an address like "example.com").
        var words = string.Join(' ', lines);
        if (words.Length > MaxSearchLength) words = words[..MaxSearchLength];
        return ActivationRequest.Classify(words, @"C:\", _ => false) is (true, { } target) ? [target] : [];
    }

    private static bool IsLink(string line) =>
        !line.Contains(' ') && ActivationRequest.Classify(line, @"C:\", _ => false) is (true, { } target)
        && target.Contains("://", StringComparison.Ordinal);
}
