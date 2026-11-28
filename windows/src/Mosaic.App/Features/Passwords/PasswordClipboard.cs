using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Mosaic.App.Features.Passwords;

/// <summary>
/// Copies to the clipboard. A secret is kept out of clipboard history (Win+V) and cloud
/// clipboard sync, and cleared after a minute if nothing else was copied meanwhile (as macOS
/// marks it concealed and clears it).
/// </summary>
internal static class PasswordClipboard
{
    private static readonly TimeSpan ClearAfter = TimeSpan.FromMinutes(1);

    public static void Copy(string text, bool secret)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        if (!secret)
        {
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return;
        }
        Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
        Clipboard.Flush();
        // Windows counts every change to the clipboard; if the count moved, someone copied since.
        var ours = GetClipboardSequenceNumber();
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = ClearAfter;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (GetClipboardSequenceNumber() == ours) Clipboard.Clear();
        };
        timer.Start();
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
