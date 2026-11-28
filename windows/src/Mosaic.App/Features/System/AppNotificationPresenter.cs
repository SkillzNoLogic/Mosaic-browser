using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.App.Features.System;

/// <summary>
/// Windows app notifications (toasts) through Windows App SDK's AppNotificationManager, for an
/// unpackaged app: registers on first use (display name "Mosaic" and Mosaic's icon), routes clicks
/// to <see cref="UserNotifications.Deliver"/> on the UI thread, and unregisters at exit.
/// When the system cannot show app notifications (not supported, registration failed) Mosaic
/// falls back to an in-window notice so the message is not lost; when the user turned Mosaic's
/// notifications off, it respects that and only records.
/// </summary>
internal sealed class AppNotificationPresenter : INotificationPresenter
{
    private readonly DispatcherQueue? dispatcher;
    private readonly Action<string>? fallback;
    private readonly AppNotificationManager? manager;
    private string status;

    private AppNotificationPresenter(DispatcherQueue? dispatcher, Action<string>? fallback)
    {
        this.dispatcher = dispatcher;
        this.fallback = fallback;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                status = "unsupported";
                return;
            }
            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnInvoked;
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Mosaic.png");
            if (File.Exists(icon)) manager.Register("Mosaic", new Uri(icon));
            else manager.Register();
            status = "ready";
            Registered = true;
            DeliverLaunchActivation();
        }
        catch (Exception error)
        {
            manager = null;
            status = "failed: " + error.Message;
        }
    }

    public static bool Registered { get; private set; }

    /// <param name="fallback">Shows a message inside Mosaic's window (UI thread) when toasts are unavailable.</param>
    public static AppNotificationPresenter Create(DispatcherQueue? dispatcher, Action<string>? fallback) => new(dispatcher, fallback);

    public string Status
    {
        get
        {
            if (manager is null) return status;
            try
            {
                return manager.Setting switch
                {
                    AppNotificationSetting.Enabled => "ready",
                    var setting => "disabled: " + setting,
                };
            }
            catch (Exception)
            {
                return status;
            }
        }
    }

    public NotificationResult Show(NotificationRequest request)
    {
        if (manager is null) return Fallback(request, status);
        var current = Status;
        if (current != "ready") return new NotificationResult(false, current);

        var builder = new AppNotificationBuilder().AddText(request.Title);
        if (request.Body.Length > 0) builder.AddText(request.Body);
        builder.AddArgument(UserNotifications.AreaArgument, request.Area);
        foreach (var (key, value) in request.Arguments) builder.AddArgument(key, value);
        foreach (var button in request.Buttons)
        {
            var item = new AppNotificationButton(button.Label).AddArgument(UserNotifications.AreaArgument, request.Area);
            foreach (var (key, value) in button.Arguments) item.AddArgument(key, value);
            builder.AddButton(item);
        }
        if (request.Silent) builder.MuteAudio();
        if (request.Tag is { Length: > 0 } tag) builder.SetTag(tag);
        builder.SetGroup(request.Area);

        var notification = builder.BuildNotification();
        manager.Show(notification);
        return notification.Id != 0
            ? new NotificationResult(true, "shown", notification.Id)
            : new NotificationResult(false, "failed: the system did not accept the notification");
    }

    public void Remove(string area, string tag) => _ = manager?.RemoveByTagAndGroupAsync(tag, area);

    /// <summary>Unregisters the click handler (Windows then starts a new Mosaic for clicks).</summary>
    public static void Shutdown()
    {
        if (!Registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch (Exception) { /* exiting anyway */ }
        Registered = false;
    }

    private NotificationResult Fallback(NotificationRequest request, string reason)
    {
        if (fallback is null) return new NotificationResult(false, "recorded (" + reason + ")");
        var text = request.Body.Length > 0 ? $"{request.Title}: {request.Body}" : request.Title;
        if (dispatcher is null || dispatcher.HasThreadAccess) fallback(text);
        else dispatcher.TryEnqueue(() => fallback(text));
        return new NotificationResult(false, "in-app notice (" + reason + ")");
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Deliver(args);

    /// <summary>Mosaic started by a click on a toast: the activation is in AppInstance, not an event.</summary>
    private void DeliverLaunchActivation()
    {
        try
        {
            var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activated.Kind == ExtendedActivationKind.AppNotification && activated.Data is AppNotificationActivatedEventArgs args)
                Deliver(args);
        }
        catch (Exception)
        {
            // Not activated by a notification, or AppLifecycle unavailable.
        }
    }

    private void Deliver(AppNotificationActivatedEventArgs args)
    {
        var activation = new NotificationActivation(
            new Dictionary<string, string>(args.Arguments),
            args.UserInput is { } input ? new Dictionary<string, string>(input) : new Dictionary<string, string>());
        if (dispatcher is null || dispatcher.HasThreadAccess) UserNotifications.Deliver(activation);
        else dispatcher.TryEnqueue(() =>
        {
            UserNotifications.Deliver(activation);
            SystemIntegration.BringToFront();
        });
    }
}
