using System.Text.Json.Nodes;

namespace Mosaic.Core.Gecko;

public enum GeckoStatus { Stopped, Starting, Running, Failed }

/// <summary>
/// One Gecko tab as reported by the agent's <c>state()</c>. <c>Blocked</c> is the number of requests
/// the content blocker cancelled since the tab's current page began loading.
/// </summary>
public sealed record GeckoTabState(
    int Id, string Url, string Title, bool Loading, bool CanGoBack, bool CanGoForward, bool Selected, int Blocked = 0);

/// <summary>
/// The agent's periodic report. <see cref="Events"/> are things that happened inside Gecko that
/// Mosaic's UI must react to: "focusAddress", "newTab", "closeTab", "toggleEngine".
/// </summary>
public sealed record GeckoSnapshot(bool Ready, IReadOnlyList<GeckoTabState> Tabs, IReadOnlyList<string> Events);

/// <summary>
/// Mosaic's Mozilla engine: a private Gecko runtime running as a companion process with all of
/// its own UI removed, controlled through <c>agent.sys.mjs</c>.
/// </summary>
/// <remarks>
/// Threading: every event and the <see cref="OnStarted"/> callback are raised on the
/// <see cref="SynchronizationContext"/> that was current when the engine was constructed (the UI
/// thread in the app). Members may be called from that context; they never block it.
/// </remarks>
public interface IGeckoEngine
{
    GeckoStatus Status { get; }

    /// <summary>User-presentable reason when <see cref="Status"/> is <see cref="GeckoStatus.Failed"/>.</summary>
    string? FailureMessage { get; }

    /// <summary>The runtime that is (or would be) used; null if none is installed.</summary>
    GeckoRuntime? Runtime { get; }

    /// <summary>
    /// Process ID of Gecko's main (browser) process while running. This is the ID the agent reports,
    /// which on Windows can differ from the launched process because of Firefox's launcher process.
    /// </summary>
    int? ProcessId { get; }

    /// <summary>
    /// Native handle (HWND) of Gecko's chromeless browser window while running, as reported by the
    /// agent; <see cref="nint.Zero"/> if unknown.
    /// </summary>
    nint WindowHandle { get; }

    event Action? StatusChanged;

    /// <summary>Delivered a few times per second while Gecko runs.</summary>
    event Action<GeckoSnapshot>? SnapshotReceived;

    /// <summary>Gecko went away after having been running (quit, crashed, or was stopped).</summary>
    event Action? Stopped;

    /// <summary>Awaited each time Gecko finishes starting; the place to re-install extensions.</summary>
    Func<Task>? OnStarted { get; set; }

    /// <summary>Starts Gecko if needed. Concurrent callers share one launch. Throws with a
    /// user-presentable message if it cannot start.</summary>
    Task StartAsync();

    /// <summary>Calls a method of the agent (see agent.sys.mjs) and returns its decoded JSON result.</summary>
    /// <exception cref="InvalidOperationException">Gecko is not running.</exception>
    Task<JsonNode?> CallAsync(string method, params object?[] arguments);

    /// <summary>Fire-and-forget <see cref="CallAsync"/> for UI actions; errors are swallowed.</summary>
    void Send(string method, params object?[] arguments);

    /// <summary>Stops Gecko. Safe at any time, including while it is starting.</summary>
    void Stop();

    /// <summary>Stops Gecko and blocks (briefly) until its processes are gone; for app shutdown.</summary>
    void StopAndWait(TimeSpan timeout);
}
