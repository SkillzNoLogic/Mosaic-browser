using System.Text.Json.Nodes;

namespace Mosaic.Core.Gecko;

public sealed class GeckoEngineOptions
{
    /// <summary>Profile directory. Default: <see cref="AppPaths.GeckoProfile"/>.</summary>
    public string ProfileDirectory { get; init; } = AppPaths.GeckoProfile;

    /// <summary>Folder containing <c>agent.sys.mjs</c>. Default: <see cref="AppPaths.GeckoAgent"/>.</summary>
    public string AgentDirectory { get; init; } = AppPaths.GeckoAgent;

    /// <summary>Use this runtime instead of <see cref="GeckoRuntime.Locate"/>.</summary>
    public GeckoRuntime? Runtime { get; init; }

    /// <summary>Extra command-line arguments for Gecko (tests pass <c>--headless</c>).</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>How often the agent's state is polled.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Called on a thread-pool thread with the ID of the process Mosaic launched, immediately after
    /// launch. The app uses it to tie that process's lifetime to its own (a kill-on-close job object).
    /// </summary>
    public Action<int>? ProcessLaunched { get; init; }
}

/// <summary>The real <see cref="IGeckoEngine"/>.</summary>
/// <remarks>
/// Port of <c>Sources/Mosaic/Engines/GeckoEngine.swift</c> minus the macOS window docking
/// (on Windows the app embeds <see cref="WindowHandle"/> directly). Windows specifics: the
/// debugging server listens on a free loopback TCP port; Firefox's launcher process means the
/// launched PID is not necessarily the browser's, so <see cref="ProcessId"/> comes from the agent;
/// an engine left behind by a previous Mosaic that crashed must be terminated before starting.
/// </remarks>
public sealed class GeckoEngine : IGeckoEngine
{
    public GeckoEngine(GeckoEngineOptions? options = null) => throw NotPorted.Yet();

    public GeckoStatus Status => throw NotPorted.Yet();
    public string? FailureMessage => throw NotPorted.Yet();
    public GeckoRuntime? Runtime => throw NotPorted.Yet();
    public int? ProcessId => throw NotPorted.Yet();
    public nint WindowHandle => throw NotPorted.Yet();

    public event Action? StatusChanged;
    public event Action<GeckoSnapshot>? SnapshotReceived;
    public event Action? Stopped;
    public Func<Task>? OnStarted { get; set; }

    public Task StartAsync() => throw NotPorted.Yet();
    public Task<JsonNode?> CallAsync(string method, params object?[] arguments) => throw NotPorted.Yet();
    public void Send(string method, params object?[] arguments) => throw NotPorted.Yet();
    public void Stop() => throw NotPorted.Yet();
    public void StopAndWait(TimeSpan timeout) => throw NotPorted.Yet();

    // Referenced so the contract compiles without warnings until implemented.
    private void Touch() { StatusChanged?.Invoke(); SnapshotReceived?.Invoke(null!); Stopped?.Invoke(); }
}
