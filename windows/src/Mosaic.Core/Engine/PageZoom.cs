using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Permissions;

namespace Mosaic.Core.Engine;

/// <summary>
/// Page zoom shared by both engines: the steps Ctrl+ / Ctrl- move through, conversion to
/// Chromium's zoom levels, and the site a zoom choice is remembered for. Port of
/// <c>Sources/MosaicCore/Engine/PageZoom.swift</c>.
/// </summary>
public static class PageZoom
{
    /// <summary>
    /// Zoom factors Ctrl+ and Ctrl- step through: Chrome's familiar steps, limited to the range
    /// both engines support (Gecko's minimum is 30 %).
    /// </summary>
    public static IReadOnlyList<double> Steps { get; } =
        [0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    public const double Minimum = 0.33;
    public const double Maximum = 5.0;

    /// <summary>The next step above <paramref name="factor"/> (or the largest step).</summary>
    public static double ZoomedIn(double factor) => Steps.FirstOrDefault(step => step > factor + 0.001, Maximum);

    /// <summary>The next step below <paramref name="factor"/> (or the smallest step).</summary>
    public static double ZoomedOut(double factor) => Steps.LastOrDefault(step => step < factor - 0.001, Minimum);

    public static double Clamped(double factor) =>
        !double.IsFinite(factor) || factor <= 0 ? 1 : Math.Min(Math.Max(factor, Minimum), Maximum);

    /// <summary>True when <paramref name="factor"/> is the default size, within rounding.</summary>
    public static bool IsDefault(double factor) => Math.Abs(factor - 1) < 0.005;

    /// <summary>Chromium expresses zoom as a level: each level is a factor of 1.2, 0 is 100 %.</summary>
    public static double ChromiumLevel(double factor) => Math.Log(Clamped(factor)) / Math.Log(1.2);

    public static double FactorForChromiumLevel(double level) => Math.Pow(1.2, level);

    /// <summary>"110 %" style label.</summary>
    public static string PercentLabel(double factor) =>
        ((int)Math.Round(factor * 100, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + " %";

    /// <summary>
    /// The key a zoom choice is remembered under: the page's host without <c>www.</c>, so a site
    /// keeps its zoom across its pages, schemes and both engines. Null for pages that are not on
    /// the web.
    /// </summary>
    public static string? Site(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme is not ("http" or "https")) return null;
        var host = parsed.IdnHost.ToLowerInvariant();
        if (host.Length == 0) return null;
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}

/// <summary>
/// Zoom factors remembered per site (see <see cref="PageZoom.Site"/>), persisted as JSON
/// (<c>SiteZoom.json</c>, the macOS format: <c>{"example.com": 1.25}</c>). Sites at 100 % are not
/// stored. UI-thread use; the file is written in the background.
/// </summary>
public sealed class SiteZoomStore
{
    private readonly Dictionary<string, double> factors = new(StringComparer.Ordinal);
    private readonly BackgroundFile file;

    public SiteZoomStore(string filePath)
    {
        FilePath = filePath;
        file = new BackgroundFile(filePath);
        try
        {
            if (File.Exists(filePath) && JsonNode.Parse(File.ReadAllText(filePath)) is JsonObject stored)
            {
                foreach (var (site, value) in stored)
                {
                    if (value is JsonValue number && number.TryGetValue(out double factor) && double.IsFinite(factor) && factor > 0)
                    {
                        factors[site] = factor;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file is a fresh start.
        }
    }

    public string FilePath { get; }

    /// <summary>The remembered factor for the site of <paramref name="url"/>; 1 when none is remembered.</summary>
    public double Factor(string? url) => PageZoom.Site(url) is { } site && factors.TryGetValue(site, out var factor) ? factor : 1;

    /// <summary>
    /// Remembers <paramref name="factor"/> for the site of <paramref name="url"/> (100 % forgets it).
    /// Returns false for pages that are not on the web, whose zoom is not remembered.
    /// </summary>
    public bool SetFactor(double factor, string? url)
    {
        if (PageZoom.Site(url) is not { } site) return false;
        var value = PageZoom.Clamped(factor);
        double? newValue = PageZoom.IsDefault(value) ? null : value;
        double? current = factors.TryGetValue(site, out var old) ? old : null;
        if (current == newValue) return true;
        if (newValue is { } stored) factors[site] = stored;
        else factors.Remove(site);
        Save();
        return true;
    }

    /// <summary>Forgets the zoom of one site (a host as <see cref="Sites"/> lists it).</summary>
    public void Forget(string site)
    {
        if (factors.Remove(site)) Save();
    }

    /// <summary>Every site with a remembered zoom, sorted.</summary>
    public IReadOnlyList<(string Site, double Factor)> Sites =>
        [.. factors.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value))];

    public Task FlushAsync() => file.FlushAsync();

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private void Save()
    {
        var json = new JsonObject();
        foreach (var (site, factor) in Sites) json[site] = factor;
        file.Write(json.ToJsonString(Indented));
    }
}

/// <summary>What the find bar says about the matches of the current search.</summary>
/// <param name="Current">1-based position of the highlighted match; 0 when unknown or none.</param>
/// <param name="Total">Number of matches.</param>
/// <param name="Limit">When above 0, the engine stopped counting there.</param>
public readonly record struct FindStatus(int Current, int Total, int Limit = 0)
{
    public bool HasMatches => Total > 0;

    private bool Capped => Limit > 0 && Total >= Limit;

    /// <summary>"3 of 12", "1 of 1000+", or "No matches".</summary>
    public string Label
    {
        get
        {
            if (Total <= 0) return "No matches";
            var count = Capped ? $"{Limit}+" : Total.ToString(CultureInfo.InvariantCulture);
            if (Current > 0) return $"{Current} of {count}";
            return Capped ? $"More than {Limit} matches" : $"{count} matches";
        }
    }

    /// <summary>The same, spelled out for screen readers.</summary>
    public string AccessibleLabel
    {
        get
        {
            if (Total <= 0) return "No matches";
            if (Current > 0) return $"Match {Current} of {(Capped ? $"more than {Limit}" : Total.ToString(CultureInfo.InvariantCulture))}";
            return Label;
        }
    }
}
