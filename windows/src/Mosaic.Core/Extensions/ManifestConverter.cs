using System.Text.Json.Nodes;

namespace Mosaic.Core.Extensions;

public enum NoteSeverity
{
    /// <summary>Adapted automatically; nothing to do.</summary>
    Info,
    /// <summary>A feature of the extension will be missing or behave differently.</summary>
    Warning,
    /// <summary>The extension depends on something Gecko does not have; expect breakage.</summary>
    Blocker,
}

/// <summary>Something the user should know about how a Chrome extension will behave on Gecko.</summary>
public sealed record CompatibilityNote(NoteSeverity Severity, string Message);

public sealed record ManifestConversion(JsonObject Manifest, IReadOnlyList<CompatibilityNote> Notes);

/// <summary>Rewrites a Chrome <c>manifest.json</c> into one Gecko will load.</summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Extensions/ManifestConverter.swift</c>, including the shim
/// script, verbatim. Behaviour is pinned down by <c>ManifestConverterTests.swift</c>.
/// </remarks>
public static class ManifestConverter
{
    /// <summary>File name of the compatibility shim injected ahead of background scripts.</summary>
    public const string ShimFileName = "__mosaic_chrome_shim.js";

    /// <summary>Gecko add-on ID used for a converted Chrome extension: <c>&lt;id&gt;@crx.mosaic</c>.</summary>
    public static string GeckoIdFor(string chromeId) => $"{chromeId}@crx.mosaic";

    /// <param name="manifest">The parsed Chrome manifest. Not modified.</param>
    /// <param name="chromeId">The extension's Chrome ID.</param>
    /// <param name="readFile">Returns the text of a file inside the extension (path relative to its
    /// root, forward slashes), or null; used to resolve <c>importScripts()</c> in service workers.</param>
    public static ManifestConversion Convert(JsonObject manifest, string chromeId, Func<string, string?>? readFile = null) =>
        throw NotPorted.Yet();

    /// <summary>Scans script text for Chrome-only APIs.</summary>
    public static IReadOnlyList<CompatibilityNote> ApiNotes(string script) => throw NotPorted.Yet();

    /// <summary>Paths imported by <c>importScripts(...)</c> calls in a worker, relative to the extension root.</summary>
    public static IReadOnlyList<string> ImportedScripts(string source, string workerPath) => throw NotPorted.Yet();

    /// <summary>Collapses <c>.</c> and <c>..</c>; null for a path that would leave the extension.</summary>
    public static string? Normalize(string path) => throw NotPorted.Yet();

    /// <summary>The shim script's source.</summary>
    public static string ShimSource => throw NotPorted.Yet();
}
