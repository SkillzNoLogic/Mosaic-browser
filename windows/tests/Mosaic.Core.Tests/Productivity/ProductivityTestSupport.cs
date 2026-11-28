using System.Runtime.CompilerServices;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Productivity;

/// <summary>Tests that change the productivity area's static hooks run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProductivityStatics
{
    public const string Name = "Productivity statics";
}

internal static class ProductivityTestSupport
{
    /// <summary>The repository's <c>Resources/reader</c> (Readability 0.6.0 and Mosaic's reader page).</summary>
    public static string ReaderResources { get; } = FindReaderResources();

    private static string FindReaderResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Resources", "reader");
            if (File.Exists(Path.Combine(candidate, "Readability.js"))) return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Resources/reader not found above the test binaries.");
    }

    /// <summary>A model on fakes whose productivity files live in the fixture's folder.</summary>
    public static ProductivityFixture Create(Action<BrowserFixture>? configure = null, string? readerResources = null)
    {
        PageScripting.Resolver = FakePageScriptHost.For;
        var fixture = new BrowserFixture(configure);
        fixture.Model.UseProductivityOptions(new ProductivityOptions
        {
            Directory = fixture.PathFor("productivity"),
            ReaderResources = readerResources ?? ReaderResources,
        });
        return new ProductivityFixture(fixture);
    }

    /// <summary>Lets background work (file reads on the thread pool) finish and its continuations run on the loop.</summary>
    public static async Task Pump()
    {
        for (var round = 0; round < 5; round++)
        {
            await Task.Delay(20);
            await TestLoop.Settle();
        }
    }

    /// <summary>Pumps until <paramref name="condition"/> holds (at most 5 s).</summary>
    public static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never became true.");
            await Task.Delay(10);
            await TestLoop.Settle();
        }
    }

    public static KeyCombo Key(string text) => KeyCombo.Parse(text) ?? throw new ArgumentException(text);
}

/// <summary>A <see cref="BrowserFixture"/> that waits for the productivity area's background writes before deleting its folder.</summary>
internal sealed class ProductivityFixture(BrowserFixture inner) : IDisposable
{
    public BrowserFixture Inner => inner;
    public BrowserModel Model => inner.Model;
    public FakeGeckoEngine Gecko => inner.Gecko;
    public ManualTimeProvider Time => inner.Time;
    public int AddressFocusRequests => inner.AddressFocusRequests;
    public int WindowActivationRequests => inner.WindowActivationRequests;
    public string PathFor(string name) => inner.PathFor(name);
    public BrowserTab OpenGeckoTab(string url) => inner.OpenGeckoTab(url);
    public (BrowserTab Tab, FakeChromiumBrowser Browser) OpenChromiumTab(string url) => inner.OpenChromiumTab(url);

    public void Dispose()
    {
        Model.Productivity.Files.Flush().Wait(TimeSpan.FromSeconds(5));
        inner.Dispose();
    }
}

/// <summary>The page-script side of a fake Chromium browser: records scripts, answers as told.</summary>
internal sealed class FakePageScriptHost : IPageScriptHost
{
    private static readonly ConditionalWeakTable<IChromiumBrowser, FakePageScriptHost> hosts = [];

    public static FakePageScriptHost For(IChromiumBrowser browser) => hosts.GetValue(browser, _ => new FakePageScriptHost());

    public bool CanRunScripts { get; set; } = true;

    public List<string> Scripts { get; } = [];

    /// <summary>Answer for each script; by default the probe says "article" and entering works.</summary>
    public Func<string, string?> Respond { get; set; } = script =>
        script.Contains("isProbablyReaderable(document)", StringComparison.Ordinal) && !script.Contains("__mosaicReader.enter", StringComparison.Ordinal)
            ? "true"
            : script.Contains("__mosaicReader.enter", StringComparison.Ordinal) ? """{"ok":true,"title":"Article"}""" : "null";

    public event Action<string>? MessageReceived;

    public int ListenerCount => MessageReceived?.GetInvocationList().Length ?? 0;

    public Task<string?> ExecuteScriptAsync(string script)
    {
        Scripts.Add(script);
        return Task.FromResult(Respond(script));
    }

    public void Post(string message) => MessageReceived?.Invoke(message);
}
