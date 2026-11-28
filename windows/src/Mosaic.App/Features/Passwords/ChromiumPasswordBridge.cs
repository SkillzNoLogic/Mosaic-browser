using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Mosaic.Core;
using Mosaic.Core.Browser;
using Mosaic.Core.Passwords;

namespace Mosaic.App.Features.Passwords;

/// <summary>
/// Passwords in Chromium tabs: injects the shared page script into each tab's WebView2 and
/// carries its messages to <see cref="PasswordManager"/>, and fill requests back.
/// Counterpart of <c>CEFPasswords.mm</c> on macOS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Injection.</b> <c>AddScriptToExecuteOnDocumentCreatedAsync</c> with
/// <see cref="PasswordPageScripts.ChromiumDocumentScript"/>; WebView2 runs it in the top-level
/// document and in frames, and the script attaches only in the top-level document and in frames
/// of the same origin. The document already open when the bridge attaches gets the same script
/// once through <c>ExecuteScriptAsync</c> (it is guarded against running twice).
/// </para>
/// <para>
/// <b>Trust.</b> The origin is WebView2's <c>WebMessageReceivedEventArgs.Source</c> (the
/// sending document's address, set by the engine) and the frame kind is which object raised the
/// message: the WebView (top-level document) or a <c>CoreWebView2Frame</c>, whose origin must
/// equal the top-level document's or the message is dropped (cross-origin frames refused).
/// </para>
/// <para>
/// <b>Private tabs.</b> Saving is never offered and no password is suggested in a WebView2 whose
/// profile is InPrivate (messages are marked private, from the engine's own flag) or in a tab the
/// privacy area marks private (<see cref="PasswordManager.IsPrivateTab"/>). Filling a saved login
/// on request still works there, as on macOS.
/// </para>
/// <para>
/// <b>Finding the WebView2.</b> The winui area's <see cref="IChromiumBrowser"/> holds a WinUI
/// <see cref="WebView2"/>; the bridge finds it as a property of that type (the WPF port called it
/// <c>View</c>), so no other area's code has to call in.
/// </para>
/// </remarks>
internal sealed class ChromiumPasswordBridge : IChromiumPasswordBridge
{
    private readonly BrowserModel model;
    private readonly PasswordManager passwords;
    private readonly Dictionary<IChromiumBrowser, Attachment> attachments = new(ReferenceEqualityComparer.Instance);
    private string? pageScript;
    private string? documentScript;

    public ChromiumPasswordBridge(BrowserModel model)
    {
        this.model = model;
        passwords = model.Passwords;
    }

    public bool IsScriptLoaded => pageScript is not null;

    /// <summary>Reads the page script (off the UI thread) and starts following the tabs.</summary>
    public async Task StartAsync()
    {
        var path = Path.Combine(AppPaths.GeckoAgent, "passwords-page.js");
        try
        {
            pageScript = await File.ReadAllTextAsync(path);
            documentScript = PasswordPageScripts.ChromiumDocumentScript(pageScript);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            passwords.ShowNotice("Password saving is unavailable in Chromium tabs: passwords-page.js is missing.");
            return;
        }
        model.Tabs.CollectionChanged += TabsChanged;
        foreach (var tab in model.Tabs) Follow(tab);
    }

    private void TabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (BrowserTab tab in e.NewItems ?? Array.Empty<BrowserTab>()) Follow(tab);
        foreach (BrowserTab tab in e.OldItems ?? Array.Empty<BrowserTab>()) tab.PropertyChanged -= TabPropertyChanged;
        // Browsers of closed tabs (and tabs moved to Gecko) are let go.
        var live = model.Tabs.Select(t => t.Chromium).Where(b => b is not null).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var gone in attachments.Keys.Where(b => !live.Contains(b)).ToList())
        {
            attachments[gone].Detach();
            attachments.Remove(gone);
        }
    }

    private void Follow(BrowserTab tab)
    {
        tab.PropertyChanged -= TabPropertyChanged;
        tab.PropertyChanged += TabPropertyChanged;
        Attach(tab);
    }

    private void TabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.Chromium) && sender is BrowserTab tab) Attach(tab);
    }

    private void Attach(BrowserTab tab)
    {
        if (documentScript is null || tab.Chromium is not { } browser || attachments.ContainsKey(browser)) return;
        if (FindView(browser) is not { } view) return;
        attachments[browser] = new Attachment(this, tab, view);
    }

    private static readonly Dictionary<Type, Func<object, WebView2?>> finders = [];

    /// <summary>The WebView2 control inside the winui area's browser object.</summary>
    internal static WebView2? FindView(IChromiumBrowser browser)
    {
        if (browser is WebView2 itself) return itself;
        var type = browser.GetType();
        if (!finders.TryGetValue(type, out var finder))
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = type.GetProperties(flags).FirstOrDefault(p => typeof(WebView2).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0);
            var field = property is null ? type.GetFields(flags).FirstOrDefault(f => typeof(WebView2).IsAssignableFrom(f.FieldType)) : null;
            finder = property is not null ? o => property.GetValue(o) as WebView2
                : field is not null ? o => field.GetValue(o) as WebView2
                : _ => null;
            finders[type] = finder;
        }
        return finder(browser);
    }

    // ---- Filling ----

    public Task<int> FillAsync(BrowserTab tab, SiteOrigin origin, string username, string password) =>
        RunAsync(tab, origin, PasswordPageScripts.ChromiumFill(pageScript ?? "", origin, username, password));

    public Task<int> FillGeneratedAsync(BrowserTab tab, SiteOrigin origin, string password) =>
        RunAsync(tab, origin, PasswordPageScripts.ChromiumFillGenerated(pageScript ?? "", origin, password));

    private async Task<int> RunAsync(BrowserTab tab, SiteOrigin origin, string script)
    {
        if (pageScript is null || tab.Chromium is not { } browser || !attachments.TryGetValue(browser, out var attachment)) return 0;
        return await attachment.FillAsync(origin, script);
    }

    private static int Count(string? result) =>
        result is not null && int.TryParse(result, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : 0;

    /// <summary>One tab's WebView2, once it has a CoreWebView2.</summary>
    private sealed class Attachment
    {
        private readonly ChromiumPasswordBridge bridge;
        private readonly BrowserTab tab;
        private readonly WebView2 view;
        private readonly List<CoreWebView2Frame> frames = [];
        private CoreWebView2? core;
        private bool ranOnCurrentDocument;
        private bool isPrivate;
        private bool detached;

        public Attachment(ChromiumPasswordBridge bridge, BrowserTab tab, WebView2 view)
        {
            this.bridge = bridge;
            this.tab = tab;
            this.view = view;
            if (view.CoreWebView2 is { } ready) _ = SetUpAsync(ready);
            else view.CoreWebView2Initialized += Initialized;
        }

        private void Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
        {
            view.CoreWebView2Initialized -= Initialized;
            if (args.Exception is null && sender.CoreWebView2 is { } ready) _ = SetUpAsync(ready);
        }

        private async Task SetUpAsync(CoreWebView2 webView)
        {
            if (detached) return;
            isPrivate = webView.Profile.IsInPrivateModeEnabled;
            core = webView;
            webView.WebMessageReceived += MainMessage;
            webView.FrameCreated += FrameCreated;
            webView.NavigationCompleted += NavigationCompleted;
            await webView.AddScriptToExecuteOnDocumentCreatedAsync(bridge.documentScript);
            // The document that was already loading or loaded before the script was registered.
            if (!ranOnCurrentDocument && webView.Source is { Length: > 0 } source && SiteOrigin.FromUrl(source) is not null)
            {
                ranOnCurrentDocument = true;
                _ = webView.ExecuteScriptAsync(bridge.documentScript);
            }
        }

        private void NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (ranOnCurrentDocument) return;
            ranOnCurrentDocument = true;
            _ = sender.ExecuteScriptAsync(bridge.documentScript);
        }

        private void MainMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args) =>
            Deliver(args.Source, args.WebMessageAsJson, PasswordPageMessage.FrameKind.Main);

        private void FrameCreated(CoreWebView2 sender, CoreWebView2FrameCreatedEventArgs args)
        {
            var frame = args.Frame;
            frames.Add(frame);
            frame.WebMessageReceived += (_, message) =>
            {
                // A frame's message counts only while its document has the top-level document's origin.
                var top = SiteOrigin.FromUrl(core?.Source);
                var origin = SiteOrigin.FromUrl(message.Source);
                if (top is null || origin != top) return;
                Deliver(message.Source, message.WebMessageAsJson, PasswordPageMessage.FrameKind.SameOrigin);
            };
            frame.Destroyed += (_, _) => frames.Remove(frame);
        }

        private void Deliver(string source, string json, PasswordPageMessage.FrameKind frame)
        {
            if (detached || !ReferenceEquals(tab.Chromium is { } current ? FindView(current) : null, view)) return;
            if (PasswordPageScripts.FromWebMessage(json) is not { } message) return;
            message.Origin = SiteOrigin.FromUrl(source);
            message.Frame = frame;
            if (message.Origin is null) return;
            bridge.passwords.HandleChromium(tab, message, isPrivate);
        }

        public async Task<int> FillAsync(SiteOrigin origin, string script)
        {
            // As in Gecko: only when the tab's page itself is on that origin.
            if (core is null || SiteOrigin.FromUrl(core.Source) != origin) return 0;
            try
            {
                var count = Count(await core.ExecuteScriptAsync(script));
                if (count > 0) return count;
                foreach (var frame in frames.ToList())
                {
                    if (frame.IsDestroyed() != 0) continue;
                    // The script itself does nothing unless the frame's document is on `origin`.
                    count = Count(await frame.ExecuteScriptAsync(script));
                    if (count > 0) return count;
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException)
            {
                // The page went away meanwhile.
            }
            return 0;
        }

        public void Detach()
        {
            detached = true;
            view.CoreWebView2Initialized -= Initialized;
            if (core is null) return;
            try
            {
                core.WebMessageReceived -= MainMessage;
                core.FrameCreated -= FrameCreated;
                core.NavigationCompleted -= NavigationCompleted;
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException)
            {
                // Already closed.
            }
            frames.Clear();
        }
    }
}
