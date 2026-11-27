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
