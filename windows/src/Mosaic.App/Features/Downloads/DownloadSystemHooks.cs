using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Mosaic.App.Features.Downloads;

/// <summary>
/// Notifications and taskbar progress for downloads.
/// </summary>
/// <remarks>
/// TODO(system): once <c>Features/System</c> provides <c>Notifications</c> and the taskbar helper in
/// win-next, replace the bodies of <see cref="ShowNotification"/> and <see cref="ShowTaskbarProgress"/>
/// with calls to them; nothing else in the downloads area talks to these APIs. Until then:
/// app notifications through the Windows App SDK (registered here, lazily, only when a
/// notification is first needed) and <c>ITaskbarList3</c>.
/// </remarks>
internal static class DownloadSystemHooks
{
    private const string ArgumentKey = "mosaic-download";
    private static bool notificationsRegistered;
    private static bool notificationsFailed;
    private static ITaskbarList3? taskbar;
    private static bool taskbarFailed;
    private static (int Active, double? Progress) lastProgress = (0, null);

    /// <summary>Clicking a download notification: the download's ID (on a background thread).</summary>
    public static event Action<Guid>? NotificationClicked;

    /// <summary>What the last notification said and whether Windows took it, for diagnostics.</summary>
    public static string NotificationStatus { get; private set; } = "none";

    public static void ShowNotification(string title, string body, Guid id)
    {
        try
        {
            if (!EnsureNotifications()) return;
            var notification = new AppNotificationBuilder()
                .AddArgument(ArgumentKey, id.ToString())
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            NotificationStatus = notification.Id != 0 ? "shown" : "not shown";
        }
        catch (Exception error)
        {
            NotificationStatus = "failed: " + error.Message;
        }
    }

    private static bool EnsureNotifications()
    {
        if (notificationsRegistered) return true;
        if (notificationsFailed) return false;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                notificationsFailed = true;
                NotificationStatus = "unsupported";
                return false;
            }
            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
            {
                if (args.Arguments.TryGetValue(ArgumentKey, out var value) && Guid.TryParse(value, out var id))
                {
                    NotificationClicked?.Invoke(id);
                }
            };
            AppNotificationManager.Default.Register();
            notificationsRegistered = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { AppNotificationManager.Default.Unregister(); }
                catch (Exception) { }
            };
            return true;
        }
        catch (Exception error)
        {
            // Another part of the app may have registered already; Show still works then.
            notificationsRegistered = error is COMException;
            notificationsFailed = !notificationsRegistered;
            NotificationStatus = "register failed: " + error.Message;
            return notificationsRegistered;
        }
    }

    /// <summary>Last progress shown on the taskbar button, for diagnostics.</summary>
    public static string TaskbarStatus { get; private set; } = "none";

    public static void ShowTaskbarProgress(nint window, int activeCount, double? progress)
    {
        if (window == 0 || taskbarFailed || lastProgress == (activeCount, progress)) return;
        lastProgress = (activeCount, progress);
        try
        {
            if (taskbar is null)
            {
                taskbar = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(TaskbarListClsid)!)!;
                taskbar.HrInit();
            }
            if (activeCount == 0)
            {
                taskbar.SetProgressState(window, TBPF_NOPROGRESS);
                TaskbarStatus = "none";
            }
            else if (progress is { } value)
            {
                taskbar.SetProgressState(window, TBPF_NORMAL);
                taskbar.SetProgressValue(window, (ulong)Math.Round(value * 1000), 1000);
                TaskbarStatus = $"normal {value:0.00}";
            }
            else
            {
                taskbar.SetProgressState(window, TBPF_INDETERMINATE);
                TaskbarStatus = "indeterminate";
            }
        }
        catch (Exception error)
        {
            taskbarFailed = true;
            TaskbarStatus = "failed: " + error.Message;
        }
    }

    private static readonly Guid TaskbarListClsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private const int TBPF_NOPROGRESS = 0;
    private const int TBPF_INDETERMINATE = 1;
    private const int TBPF_NORMAL = 2;

    /// <summary>ITaskbarList3, with the base interfaces' methods first in vtable order.</summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, int flags);
    }
}
