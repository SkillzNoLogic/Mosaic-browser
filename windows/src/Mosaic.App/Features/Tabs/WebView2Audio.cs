using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Mosaic.Core.Tabs;

namespace Mosaic.App.Features.Tabs;

/// <summary>
/// Mute and "playing audio" for a Chromium tab, through its WebView2
/// (<c>CoreWebView2.IsMuted</c>, <c>IsDocumentPlayingAudio</c>). Supplied to the model through
/// <see cref="Core.Browser.BrowserModel.ChromiumAudioProvider"/>; works before the WebView2 has
/// finished initialising (the mute state is applied once it has).
/// </summary>
internal sealed class WebView2Audio : IChromiumAudio
{
    private readonly WebView2 view;
    private CoreWebView2? core;
    private bool muted;

    public WebView2Audio(WebView2 view)
    {
        this.view = view;
        if (view.CoreWebView2 is { } ready) Attach(ready);
        else view.CoreWebView2Initialized += Initialized;
    }

    public bool IsMuted
    {
        get => muted;
        set
        {
            muted = value;
            if (core is not null) core.IsMuted = value;
        }
    }

    public bool IsPlayingAudio => core?.IsDocumentPlayingAudio ?? false;

    public event Action? PlayingAudioChanged;

    private void Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        view.CoreWebView2Initialized -= Initialized;
        if (args.Exception is null && sender.CoreWebView2 is { } ready) Attach(ready);
    }

    private void Attach(CoreWebView2 ready)
    {
        core = ready;
        if (muted) ready.IsMuted = true;
        ready.IsDocumentPlayingAudioChanged += (_, _) => PlayingAudioChanged?.Invoke();
        if (ready.IsDocumentPlayingAudio) PlayingAudioChanged?.Invoke();
    }
}
