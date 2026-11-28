using System.Text.Json.Nodes;

namespace Mosaic.Core.WindowsIntegration;

/// <summary>A button on a notification; clicking it activates with <see cref="Arguments"/>.</summary>
public sealed record NotificationButton(string Label, IReadOnlyDictionary<string, string> Arguments);

/// <summary>
/// A notification to show (Windows app notification / toast).
/// </summary>
/// <param name="Area">The area that posts it ("downloads", "updates", …); activations carry it back as the <c>area</c> argument.</param>
/// <param name="Arguments">Returned when the notification body is clicked (e.g. <c>download=&lt;id&gt;</c>).</param>
/// <param name="Tag">Replaces an earlier notification with the same tag and group (e.g. one per download).</param>
public sealed record NotificationRequest(string Area, string Title, string Body)
{
    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<NotificationButton> Buttons { get; init; } = [];
    public string? Tag { get; init; }
    /// <summary>Shown without sound (progress-style updates).</summary>
    public bool Silent { get; init; }
}

/// <summary>What happened to a posted notification.</summary>
/// <param name="Status">"shown", "recorded" (no presenter: scripted run or unsupported system), "disabled: …" or "failed: …".</param>
/// <param name="Id">The system's notification id when shown.</param>
public sealed record NotificationResult(bool Shown, string Status, uint? Id = null);

/// <summary>A click on a notification or one of its buttons.</summary>
public sealed record NotificationActivation(IReadOnlyDictionary<string, string> Arguments, IReadOnlyDictionary<string, string> UserInput)
{
    public string? Area => Arguments.TryGetValue(UserNotifications.AreaArgument, out var area) ? area : null;
}

/// <summary>Shows notifications on this system (the app's Windows App SDK implementation).</summary>
public interface INotificationPresenter
{
    /// <summary>"ready", "unsupported", "disabled: …", "failed: …".</summary>
    string Status { get; }

    NotificationResult Show(NotificationRequest request);

    void Remove(string area, string tag);
}

/// <summary>
/// Notifications for every area (downloads finished, an update is ready, …), from Core or app
/// code, without a WinUI dependency:
/// <code>
/// UserNotifications.Post(new NotificationRequest("downloads", "Download finished", "report.pdf")
/// {
///     Arguments = new Dictionary&lt;string, string&gt; { ["download"] = id.ToString() },
///     Tag = id.ToString(),
/// });
/// UserNotifications.Activated += activation =&gt;
/// {
///     if (activation.Area == "downloads" &amp;&amp; activation.Arguments.TryGetValue("download", out var id)) …
/// };
/// </code>
/// The presenter is created on first use (registration with Windows happens then, not at
/// startup). Activations that arrive before anyone subscribes (Mosaic launched by a click) are
/// kept and delivered to the first subscriber. <see cref="Activated"/> is raised on the thread
/// that delivered the activation; the app marshals to the UI thread before raising it.
/// </summary>
public static class UserNotifications
{
    /// <summary>Argument carrying <see cref="NotificationRequest.Area"/> back on activation.</summary>
    public const string AreaArgument = "area";

    private const int RecentLimit = 20;
    private static readonly object gate = new();
    private static readonly List<JsonObject> recent = [];
    private static readonly List<NotificationActivation> pending = [];
    private static Func<INotificationPresenter?>? factory;
    private static INotificationPresenter? presenter;
    private static bool created;
    private static Action<NotificationActivation>? activated;

    /// <summary>Set by the app; called once, on the first <see cref="Post"/>.</summary>
    public static Func<INotificationPresenter?>? PresenterFactory
    {
        set { lock (gate) { factory = value; created = false; presenter = null; } }
    }

    /// <summary>The presenter's status, or "not started" before the first post.</summary>
    public static string Status
    {
        get { lock (gate) return created ? presenter?.Status ?? "unsupported" : "not started"; }
    }

    /// <summary>
    /// Creates the presenter now (registering with Windows) without posting: used when Mosaic was
    /// started by a click on a notification, so the click can be delivered. Returns the status.
    /// </summary>
    public static string Start()
    {
        EnsurePresenter();
        return Status;
    }

    private static INotificationPresenter? EnsurePresenter()
    {
        Func<INotificationPresenter?>? create;
        lock (gate)
        {
            if (created) return presenter;
            created = true;
            create = factory;
        }
        // Outside the lock: the presenter may deliver a launch activation while it is created.
        INotificationPresenter? made = null;
        try { made = create?.Invoke(); }
        catch (Exception) { made = null; }
        lock (gate) presenter = made;
        return made;
    }

    public static NotificationResult Post(NotificationRequest request)
    {
        var current = EnsurePresenter();

        NotificationResult result;
        try
        {
            result = current?.Show(request) ?? new NotificationResult(false, "recorded");
        }
        catch (Exception error)
        {
            result = new NotificationResult(false, "failed: " + error.Message);
        }

        lock (gate)
        {
            recent.Add(new JsonObject
            {
                ["area"] = request.Area,
                ["title"] = request.Title,
                ["body"] = request.Body,
                ["tag"] = request.Tag,
                ["status"] = result.Status,
                ["id"] = result.Id,
            });
            if (recent.Count > RecentLimit) recent.RemoveAt(0);
        }
        return result;
    }

    public static void Remove(string area, string tag)
    {
        INotificationPresenter? current;
        lock (gate) current = presenter;
        try { current?.Remove(area, tag); } catch (Exception) { /* nothing to remove */ }
    }

    /// <summary>Raised when the user clicks a notification (see the class remarks for threading).</summary>
    public static event Action<NotificationActivation> Activated
    {
        add
        {
            List<NotificationActivation> flush;
            lock (gate)
            {
                activated += value;
                flush = [.. pending];
                pending.Clear();
            }
            foreach (var activation in flush) value(activation);
        }
        remove { lock (gate) activated -= value; }
    }

    /// <summary>Delivers an activation (the app's presenter calls this).</summary>
    public static void Deliver(NotificationActivation activation)
    {
        Action<NotificationActivation>? handlers;
        lock (gate)
        {
            LastActivation = activation;
            handlers = activated;
            if (handlers is null)
            {
                pending.Add(activation);
                return;
            }
        }
        handlers(activation);
    }

    public static NotificationActivation? LastActivation { get; private set; }

    public static JsonObject State()
    {
        lock (gate)
        {
            var last = new JsonObject();
            if (LastActivation is { } activation)
                foreach (var (key, value) in activation.Arguments) last[key] = value;
            return new JsonObject
            {
                ["status"] = created ? presenter?.Status ?? "unsupported" : "not started",
                ["recent"] = new JsonArray([.. recent.Select(r => (JsonNode)r.DeepClone())]),
                ["lastActivation"] = LastActivation is null ? null : last,
                ["pendingActivations"] = pending.Count,
            };
        }
    }

    /// <summary>For tests: forget everything.</summary>
    internal static void Reset()
    {
        lock (gate)
        {
            recent.Clear();
            pending.Clear();
            factory = null;
            presenter = null;
            created = false;
            activated = null;
            LastActivation = null;
        }
    }
}
