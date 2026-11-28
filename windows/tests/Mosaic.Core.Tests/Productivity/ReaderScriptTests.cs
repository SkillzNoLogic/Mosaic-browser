using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Mosaic.Core.Productivity;

namespace Mosaic.Core.Tests.Productivity;

/// <summary>The scripts injected into Chromium pages, checked in V8 (Chromium's own JavaScript engine).</summary>
public class ReaderScriptTests
{
    private static ReaderScripts Scripts => ReaderScripts.Load(ProductivityTestSupport.ReaderResources)
        ?? throw new InvalidOperationException("reader scripts missing");

    [Fact]
    public void ShippedFilesAreReadability060()
    {
        var version = File.ReadAllText(Path.Combine(ProductivityTestSupport.ReaderResources, "VERSION"));
        Assert.Contains("Mozilla Readability 0.6.0", version);
        Assert.True(File.Exists(Path.Combine(ProductivityTestSupport.ReaderResources, "LICENSE-Readability.txt")));
    }

    [Fact]
    public void ScriptsCompile()
    {
        using var engine = new V8ScriptEngine();
        engine.Compile(Scripts.Probe);
        engine.Compile(Scripts.Enter(new ReaderSettings()));
    }

    [Fact]
    public void EnteringDefinesTheReaderWithoutLeakingReadabilityAndReportsNoArticle()
    {
        using var engine = new V8ScriptEngine();
        // Just enough of a page for the reader's definition and Readability's refusal to run.
        engine.Execute("""
            var posted = [];
            var window = this;
            window.chrome = { webview: { postMessage: function (m) { posted.push(m); } } };
            var document = { cloneNode: function () { return { documentElement: null, getElementsByTagName: function () { return []; },
                querySelectorAll: function () { return []; } }; }, title: "Empty" };
            """);
        engine.Script.entered = engine.Evaluate(Scripts.Enter(new ReaderSettings()));
        var result = engine.Evaluate("JSON.stringify(entered)") as string;
        Assert.Contains("\"ok\":false", result);
        Assert.Equal("object", engine.Evaluate("typeof window.__mosaicReader"));
        Assert.Equal("undefined", engine.Evaluate("typeof Readability"));
        Assert.Equal("function", engine.Evaluate("typeof window.__mosaicReaderPost"));
        engine.Execute("window.__mosaicReaderPost('{\"type\":\"closed\"}')");
        Assert.Equal("__mosaicReader:{\"type\":\"closed\"}", engine.Evaluate("posted[0]"));
    }
}
