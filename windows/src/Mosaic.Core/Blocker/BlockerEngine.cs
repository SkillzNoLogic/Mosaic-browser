using System.Diagnostics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace Mosaic.Core.Blocker;

/// <summary>The engine's decision for one request; the numbers are those of the bundle's <c>match</c>.</summary>
public enum BlockerVerdict
{
    /// <summary>No filter matched.</summary>
    None = 0,
    /// <summary>A blocking filter matched.</summary>
    Block = 1,
    /// <summary>An exception filter (<c>@@…</c>) matched; the request is explicitly allowed.</summary>
    Allow = 2,
}

/// <summary>Counts of the cosmetic filters compiled from the lists.</summary>
/// <param name="Generic">Generic hiding selectors (<c>##.ad</c>), applied on every page.</param>
/// <param name="Specific">Site-specific hiding filters and exceptions.</param>
/// <param name="Unsupported">Valid uBO syntax Mosaic does not apply: procedural filters, scriptlets,
/// HTML and response-header filters, regex/path-based site targets.</param>
public sealed record CosmeticStats(int Generic, int Specific, int Unsupported);

/// <summary>Cosmetic filtering for one hostname.</summary>
/// <param name="Css">CSS hiding what site-specific filters name, exceptions applied.</param>
/// <param name="GenericExceptions">Generic selectors this site excepts (<c>example.com#@#.ad</c>);
/// the generic stylesheet for this site must leave them out.</param>
public sealed record CosmeticRules(string Css, IReadOnlyList<string> GenericExceptions)
{
    public static CosmeticRules Empty { get; } = new("", []);

    public bool Equals(CosmeticRules? other) =>
        other is not null && Css == other.Css && GenericExceptions.SequenceEqual(other.GenericExceptions);

    public override int GetHashCode() => HashCode.Combine(Css, GenericExceptions.Count);
}

/// <summary>
/// The verdict on one line of the user's own filters. <c>Line</c> is the 1-based line number in the
/// text checked; <c>Message</c> says why it is an error or unsupported, or notes something about a valid filter.
/// </summary>
public sealed record FilterCheck(int Line, string Text, FilterCheckKind Kind, string Message)
{
    /// <summary>Whether the line takes effect (possibly with a note).</summary>
    public bool IsEffective => Kind is FilterCheckKind.Network or FilterCheckKind.Cosmetic;

    public static FilterCheckKind KindFromId(string? id) => id switch
    {
        "comment" => FilterCheckKind.Comment,
        "network" => FilterCheckKind.Network,
        "cosmetic" => FilterCheckKind.Cosmetic,
        "unsupported" => FilterCheckKind.Unsupported,
        _ => FilterCheckKind.Error,
    };

    /// <summary>macOS's raw value of <see cref="Kind"/>.</summary>
    public string KindId => Kind.ToString().ToLowerInvariant();
}

public enum FilterCheckKind
{
    Comment,
    Network,
    Cosmetic,
    /// <summary>Valid uBO syntax that Mosaic does not apply (scriptlets, procedural cosmetic filters, modifiers such as <c>$removeparam</c>).</summary>
    Unsupported,
    Error,
}

/// <summary>The engine could not be loaded. <see cref="Exception.Message"/> is user-presentable.</summary>
public sealed class BlockerEngineException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// uBlock Origin's network filtering engine, hosted in a V8 isolate of its own (ClearScript).
/// </summary>
/// <remarks>
/// <para>
/// Port of <c>Sources/MosaicCore/Blocker/BlockerEngine.swift</c>, with V8 in place of
/// JavaScriptCore. The engine is uBO's own code (<c>static-net-filtering.js</c> and its
/// dependencies), bundled from uBO's source by <c>scripts/bootstrap.ps1</c> together with
/// <c>resources/blocker/engine.js</c>, which exposes it as a global <c>MosaicBlocker</c>. This
/// class loads that bundle and marshals calls to it. (Mozilla tabs load the same bundle inside
/// Gecko's own process; see <c>agent.sys.mjs</c>.)
/// </para>
/// <para>
/// Threading: a V8 isolate must not be entered from two threads at once, and WebView2 asks from
/// the UI thread while a replacement engine may be compiling on another. So every call into the
/// isolate holds <see cref="gate"/>, and <see cref="Verdict"/> may be called from any thread,
/// including while (or after) the engine is disposed, when it answers <see cref="BlockerVerdict.None"/>.
/// </para>
/// </remarks>
public sealed class BlockerEngine : IDisposable
{
    private readonly Lock gate = new();
    private readonly V8ScriptEngine script;
    /// <summary>The bundle's <c>MosaicBlocker</c>.</summary>
    private readonly ScriptObject blocker;
    private bool isDisposed;

    /// <summary>
    /// Loads the engine and compiles <paramref name="lists"/> into it. Takes on the order of a
    /// second for uBO's default lists; call it off the UI thread.
    /// </summary>
    /// <param name="bundlePath"><c>ublock-engine.js</c>.</param>
    /// <param name="suffixList">Text of the Public Suffix List, which the engine needs to tell
    /// first-party requests from third-party ones.</param>
    /// <param name="lists">The filter lists to compile.</param>
    /// <exception cref="BlockerEngineException">The bundle cannot be read or fails to load.</exception>
    public BlockerEngine(string bundlePath, string suffixList, IEnumerable<FilterListText> lists)
    {
        string source;
        try
        {
            source = File.ReadAllText(bundlePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new BlockerEngineException($"The content blocker’s engine could not be read from {bundlePath}.", error);
        }

        script = new V8ScriptEngine("Mosaic content blocker", V8ScriptEngineFlags.None);
        try
        {
            // polyfills.js routes the bundle's console output here when the host has no console of
            // its own; a bare V8 has none. As on macOS only warnings and errors are kept.
            script.AddHostObject("__mosaicHost", HostItemFlags.None, new ConsoleSink());
            script.Execute("globalThis.__mosaicLog = (level, text) => __mosaicHost.Log(String(level), String(text));");

            script.Execute(new DocumentInfo(Path.GetFileName(bundlePath)), source);
            if (script.Global.GetProperty("MosaicBlocker") is not ScriptObject entryPoint
                || entryPoint.GetProperty("match") is not ScriptObject)
            {
                throw new BlockerEngineException("The content blocker’s engine reported an error: the engine bundle did not define MosaicBlocker");
            }
            blocker = entryPoint;
            Version = blocker.GetProperty("version") as string ?? "?";

            blocker.InvokeMethod("init", suffixList);
            // List by list, so no list has to be turned into a script array first.
            blocker.InvokeMethod("beginLists");
            foreach (var list in lists) blocker.InvokeMethod("addList", list.Name, list.Text);
            FilterCount = Convert.ToInt32(blocker.InvokeMethod("commitLists"), System.Globalization.CultureInfo.InvariantCulture);
            // Bundles built before cosmetic filtering have no cosmeticStats; they simply hide nothing.
            if (blocker.GetProperty("cosmeticStats") is ScriptObject && blocker.InvokeMethod("cosmeticStats") is ScriptObject stats)
            {
                CosmeticStats = new CosmeticStats(Int(stats.GetProperty("generic")), Int(stats.GetProperty("specific")), Int(stats.GetProperty("unsupported")));
                stats.Dispose();
                SupportsCosmetics = true;
            }
            SupportsValidation = blocker.GetProperty("validateFilters") is ScriptObject;
            SupportsDetails = blocker.GetProperty("matchDetails") is ScriptObject;
        }
        catch (ScriptEngineException error)
        {
            script.Dispose();
            throw new BlockerEngineException($"The content blocker’s engine reported an error: {error.Message}", error);
        }
        catch
        {
            script.Dispose();
            throw;
        }
    }

    /// <summary>Version of uBlock Origin the engine was built from.</summary>
    public string Version { get; }

    /// <summary>Number of network filters in effect.</summary>
    public int FilterCount { get; }

    /// <summary>Cosmetic filters in effect.</summary>
    public CosmeticStats CosmeticStats { get; } = new(0, 0, 0);

    /// <summary>Whether the bundle has cosmetic filtering (bundles built from an older <c>engine.js</c> do not).</summary>
    public bool SupportsCosmetics { get; }

    /// <summary>Whether <see cref="Validate"/> can check filters with this bundle.</summary>
    public bool SupportsValidation { get; }

    /// <summary>Whether <see cref="Details"/> can name filters with this bundle.</summary>
    public bool SupportsDetails { get; }

    /// <summary>The site-specific cosmetic rules for <paramref name="hostname"/>. Cheap (well under a millisecond); callers still cache per hostname.</summary>
    public CosmeticRules CosmeticRulesFor(string hostname)
    {
        lock (gate)
        {
            if (isDisposed || !SupportsCosmetics) return CosmeticRules.Empty;
            try
            {
                if (blocker.InvokeMethod("cosmeticFor", hostname) is not ScriptObject result) return CosmeticRules.Empty;
                using (result)
                {
                    var css = result.GetProperty("css") as string ?? "";
                    List<string> exceptions = [];
                    if (result.GetProperty("exceptions") is ScriptObject array)
                    {
                        using (array)
                        {
                            var length = Int(array.GetProperty("length"));
                            for (var i = 0; i < length; i++)
                            {
                                if (array.GetProperty(i) is string selector) exceptions.Add(selector);
                            }
                        }
                    }
                    return new CosmeticRules(css, exceptions);
                }
            }
            catch (ScriptEngineException error)
            {
                Trace.WriteLine($"[Blocker] cosmeticFor failed for {hostname}: {error.Message}");
                return CosmeticRules.Empty;
            }
        }
    }

    /// <summary>
    /// Every generic hiding selector as a stylesheet (one rule per selector), leaving out
    /// <paramref name="excluded"/>. The same for every page apart from exclusions.
    /// </summary>
    public string GenericCosmeticCss(IReadOnlyList<string>? excluded = null)
    {
        lock (gate)
        {
            if (isDisposed || !SupportsCosmetics) return "";
            try
            {
                var list = script.Evaluate("[]") as ScriptObject;
                using (list)
                {
                    foreach (var selector in excluded ?? []) list!.InvokeMethod("push", selector);
                    return blocker.InvokeMethod("cosmeticGeneric", list) as string ?? "";
                }
            }
            catch (ScriptEngineException error)
            {
                Trace.WriteLine($"[Blocker] cosmeticGeneric failed: {error.Message}");
                return "";
            }
        }
    }

    /// <summary>
    /// The decision for one request together with the filter that made it, as uBO's logger writes
    /// it (empty when no filter matched). Costs one extra match; for the dashboard, not the request path.
    /// </summary>
    public (BlockerVerdict Verdict, string Filter) Details(string url, string documentUrl, string type)
    {
        lock (gate)
        {
            if (isDisposed || !SupportsDetails) return (BlockerVerdict.None, "");
            try
            {
                if (blocker.InvokeMethod("matchDetails", url, documentUrl, type) is not ScriptObject result) return (BlockerVerdict.None, "");
                using (result)
                {
                    var verdict = Int(result.GetProperty("result")) switch
                    {
                        1 => BlockerVerdict.Block,
                        2 => BlockerVerdict.Allow,
                        _ => BlockerVerdict.None,
                    };
                    return (verdict, result.GetProperty("filter") as string ?? "");
                }
            }
            catch (ScriptEngineException error)
            {
                Trace.WriteLine($"[Blocker] matchDetails failed for {url}: {error.Message}");
                return (BlockerVerdict.None, "");
            }
        }
    }

    /// <summary>Checks filters the user typed (one per line) with uBO's parser. Empty when the bundle cannot.</summary>
    public IReadOnlyList<FilterCheck> Validate(string filters)
    {
        lock (gate)
        {
            if (isDisposed || !SupportsValidation) return [];
            try
            {
                if (blocker.InvokeMethod("validateFilters", filters) is not ScriptObject array) return [];
                using (array)
                {
                    List<FilterCheck> checks = [];
                    var length = Int(array.GetProperty("length"));
                    for (var i = 0; i < length; i++)
                    {
                        if (array.GetProperty(i) is not ScriptObject record) continue;
                        using (record)
                        {
                            checks.Add(new FilterCheck(
                                Int(record.GetProperty("line")),
                                record.GetProperty("text") as string ?? "",
                                FilterCheck.KindFromId(record.GetProperty("kind") as string),
                                record.GetProperty("message") as string ?? ""));
                        }
                    }
                    return checks;
                }
            }
            catch (ScriptEngineException error)
            {
                Trace.WriteLine($"[Blocker] validateFilters failed: {error.Message}");
                return [];
            }
        }
    }

    private static int Int(object? value) => value switch
    {
        int number => number,
        double number => (int)number,
        long number => (int)number,
        _ => 0,
    };

    /// <summary>The engine's decision for one request.</summary>
    /// <param name="url">The request URL.</param>
    /// <param name="documentUrl">URL of the page (top-level document) making the request.</param>
    /// <param name="type">A webRequest resource type (<c>script</c>, <c>image</c>, <c>xmlhttprequest</c>, …).</param>
    public BlockerVerdict Verdict(string url, string documentUrl, string type)
    {
        object result;
        lock (gate)
        {
            if (isDisposed) return BlockerVerdict.None;
            try
            {
                // As a method: ClearScript's InvokeAsFunction takes ten times as long per call
                // (about 45 µs against 4 µs with the full lists, on an Apple Silicon Mac).
                result = blocker.InvokeMethod("match", url, documentUrl, type);
            }
            catch (ScriptEngineException error)
            {
                // A script error is a wrong answer for one request, not a reason to stop; it is
                // reported and that request is let through, as on macOS.
                Trace.WriteLine($"[Blocker] match failed for {url}: {error.Message}");
                return BlockerVerdict.None;
            }
        }
        return result switch
        {
            int number when Enum.IsDefined((BlockerVerdict)number) => (BlockerVerdict)number,
            double number when number is 1 or 2 => (BlockerVerdict)(int)number,
            _ => BlockerVerdict.None,
        };
    }

    /// <summary>Releases the isolate. Safe while another thread is asking for a verdict.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (isDisposed) return;
            isDisposed = true;
            blocker.Dispose();
            script.Dispose();
        }
    }

    /// <summary>Receives the bundle's console output (see the constructor).</summary>
    private sealed class ConsoleSink
    {
        // Called from script, by name.
        public void Log(string level, string text)
        {
            if (level is "error" or "warn") Trace.WriteLine($"[Blocker] {level}: {text}");
        }
    }
}
