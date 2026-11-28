using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;

namespace Mosaic.App.Features.Productivity;

/// <summary>
/// Page scripting for a Chromium tab through its WinUI <see cref="WebView2"/> control, for
/// browsers that do not implement <see cref="IPageScriptHost"/> themselves: the control is the
/// browser's public property of type <see cref="WebView2"/> (the winui area's <c>View</c>).
/// </summary>
internal sealed class WebView2ScriptHost : IPageScriptHost
{
    private readonly WebView2 view;
    private Action<string>? received;
    private CoreWebView2? subscribed;

    private WebView2ScriptHost(WebView2 view)
    {
        this.view = view;
    }

    public static IPageScriptHost? Resolve(IChromiumBrowser browser)
    {
        foreach (var property in browser.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (typeof(WebView2).IsAssignableFrom(property.PropertyType) && property.GetValue(browser) is WebView2 view)
            {
                return new WebView2ScriptHost(view);
            }
        }
        return null;
    }

    public bool CanRunScripts => view.CoreWebView2 is not null;

    public async Task<string?> ExecuteScriptAsync(string script)
    {
        if (view.CoreWebView2 is not { } core) return null;
        try
        {
            return await core.ExecuteScriptAsync(script);
        }
        catch (Exception)
        {
            // The page navigated away or the browser closed while the script ran.
            return null;
        }
    }

    public event Action<string>? MessageReceived
    {
        add
        {
            received += value;
            Subscribe();
        }
        remove
        {
            received -= value;
            if (received is null) Unsubscribe();
        }
    }

    private void Subscribe()
    {
        if (subscribed is not null) return;
        if (view.CoreWebView2 is { } core)
        {
            subscribed = core;
            core.WebMessageReceived += OnMessage;
        }
        else
        {
            view.CoreWebView2Initialized += OnInitialized;
        }
    }

    private void OnInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        view.CoreWebView2Initialized -= OnInitialized;
        if (received is not null) Subscribe();
    }

    private void Unsubscribe()
    {
        view.CoreWebView2Initialized -= OnInitialized;
        if (subscribed is null) return;
        subscribed.WebMessageReceived -= OnMessage;
        subscribed = null;
    }

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string text;
        try
        {
            text = args.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;  // a JSON message from someone else
        }
        received?.Invoke(text);
    }
}
