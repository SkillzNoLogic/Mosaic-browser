using Mosaic.Core.Blocker;
using Mosaic.Core.Browser;
using Mosaic.Core.Tabs;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Tabs;

internal sealed class FakePressure : IMemoryPressureSource
{
    public MemoryPressure Level { get; set; }

    public MemoryPressure Read() => Level;
}

/// <summary>A Chromium browser that can report audio and be muted (what WebView2 does in the app).</summary>
internal sealed class AudioChromiumBrowser(string url) : IChromiumBrowser, IChromiumAudio
{
    private bool playing;

    public bool IsShown { get; set; }
    public bool IsClosed { get; private set; }
    public bool IsMuted { get; set; }
    public List<string> Loads { get; } = [url];

    public bool IsPlayingAudio => playing;

    public event Action<string>? TitleChanged;
    public event Action<string>? UrlChanged;
    public event Action<ChromiumLoadState>? LoadStateChanged;
    public event Action<string>? NewTabRequested;
    public event Action<int>? BlockedCountChanged;
    public event Action<string>? Failed;
    public event Action? PlayingAudioChanged;

    public void Load(string address) => Loads.Add(address);
    public void GoBack() { }
    public void GoForward() { }
    public void Reload() { }
    public void Stop() { }
    public void Focus() { }
    public void Close() => IsClosed = true;

    public void Play(bool value)
    {
        playing = value;
        PlayingAudioChanged?.Invoke();
    }

    public void RaiseNewTab(string address) => NewTabRequested?.Invoke(address);

    // Unused by these tests; keeps the compiler quiet about unraised events.
    internal void Unused()
    {
        TitleChanged?.Invoke("");
        UrlChanged?.Invoke("");
        LoadStateChanged?.Invoke(default);
        BlockedCountChanged?.Invoke(0);
        Failed?.Invoke("");
    }
}

internal sealed class AudioChromiumEngine : IChromiumEngine
{
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public List<AudioChromiumBrowser> Created { get; } = [];

    public IChromiumBrowser CreateBrowser(string url)
    {
        var browser = new AudioChromiumBrowser(url);
        Created.Add(browser);
        return browser;
    }
}

/// <summary>
/// A browser model on fakes (<see cref="BrowserFixture"/>'s, or a Chromium engine with audio)
/// with its session and tab settings in a folder the test owns, so a second fixture on the same
/// folder is the next launch.
/// </summary>
internal sealed class TabsFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mosaic-tabs-" + Guid.NewGuid().ToString("N"));
    private readonly bool ownsSessionDirectory;

    public TabsFixture(string? sessionDirectory = null, bool audioChromium = false)
    {
        Directory.CreateDirectory(root);
        ownsSessionDirectory = sessionDirectory is null;
        SessionDirectory = sessionDirectory ?? Path.Combine(root, "profile");
        Store = new SessionStore(Path.Combine(SessionDirectory, "Session"));
        SettingsStore = new TabSettingsStore(Path.Combine(SessionDirectory, "tab-settings.json"));
        if (audioChromium)
        {
            AudioChromium = new AudioChromiumEngine();
            var settings = new BrowserSettings(Path.Combine(root, "settings.json"));
            var extensions = new ExtensionManager(Gecko, () => new FakeExtensionLibrary(), settings, new FakeChromiumExtensionHost(), new StubHttpHandler(), TestRules.Create());
            Model = new BrowserModel(Gecko, AudioChromium, extensions, settings, TestRules.Create(), Time, new ContentBlockerOptions
            {
                ResourcesDirectory = Path.Combine(root, "blocker"),
                ListsDirectory = Path.Combine(root, "lists"),
                HttpHandler = new StubHttpHandler { Respond = _ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)) },
            });
        }
        else
        {
            Browser = new BrowserFixture();
            Gecko = Browser.Gecko;
            Time = Browser.Time;
            Model = Browser.Model;
        }
    }

    public BrowserFixture? Browser { get; }
    public FakeGeckoEngine Gecko { get; } = new();
    public ManualTimeProvider Time { get; } = new();
    public AudioChromiumEngine? AudioChromium { get; }
    public FakeChromiumEngine Chromium => Browser!.Chromium;
    public BrowserModel Model { get; }
    public string SessionDirectory { get; }
    public SessionStore Store { get; }
    public TabSettingsStore SettingsStore { get; }
    public FakePressure Pressure { get; } = new();

    public Task StartAsync() => Model.StartTabsAsync(Store, SettingsStore, Pressure);

    public BrowserTab OpenGecko(string url, bool select = true)
    {
        var tab = Model.NewTab(url, EngineKind.Gecko, select);
        Assert.NotNull(tab.GeckoId);
        return tab;
    }

    public BrowserTab OpenChromium(string url, bool select = true) => Model.NewTab(url, EngineKind.Chromium, select);

    public void Dispose()
    {
        Browser?.Dispose();
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        _ = ownsSessionDirectory;
    }
}
