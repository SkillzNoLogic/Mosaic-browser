using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using Mosaic.Core.Browser;
using Mosaic.Core.Engine;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Mosaic.App.Features.Engine;

/// <summary>What Mosaic's window does for the page features: pickers, clipboard, full screen, Settings.</summary>
internal sealed partial class EngineHost(BrowserModel model, Window window) : IPageFeaturesHost, IDevToolsProbe, IPanelCloser
{
    private AppWindowPresenterKind presenterBeforeFullScreen = AppWindowPresenterKind.Default;

    public string ScreenshotsFolder => PageFeatures.DefaultScreenshotsFolder;

    public async Task<string?> AskSavePathAsync(string suggestedName, string extension)
    {
        try
        {
            // The Windows App SDK picker: parented to Mosaic's window, and made for unpackaged apps.
            var picker = new FileSavePicker(window.AppWindow.Id)
            {
                SuggestedFileName = suggestedName,
                SuggestedStartLocation = extension == "png" ? PickerLocationId.PicturesLibrary : PickerLocationId.Downloads,
            };
            picker.FileTypeChoices.Add(extension == "png" ? "PNG image" : "PDF document", [$".{extension}"]);
            var result = await picker.PickSaveFileAsync();
            return result?.Path;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            model.Notice = "Couldn’t open the save dialog.";
            return null;
        }
    }

    public async Task<bool> CopyImageAsync(byte[] png)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void CopyText(string text)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (COMException)
        {
            // The clipboard is held by another app for a moment; nothing more to do.
        }
    }

    public void Beep() => MessageBeep(0xFFFFFFFF);

    public void SetPageFullScreen(bool fullScreen)
    {
        // The winui area's window API (it keeps the overlapped presenter, so a maximised window
        // comes back maximised, and tells the shell to hide its chrome).
        if (window is MainWindow main)
        {
            main.SetPageFullScreen(fullScreen);
            return;
        }
        var appWindow = window.AppWindow;
        if (fullScreen)
        {
            if (appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) return;
            presenterBeforeFullScreen = appWindow.Presenter.Kind;
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else if (appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            appWindow.SetPresenter(presenterBeforeFullScreen == AppWindowPresenterKind.FullScreen
                ? AppWindowPresenterKind.Default : presenterBeforeFullScreen);
        }
    }

    public void OpenSettings(string uri) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(uri));

    public bool HasDevTools(BrowserTab tab) => ChromiumPageBridge.Current?.PageOf(tab)?.HasDevTools ?? false;

    public IReadOnlyList<string> ClosePanels()
    {
        // Mosaic's own: the page's dialogs and prompts (pickers close with their window).
        var closed = new List<string>();
        var pages = model.Pages;
        foreach (var dialog in pages.Dialogs.ToList())
        {
            closed.Add($"dialog: {dialog.Message}");
            pages.Finish(dialog, accepted: false);
        }
        return closed;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}
