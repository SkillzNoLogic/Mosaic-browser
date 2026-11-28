namespace Mosaic.Core.Downloads;

/// <summary>
/// What the download manager needs from the app and from Windows: opening files, the Recycle
/// Bin, the clipboard, notifications and taskbar progress. The app implements it
/// (<c>Features/Downloads/WindowsDownloadShell.cs</c>); <see cref="RecordingDownloadShell"/> stands
/// in for tests and records what would have happened.
/// </summary>
/// <remarks>Members are called on the UI thread and must not block it.</remarks>
public interface IDownloadShell
{
    /// <summary>Launched with <c>--mosaic-automation</c>: never open files, Explorer or other apps on the screen.</summary>
    bool IsScripted { get; }

    /// <summary>Whether Mosaic's window is the foreground window.</summary>
    bool IsAppActive { get; }

    /// <summary>Opens a file with its default app (Windows' own Attachment Manager checks still apply).</summary>
    void OpenFile(string path);

    /// <summary>Shows the file selected in File Explorer.</summary>
    void RevealFile(string path);

    void OpenFolder(string path);

    /// <summary>Moves the file to the Recycle Bin (not a permanent delete).</summary>
    void MoveToRecycleBin(string path);

    void CopyText(string text);

    /// <summary>A "Download finished" notification; clicking it opens the Downloads page.</summary>
    void ShowNotification(string title, string body, Guid id);

    /// <summary>
    /// Taskbar progress: <paramref name="activeCount"/> downloads running, together this far
    /// (null when a size is unknown: indeterminate). Zero active clears it.
    /// </summary>
    void ShowProgress(int activeCount, double? progress);
}

/// <summary>An <see cref="IDownloadShell"/> that only records requests (tests, and before the app wires its own).</summary>
public sealed class RecordingDownloadShell : IDownloadShell
{
    public bool IsScripted { get; set; } = true;
    public bool IsAppActive { get; set; } = true;
    public List<string> Requests { get; } = [];
    public string? LastNotification { get; private set; }
    public (int Active, double? Progress) LastProgress { get; private set; }

    public void OpenFile(string path) => Requests.Add("open:" + path);
    public void RevealFile(string path) => Requests.Add("reveal:" + path);
    public void OpenFolder(string path) => Requests.Add("folder:" + path);

    public void MoveToRecycleBin(string path)
    {
        Requests.Add("recycle:" + path);
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void CopyText(string text) => Requests.Add("copy:" + text);

    public void ShowNotification(string title, string body, Guid id) => LastNotification = $"{title}: {body}";

    public void ShowProgress(int activeCount, double? progress) => LastProgress = (activeCount, progress);
}

/// <summary>A Chromium (WebView2) download as the manager sees it; the app wraps <c>CoreWebView2DownloadOperation</c>.</summary>
public enum ChromiumDownloadState { InProgress, Paused, Interrupted, Completed, Cancelled }

/// <summary>One live Chromium download. Members are used on the UI thread.</summary>
public interface IChromiumDownload
{
    /// <summary>Where the file comes from (after redirects).</summary>
    string Url { get; }

    /// <summary>The file being written; final once <see cref="State"/> is completed.</summary>
    string ResultFilePath { get; }

    string? MimeType { get; }

    /// <summary>-1 when unknown.</summary>
    long TotalBytes { get; }

    long ReceivedBytes { get; }

    ChromiumDownloadState State { get; }

    /// <summary>Why it stopped, readable ("Network error"), when interrupted.</summary>
    string? InterruptReason { get; }

    bool CanResume { get; }

    void Pause();
    void Resume();
    void Cancel();
}

/// <summary>Chromium-wide download services the app provides.</summary>
public interface IChromiumDownloadService
{
    /// <summary>
    /// Starts downloading <paramref name="url"/> in Chromium (its <c>DownloadStarting</c> then reaches
    /// the manager as usual). False when Chromium cannot (no WebView2, nothing to start it from).
    /// </summary>
    bool StartDownload(string url);

    /// <summary>The User-Agent Chromium sends, for Free Download Manager.</summary>
    string? UserAgent { get; }

    /// <summary>The Cookie header Chromium would send to <paramref name="url"/> ("a=1; b=2").</summary>
    Task<string?> CookiesAsync(string url);
}

/// <summary>How a download reached Free Download Manager.</summary>
public enum FdmRoute
{
    /// <summary>Through FDM's helper, with referrer and User-Agent (and cookies if allowed).</summary>
    Integration,
    /// <summary>By passing the URL to FDM's program; the address only.</summary>
    OpenUrl,
}

/// <summary>Free Download Manager, when it is installed.</summary>
public interface IFreeDownloadManager
{
    bool IsInstalled { get; }
    string? Version { get; }

    /// <summary>Looks FDM up again (it may have been installed or removed while Mosaic runs). Cheap; may touch the registry.</summary>
    void Refresh();

    /// <summary>
    /// Hands <paramref name="request"/> to FDM. Returns once FDM has it; <paramref name="onOutcome"/>
    /// is called later (on the caller's context) when the user confirms or cancels in FDM (only for
    /// <see cref="FdmRoute.Integration"/>). Throws with a presentable message when it cannot.
    /// </summary>
    Task<FdmRoute> SendAsync(FdmDownloadRequest request, bool bringToFront, Action<FdmOutcome> onOutcome);
}

/// <summary>No Free Download Manager (non-Windows, tests).</summary>
public sealed class NoFreeDownloadManager : IFreeDownloadManager
{
    public bool IsInstalled => false;
    public string? Version => null;
    public void Refresh() { }

    public Task<FdmRoute> SendAsync(FdmDownloadRequest request, bool bringToFront, Action<FdmOutcome> onOutcome) =>
        Task.FromException<FdmRoute>(new InvalidOperationException("Free Download Manager is not installed."));
}
