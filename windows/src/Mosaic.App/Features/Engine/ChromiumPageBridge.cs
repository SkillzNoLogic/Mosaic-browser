using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Mosaic.Core.Browser;
using Mosaic.Core.Engine;
using Mosaic.Core.Permissions;

namespace Mosaic.App.Features.Engine;

/// <summary>
/// Gives every Chromium tab its <see cref="ChromiumPage"/>: finds the tab's WebView2 behind the
/// winui area's <see cref="IChromiumBrowser"/>, hooks it as early as possible, and lets go when
/// the tab closes or changes engine.
/// </summary>
internal sealed class ChromiumPageBridge
{
    private readonly BrowserModel model;
    private readonly Dictionary<Guid, ChromiumPage> pages = [];

    public ChromiumPageBridge(BrowserModel model)
    {
        this.model = model;
        Current = this;
        model.Pages.ChromiumPages = PageOf;
        model.Pages.ChromiumPermissionMirror = Mirror;
        model.Tabs.CollectionChanged += TabsChanged;
        foreach (var tab in model.Tabs) Watch(tab);
    }

    public static ChromiumPageBridge? Current { get; private set; }

    /// <summary>The page features of a Chromium tab with a browser; null otherwise.</summary>
    public ChromiumPage? PageOf(BrowserTab tab)
    {
        if (tab.Engine != EngineKind.Chromium || tab.Chromium is not { } browser)
        {
            Release(tab.Id);
            return null;
        }
        if (pages.TryGetValue(tab.Id, out var page) && ReferenceEquals(page.Browser, browser)) return page;
        Release(tab.Id);
        if (FindWebView(browser) is not { } view) return null;
        page = new ChromiumPage(tab, browser, view, model.Pages);
        pages[tab.Id] = page;
        return page;
    }

    private void TabsChanged(object? sender, NotifyCollectionChangedEventArgs change)
    {
        foreach (var tab in change.NewItems?.OfType<BrowserTab>() ?? []) Watch(tab);
        var present = model.Tabs.Select(tab => tab.Id).ToHashSet();
        foreach (var id in pages.Keys.Where(id => !present.Contains(id)).ToList()) Release(id);
    }

    private void Watch(BrowserTab tab)
    {
        // Hooked the moment the browser is created, before its CoreWebView2 exists.
        tab.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(BrowserTab.Chromium) or nameof(BrowserTab.Engine)) PageOf(tab);
        };
        PageOf(tab);
    }

    private void Release(Guid id)
    {
        if (pages.Remove(id, out var page)) page.Close();
    }

    /// <summary>
    /// The WinUI WebView2 behind a browser: <see cref="IChromiumViewSource"/> if the winui area
    /// implements it, else its first public (then non-public) member of type WebView2.
    /// </summary>
    internal static WebView2? FindWebView(IChromiumBrowser browser)
    {
        if (browser is IChromiumViewSource { View: WebView2 view }) return view;
        if (browser is WebView2 itself) return itself;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = browser.GetType();
        foreach (var property in type.GetProperties(flags).OrderBy(p => p.GetMethod?.IsPublic == true ? 0 : 1))
        {
            if (typeof(WebView2).IsAssignableFrom(property.PropertyType) && property.GetIndexParameters().Length == 0
                && property.GetValue(browser) is WebView2 found)
            {
                return found;
            }
        }
        foreach (var field in type.GetFields(flags))
        {
            if (typeof(WebView2).IsAssignableFrom(field.FieldType) && field.GetValue(browser) is WebView2 found) return found;
        }
        return null;
    }

    /// <summary>A site decision made in Mosaic (or in Privacy settings) applied to Chromium's profile.</summary>
    private void Mirror(SitePermission permission, SitePermissionDecision? decision, string site)
    {
        CoreWebView2PermissionKind? kind = permission switch
        {
            SitePermission.Location => CoreWebView2PermissionKind.Geolocation,
            SitePermission.Notifications => CoreWebView2PermissionKind.Notifications,
            SitePermission.Clipboard => CoreWebView2PermissionKind.ClipboardRead,
            SitePermission.Camera => CoreWebView2PermissionKind.Camera,
            SitePermission.Microphone => CoreWebView2PermissionKind.Microphone,
            _ => null,
        };
        if (kind is not { } value || site == "file://") return;
        if (pages.Values.Select(page => page.Core).FirstOrDefault(core => core is not null) is not { } core) return;
        var state = decision switch
        {
            SitePermissionDecision.Allow => CoreWebView2PermissionState.Allow,
            SitePermissionDecision.Block => CoreWebView2PermissionState.Deny,
            _ => CoreWebView2PermissionState.Default,
        };
        _ = SetPermissionAsync(core, value, site, state);
    }

    private static async Task SetPermissionAsync(CoreWebView2 core, CoreWebView2PermissionKind kind, string site, CoreWebView2PermissionState state)
    {
        try
        {
            await core.Profile.SetPermissionStateAsync(kind, site, state);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or NotImplementedException)
        {
            // An older runtime: Mosaic still answers every request itself.
        }
    }
}
