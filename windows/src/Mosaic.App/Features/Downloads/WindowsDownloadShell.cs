using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Mosaic.Core.Downloads;
using Windows.ApplicationModel.DataTransfer;

namespace Mosaic.App.Features.Downloads;

/// <summary>
/// The download manager's view of Windows: opening files with their apps, File Explorer, the
/// Recycle Bin, the clipboard, "Download finished" notifications and taskbar progress.
/// </summary>
/// <remarks>
/// Notifications and the taskbar go through <see cref="DownloadSystemHooks"/>, the one place to
/// switch to the system area's <c>Notifications</c> / taskbar helpers once they are in win-next.
/// </remarks>
internal sealed class WindowsDownloadShell(nint window, bool isScripted, DispatcherQueue dispatcher) : IDownloadShell
{
    public bool IsScripted { get; } = isScripted;

    public bool IsAppActive
    {
        get
        {
            var foreground = NativeMethods.GetForegroundWindow();
            return foreground != 0 && (foreground == window || NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOTOWNER) == window);
        }
    }

    public void OpenFile(string path) => Launch(new ProcessStartInfo(path) { UseShellExecute = true });

    public void RevealFile(string path) =>
        Launch(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });

    public void OpenFolder(string path) => Launch(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });

    public void MoveToRecycleBin(string path) => _ = Task.Run(() =>
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
        }
    });

    public void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    public void ShowNotification(string title, string body, Guid id)
    {
        if (IsScripted) return; // scripted runs record it (dump's lastNotification) and show nothing
        DownloadSystemHooks.ShowNotification(title, body, id);
    }

    public void ShowProgress(int activeCount, double? progress) =>
        dispatcher.TryEnqueue(() => DownloadSystemHooks.ShowTaskbarProgress(window, activeCount, progress));

    private static void Launch(ProcessStartInfo start) => _ = Task.Run(() =>
    {
        try
        {
            using var _ = Process.Start(start);
        }
        catch (Exception error) when (error is global::System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
        }
    });
}

internal static partial class NativeMethods
{
    public const uint GA_ROOTOWNER = 3;

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint processId);

    public const uint ASFW_ANY = unchecked((uint)-1);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);

    /// <summary>FOLDERID_Downloads.</summary>
    public static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The Downloads known folder (where the user may have moved it), or null.</summary>
    public static string? KnownDownloadsFolder()
    {
        if (SHGetKnownFolderPath(DownloadsFolder, 0, 0, out var pointer) != 0) return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }
}
