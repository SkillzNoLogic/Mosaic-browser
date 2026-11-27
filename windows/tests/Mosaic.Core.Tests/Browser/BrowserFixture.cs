using Mosaic.Core.Browser;

namespace Mosaic.Core.Tests.Browser;

/// <summary>A browser model wired to fakes, with its settings in a throwaway folder.</summary>
internal sealed class BrowserFixture : IDisposable
{
    private readonly string directory =
        Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));

    public FakeGeckoEngine Gecko { get; } = new();
    public FakeChromiumEngine Chromium { get; } = new();
    public FakeExtensionLibrary Library { get; } = new();
    public FakeChromiumExtensionHost ChromiumExtensions { get; } = new();
    public StubHttpHandler Http { get; } = new();
    public ManualTimeProvider Time { get; } = new();
    public BrowserRules Rules { get; } = TestRules.Create();

    public BrowserSettings Settings { get; }
    public ExtensionManager Extensions { get; }
    public BrowserModel Model { get; }

    public int AddressFocusRequests { get; private set; }
    public int WindowActivationRequests { get; private set; }
    public int GeckoFocusRequests { get; private set; }

    /// <param name="configure">Adjusts the fakes (or writes settings) before the model is created.</param>
    public BrowserFixture(Action<BrowserFixture>? configure = null)
    {
        Directory.CreateDirectory(directory);
        configure?.Invoke(this);
        Settings = new BrowserSettings(SettingsPath);
        Extensions = new ExtensionManager(Gecko, () => Library, Settings, ChromiumExtensions, Http, Rules);
        Model = new BrowserModel(Gecko, Chromium, Extensions, Settings, Rules, Time);
        Model.AddressFocusRequested += () => AddressFocusRequests++;
        Model.WindowActivationRequested += () => WindowActivationRequests++;
        Model.GeckoFocusRequested += () => GeckoFocusRequests++;
    }

    public string SettingsPath => Path.Combine(directory, "settings.json");

    /// <summary>A path inside the fixture's folder, for tests that write files.</summary>
    public string PathFor(string name) => Path.Combine(directory, name);

    /// <summary>What a second launch would read from disk.</summary>
    public BrowserSettings ReloadSettings() => new(SettingsPath);

    /// <summary>Opens a tab on <paramref name="url"/> in Gecko and returns it once Gecko has created it.</summary>
    public BrowserTab OpenGeckoTab(string url)
    {
        var tab = Model.NewTab(url, EngineKind.Gecko);
        Assert.NotNull(tab.GeckoId);
        return tab;
    }

    public (BrowserTab Tab, FakeChromiumBrowser Browser) OpenChromiumTab(string url)
    {
        var tab = Model.NewTab(url, EngineKind.Chromium);
        return (tab, Assert.IsType<FakeChromiumBrowser>(tab.Chromium));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
