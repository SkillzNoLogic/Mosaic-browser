using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Mosaic.Core.Downloads;
using WebView2Control = Microsoft.UI.Xaml.Controls.WebView2;

namespace Mosaic.App.Features.Downloads;

/// <summary>
/// Chromium (WebView2) downloads into Mosaic's download manager: <c>DownloadStarting</c> with a
/// deferral while Mosaic decides (Free Download Manager, duplicate, where to save), then the
/// operation's progress and state.
/// </summary>
/// <remarks>
/// <para>Every <see cref="CoreWebView2"/> must be attached once with <see cref="Attach"/>. The winui
/// area's Chromium code should call it where it creates a tab's WebView2 (one line); until then
/// <see cref="AttachAll"/> finds the WebView2 controls in the window's visual tree whenever the
/// tabs change, so downloads work either way.</para>
/// <para>WebView2 has no API to start a download of a URL (CEF's <c>StartDownload</c>), which
/// retries and the <c>download</c> automation command need. <see cref="StartDownload"/> navigates
/// a hidden helper WebView2 (same environment and profile) to the URL: if the server sends a
/// file, its <c>DownloadStarting</c> reaches the manager as usual; if it turns out to be a page,
/// the manager downloads it with Gecko instead (<see cref="DownloadManager.ChromiumStartFailed"/>).</para>
/// </remarks>
internal sealed class WebView2Downloads(DownloadManager manager, nint window) : IChromiumDownloadService
{
    private static readonly TimeSpan HelperPatience = TimeSpan.FromSeconds(30);

    private readonly ConditionalWeakTable<CoreWebView2, object> attached = [];
    private CoreWebView2Environment? environment;
    private CoreWebView2Profile? profile;
    private CoreWebView2Controller? helper;
    private Task<CoreWebView2Controller?>? creatingHelper;
    private readonly Dictionary<string, DateTimeOffset> helperStarts = [];

    public string? UserAgent { get; private set; }

    /// <summary>Starts sending <paramref name="core"/>'s downloads to Mosaic. Safe to call more than once.</summary>
    public void Attach(CoreWebView2 core)
    {
        if (attached.TryGetValue(core, out _)) return;
        attached.Add(core, new object());
        environment ??= core.Environment;
        profile ??= core.Profile;
        UserAgent ??= core.Settings.UserAgent;
        core.DownloadStarting += OnDownloadStarting;
    }

    /// <summary>Attaches every WebView2 control under <paramref name="root"/> (now, or once its CoreWebView2 exists).</summary>
    public void AttachAll(DependencyObject? root)
    {
        if (root is null) return;
        if (root is WebView2Control control)
        {
            if (control.CoreWebView2 is { } core) Attach(core);
            else control.CoreWebView2Initialized += (sender, _) => { if (sender.CoreWebView2 is { } ready) Attach(ready); };
            return;
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++) AttachAll(VisualTreeHelper.GetChild(root, index));
    }

    private async void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        // Mosaic shows downloads in its own list, never Edge's download flyout.
        args.Handled = true;
        var deferral = args.GetDeferral();
        try
        {
            var operation = args.DownloadOperation;
            var download = new WebView2Download(operation);
            var suggested = DownloadFileName.FromContentDisposition(operation.ContentDisposition)
                ?? (string.IsNullOrEmpty(args.ResultFilePath) ? null : Path.GetFileName(args.ResultFilePath));
            var page = ReferenceEquals(sender, helper?.CoreWebView2) ? null : sender.Source;
            if (ReferenceEquals(sender, helper?.CoreWebView2)) helperStarts.Remove(operation.Uri);
            var path = await manager.ChromiumStartingAsync(download, suggested, page);
            if (path is null)
            {
                args.Cancel = true;
                return;
            }
            args.ResultFilePath = path;
            operation.BytesReceivedChanged += (_, _) => manager.ChromiumUpdated(download);
            operation.StateChanged += (_, _) => manager.ChromiumUpdated(download);
            manager.ChromiumUpdated(download);
        }
        catch (Exception)
        {
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---- IChromiumDownloadService ----

    public bool StartDownload(string url)
    {
        if (environment is null || window == 0) return false;
        helperStarts[url] = DateTimeOffset.UtcNow;
        _ = NavigateHelperAsync(url);
        return true;
    }

    private async Task NavigateHelperAsync(string url)
    {
        var controller = await (creatingHelper ??= CreateHelperAsync());
        if (controller is null)
        {
            helperStarts.Remove(url);
            manager.ChromiumStartFailed(url);
            return;
        }
        controller.CoreWebView2.Navigate(url);
        // A URL that is a page (not a file) never raises DownloadStarting.
        await Task.Delay(HelperPatience);
        if (helperStarts.Remove(url)) manager.ChromiumStartFailed(url);
    }

    private async Task<CoreWebView2Controller?> CreateHelperAsync()
    {
        if (environment is null) return null;
        try
        {
            var controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)window));
            controller.IsVisible = false;
            controller.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            controller.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            controller.CoreWebView2.NavigationCompleted += (core, args) =>
            {
                // A page that loaded (rather than a file) is not a download.
                if (args.IsSuccess && args.HttpStatusCode is >= 200 and < 400 && helperStarts.Remove(core.Source))
                {
                    manager.ChromiumStartFailed(core.Source);
                }
            };
            Attach(controller.CoreWebView2);
            helper = controller;
            return controller;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<string?> CookiesAsync(string url)
    {
        if (profile?.CookieManager is not { } cookies) return null;
        try
        {
            var list = await cookies.GetCookiesAsync(url);
            return string.Join("; ", list.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Closes the helper (window closing).</summary>
    public void Close()
    {
        helper?.Close();
        helper = null;
    }
}

/// <summary><see cref="IChromiumDownload"/> over WebView2's download operation.</summary>
internal sealed class WebView2Download(CoreWebView2DownloadOperation operation) : IChromiumDownload
{
    public string Url => operation.Uri;
    public string ResultFilePath => operation.ResultFilePath;
    public string? MimeType => operation.MimeType;
    public long TotalBytes => operation.TotalBytesToReceive is var total && total > 0 ? total : -1;
    public long ReceivedBytes => operation.BytesReceived;
    public bool CanResume => operation.CanResume;

    public ChromiumDownloadState State => operation.State switch
    {
        CoreWebView2DownloadState.Completed => ChromiumDownloadState.Completed,
        CoreWebView2DownloadState.InProgress => ChromiumDownloadState.InProgress,
        // Pausing and cancelling are interruptions with a reason, in WebView2.
        _ => operation.InterruptReason switch
        {
            CoreWebView2DownloadInterruptReason.UserPaused => ChromiumDownloadState.Paused,
            CoreWebView2DownloadInterruptReason.UserCanceled => ChromiumDownloadState.Cancelled,
            _ => ChromiumDownloadState.Interrupted,
        },
    };

    public string? InterruptReason => operation.State == CoreWebView2DownloadState.Interrupted
        ? Describe(operation.InterruptReason)
        : null;

    public void Pause() => operation.Pause();
    public void Resume() => operation.Resume();
    public void Cancel() => operation.Cancel();

    internal static string Describe(CoreWebView2DownloadInterruptReason reason) => reason switch
    {
        CoreWebView2DownloadInterruptReason.FileAccessDenied => "Could not write the file (access denied)",
        CoreWebView2DownloadInterruptReason.FileNoSpace => "The disk is full",
        CoreWebView2DownloadInterruptReason.FileNameTooLong => "The file name is too long",
        CoreWebView2DownloadInterruptReason.FileTooLarge => "The file is too large for this drive",
        CoreWebView2DownloadInterruptReason.FileMalicious => "Blocked: the file may be dangerous",
        CoreWebView2DownloadInterruptReason.FileBlockedByPolicy => "Blocked by policy",
        CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed => "Blocked by a security check (antivirus)",
        CoreWebView2DownloadInterruptReason.FileFailed or CoreWebView2DownloadInterruptReason.FileTransientError
            or CoreWebView2DownloadInterruptReason.FileTooShort or CoreWebView2DownloadInterruptReason.FileHashMismatch
            => "Could not write the file",
        CoreWebView2DownloadInterruptReason.NetworkTimeout => "The connection timed out",
        CoreWebView2DownloadInterruptReason.NetworkDisconnected => "The network was disconnected",
        CoreWebView2DownloadInterruptReason.NetworkFailed or CoreWebView2DownloadInterruptReason.NetworkServerDown
            or CoreWebView2DownloadInterruptReason.NetworkInvalidRequest => "Network error",
        CoreWebView2DownloadInterruptReason.ServerUnauthorized => "The server needs you to sign in",
        CoreWebView2DownloadInterruptReason.ServerForbidden => "The server refused the download",
        CoreWebView2DownloadInterruptReason.ServerCertificateProblem => "The server's certificate is not valid",
        CoreWebView2DownloadInterruptReason.ServerNoRange => "The server cannot continue the download",
        CoreWebView2DownloadInterruptReason.ServerFailed or CoreWebView2DownloadInterruptReason.ServerBadContent
            or CoreWebView2DownloadInterruptReason.ServerUnexpectedResponse
            or CoreWebView2DownloadInterruptReason.ServerContentLengthMismatch
            or CoreWebView2DownloadInterruptReason.ServerCrossOriginRedirect => "Server error",
        CoreWebView2DownloadInterruptReason.UserShutdown => "Interrupted when Mosaic quit",
        CoreWebView2DownloadInterruptReason.DownloadProcessCrashed => "The download stopped unexpectedly",
        _ => "Download failed",
    };
}
