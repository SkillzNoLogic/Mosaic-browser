using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Engine;

/// <summary>A picture of a page.</summary>
/// <param name="Png">The picture, PNG encoded.</param>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="Cropped">A full-page picture stopped at <see cref="PageFeatures.MaxScreenshotHeight"/>.</param>
public sealed record PageCapture(byte[] Png, int Width, int Height, bool Cropped);

/// <summary>
/// One Chromium tab's page features, as <see cref="PageFeatures"/> sees them. The app implements
/// it around the tab's WebView2 (<c>Features/Engine/ChromiumPage.cs</c>); tests use a fake.
/// </summary>
/// <remarks>
/// UI thread only. Nothing throws: a page whose browser is gone or not ready yet does nothing
/// and the asynchronous members return null/false. Zoom and mute are remembered and applied
/// again whenever the browser (re)creates its page.
/// </remarks>
public interface IChromiumPage
{
    /// <summary>
    /// Finds <paramref name="text"/>: from the top when <paramref name="again"/> is false (a new
    /// search), else the next or previous match. Highlights every match.
    /// </summary>
    Task<FindStatus?> FindAsync(string text, bool matchCase, bool again, bool backwards);

    /// <summary>Ends the find session: highlights go.</summary>
    void StopFind();

    /// <summary>Zooms the page (1 = 100 %), as browser zoom does: the layout viewport shrinks.</summary>
    void SetZoom(double factor);

    void SetMuted(bool muted);

    /// <summary>Chromium's print preview (inside the page area).</summary>
    void Print();

    Task<bool> PrintToPdfAsync(string path);

    /// <summary>Opens Chromium's developer tools (their own window).</summary>
    void OpenDevTools();

    Task<PageCapture?> CaptureAsync(bool fullPage);

    /// <summary>"entered", "left", "none" (no video) or "unsupported".</summary>
    Task<string> TogglePictureInPictureAsync();

    /// <summary>Leaves the JavaScript Fullscreen API's full screen, if the page is in it.</summary>
    void ExitFullScreen();

    /// <summary>Runs a script in the page (with a user gesture when asked); the JSON result, or null.</summary>
    Task<JsonNode?> EvaluateAsync(string script, bool userGesture);

    /// <summary>A real (trusted) mouse click at (x, y) in CSS pixels; button 0 left, 2 right.</summary>
    void Click(double x, double y, int button);
}

/// <summary>How a permission request is answered.</summary>
public enum PermissionResponse { Allow, Deny, Dismiss }

/// <summary>Answers one engine permission request (a WebView2 deferral, …). Called once.</summary>
public interface IPermissionResponder
{
    void Respond(PermissionResponse response);
}

public enum PageDialogKind { Alert, Confirm, Prompt, BeforeUnload }

/// <summary>Finishes one JavaScript dialog (a WebView2 deferral). Called once.</summary>
public interface IDialogResponder
{
    void Finish(bool accepted, string? text);
}

/// <summary>What Mosaic's window does for the page features (save pickers, clipboard, full screen…).</summary>
/// <remarks>The app implements it (<c>Features/Engine/EngineHost.cs</c>); UI thread only.</remarks>
public interface IPageFeaturesHost
{
    /// <summary>A save picker parented to Mosaic's window. Null when cancelled.</summary>
    /// <param name="suggestedName">File name without extension.</param>
    /// <param name="extension">"pdf" or "png".</param>
    Task<string?> AskSavePathAsync(string suggestedName, string extension);

    /// <summary>Where screenshots go without asking: Pictures\Screenshots.</summary>
    string ScreenshotsFolder { get; }

    bool CopyImage(byte[] png);

    void CopyText(string text);

    /// <summary>The system's "not possible" sound.</summary>
    void Beep();

    /// <summary>Puts Mosaic's window into (or out of) full screen for a page.</summary>
    void SetPageFullScreen(bool fullScreen);

    /// <summary>Opens a Windows Settings page (<c>ms-settings:</c>).</summary>
    void OpenSettings(string uri);
}

/// <summary>A blocked-pop-up decision for a Chromium tab, made before the model opens a tab for it.</summary>
public enum PopupVerdict { Open, Block }

/// <summary>Where a tab is in its life, for <see cref="PageFeatures"/>'s clean-up.</summary>
internal enum DropReason { Navigated, Closed, EngineSwitched }

/// <summary>Helpers shared by the engine area's types.</summary>
internal static class PageText
{
    /// <summary>A file name (without extension) from the page's title: Windows-safe and not too long.</summary>
    public static string FileName(BrowserTab tab, string? suffix)
    {
        var name = tab.DisplayTitle.Trim();
        if (name.Length == 0) name = "Page";
        foreach (var bad in "<>:\"/\\|?*") name = name.Replace(bad, '-');
        name = new string([.. name.Where(character => !char.IsControl(character))]).Trim().TrimEnd('.');
        if (name.Length == 0) name = "Page";
        if (name.Length > 80) name = name[..80].TrimEnd();
        return suffix is null ? name : $"{name} ({suffix})";
    }
}
