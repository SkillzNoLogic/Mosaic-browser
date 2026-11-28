using Mosaic.Core.Privacy;

namespace Mosaic.Core.Browser;

// Privacy area: private tabs, the connection-security indicator and the privacy controller.
// Port of Sources/Mosaic/Features/Privacy/BrowserModel+Privacy.swift. The automation commands are
// in PrivacyAutomation.cs.

public sealed partial class BrowserModel
{
    private PrivacyController? privacy;
    private bool showClearBrowsingData;

    /// <summary>Privacy settings, site data and clearing; created on first use.</summary>
    public PrivacyController Privacy => privacy ??= new PrivacyController(Gecko, Blocker, PrivacySettingsPath, time);

    /// <summary>Where the privacy settings are kept; tests point it at a throwaway folder before first use.</summary>
    internal string? PrivacySettingsPath { get; set; }

    /// <summary>
    /// Whether the Clear Browsing Data dialog is open over Settings › Privacy &amp; Security. The
    /// settings page shows it while this is true and sets it back to false when it closes.
    /// </summary>
    public bool ShowClearBrowsingData { get => showClearBrowsingData; set => Set(ref showClearBrowsingData, value); }

    /// <summary>
    /// Asks the shell to show Settings › Privacy &amp; Security (the privacy area's app code routes
    /// it to the shell's settings).
    /// </summary>
    public event Action? PrivacySettingsRequested;

    /// <summary>Opens Settings › Privacy &amp; Security with the Clear Browsing Data dialog (Ctrl+Shift+Delete).</summary>
    public void ShowClearBrowsingDataDialog()
    {
        PrivacySettingsRequested?.Invoke();
        ShowClearBrowsingData = true;
    }

    /// <summary>Opens Settings › Privacy &amp; Security.</summary>
    public void ShowPrivacySettings() => PrivacySettingsRequested?.Invoke();

    /// <summary>
    /// Opens a private tab: Chromium, with an in-memory profile shared only by private tabs and
    /// discarded when the last one closes. Nothing it does is written to disk, and it never
    /// appears in history (<see cref="PrivateTabs"/>: <c>tab.IsPrivate</c>). Returns null (with a
    /// notice) when Chromium is not available, or this build cannot open private browsers.
    /// </summary>
    public BrowserTab? NewPrivateTab(string url = "")
    {
        if (!chromium.IsAvailable)
        {
            Notice = "Private tabs need the Chromium engine, which is not available on this PC.";
            return null;
        }
        if (chromium is not IPrivateChromiumEngine)
        {
            Notice = "Private tabs are not available in this build.";
            return null;
        }
        var tab = new BrowserTab(EngineKind.Chromium);
        PrivateTabs.MarkPrivate(tab);
        InsertAfterSelected(tab);
        Select(tab);
        if (url.Length > 0) Load(url, tab);
        else FocusAddressBar();
        return tab;
    }

    public IEnumerable<BrowserTab> PrivateTabList => Tabs.Where(tab => tab.IsPrivate);

    /// <summary>
    /// The browser for a Chromium tab: a private one for a private tab. The model's
    /// <see cref="Load"/> creates Chromium browsers through this.
    /// </summary>
    internal IChromiumBrowser CreateChromiumBrowser(BrowserTab tab, string url) =>
        tab.IsPrivate && chromium is IPrivateChromiumEngine privateEngine
            ? privateEngine.CreatePrivateBrowser(url)
            : chromium.CreateBrowser(url);

    /// <summary>
    /// Whether moving the selected tab to <paramref name="kind"/> must be refused: a private tab
    /// stays on Chromium, because Mosaic has no private Gecko windows. Says why in a notice.
    /// </summary>
    internal bool RefusesEngineSwitch(EngineKind kind)
    {
        if (kind != EngineKind.Gecko || selectedTab is not { IsPrivate: true }) return false;
        Notice = PrivacyController.GeckoPrivateNotice;
        return true;
    }

    /// <summary>Snapshot events from Gecko that belong to the privacy area ("newPrivateTab": Ctrl+Shift+N inside a Gecko page).</summary>
    internal void HandlePrivacyEvent(string eventType)
    {
        if (eventType != "newPrivateTab") return;
        RequestWindowActivation();
        NewPrivateTab();
    }

    /// <summary>The padlock for the selected tab's page (shell: the address field).</summary>
    public ConnectionSecurity SelectedConnectionSecurity => selectedTab is { } tab ? ConnectionSecurities.Of(tab.Url) : ConnectionSecurity.NotApplicable;

    /// <summary>
    /// The privacy area's start-up work: connects the Chromium side, hooks Gecko's starts, applies
    /// saved blocker options and fetches lists never downloaded. Called once by the app.
    /// </summary>
    public void StartPrivacy(IChromiumPrivacy? chromiumPrivacy)
    {
        Blocker.StartPrivacy();
        Privacy.Start(chromiumPrivacy, () => Tabs.Where(tab => !tab.IsPrivate).Select(tab => tab.Url).ToList());
    }
}
