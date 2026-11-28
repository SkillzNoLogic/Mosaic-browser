namespace Mosaic.Core.Tabs;

/// <summary>
/// Optional capability of a Chromium tab's browser (<see cref="Browser.IChromiumBrowser"/>):
/// muting and "is playing audio". The winui area's WebView2 browser implements it with
/// <c>CoreWebView2.IsMuted</c>, <c>IsDocumentPlayingAudio</c> and
/// <c>IsDocumentPlayingAudioChanged</c>; the tabs area applies <c>BrowserTab.IsMuted</c> through
/// it and keeps tabs that play audio awake.
/// </summary>
/// <remarks>Members are used on the UI thread, like the rest of <see cref="Browser.IChromiumBrowser"/>.</remarks>
public interface IChromiumAudio
{
    bool IsMuted { get; set; }

    bool IsPlayingAudio { get; }

    event Action? PlayingAudioChanged;
}
