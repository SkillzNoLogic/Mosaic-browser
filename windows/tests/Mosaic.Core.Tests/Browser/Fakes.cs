using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mosaic.Core.Browser;
using Mosaic.Core.Extensions;
using Mosaic.Core.Gecko;
using Mosaic.Core.Search;

namespace Mosaic.Core.Tests.Browser;

internal sealed record GeckoCall(string Method, object?[] Arguments)
{
    public override string ToString() => $"{Method}({string.Join(", ", Arguments)})";
}

/// <summary>A scripted Gecko: records every agent call and reports whatever a test tells it to.</summary>
internal sealed class FakeGeckoEngine : IGeckoEngine
{
    private readonly List<(GeckoCall Call, TaskCompletionSource<JsonNode?> Completion)> heldCalls = [];
    private readonly HashSet<string> heldMethods = [];
    private Task? starting;
    private int nextTabId = 1;

    public GeckoStatus Status { get; private set; }
    public string? FailureMessage { get; private set; }
    public GeckoRuntime? Runtime { get; set; } = new(@"C:\Engines\Firefox\firefox.exe", "Firefox", "140.5.0", IsManaged: true);
    public int? ProcessId => Status == GeckoStatus.Running ? 4242 : null;
    public nint WindowHandle => Status == GeckoStatus.Running ? 0x1234 : 0;

    public event Action? StatusChanged;
    public event Action<GeckoSnapshot>? SnapshotReceived;
    public event Action? Stopped;
    public Func<Task>? OnStarted { get; set; }

    /// <summary>Every agent call made while running, in order, whether awaited or sent.</summary>
    public List<GeckoCall> Calls { get; } = [];
    public int StartCount { get; private set; }
    public int StopAndWaitCount { get; private set; }

    /// <summary>When set, starting fails with this.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>When set, starting does not finish until the test completes it.</summary>
    public TaskCompletionSource? StartGate { get; set; }

    /// <summary>Custom results per agent method; anything not listed gets a plausible default.</summary>
    public Dictionary<string, Func<object?[], JsonNode?>> Handlers { get; } = [];

    public Task StartAsync()
    {
        if (Status == GeckoStatus.Running) return Task.CompletedTask;
        if (starting is { IsCompleted: false }) return starting;
        starting = LaunchAsync();
        return starting;
    }

    private async Task LaunchAsync()
    {
        StartCount++;
        SetStatus(GeckoStatus.Starting);
        if (StartGate is { } gate) await gate.Task;
        if (StartFailure is { } failure)
        {
            FailureMessage = failure.Message;
            SetStatus(GeckoStatus.Failed);
            throw failure;
        }
        SetStatus(GeckoStatus.Running);
        if (OnStarted is { } started) await started();
    }

    public Task<JsonNode?> CallAsync(string method, params object?[] arguments)
    {
        if (Status != GeckoStatus.Running)
        {
            return Task.FromException<JsonNode?>(new InvalidOperationException("Gecko is not running."));
        }
        var call = new GeckoCall(method, arguments);
        Calls.Add(call);
        if (heldMethods.Contains(method))
        {
            var completion = new TaskCompletionSource<JsonNode?>();
            heldCalls.Add((call, completion));
            return completion.Task;
        }
        try
        {
            return Task.FromResult(Respond(method, arguments));
        }
        catch (Exception error)
        {
            return Task.FromException<JsonNode?>(error);
        }
    }

    public void Send(string method, params object?[] arguments)
    {
        if (Status == GeckoStatus.Running) Calls.Add(new GeckoCall(method, arguments));
    }

    public void Stop()
    {
        var wasRunning = Status == GeckoStatus.Running;
        SetStatus(GeckoStatus.Stopped);
        if (wasRunning) Stopped?.Invoke();
    }

    public void StopAndWait(TimeSpan timeout)
    {
        StopAndWaitCount++;
        Stop();
    }

    // ---- Scripting ----

    /// <summary>Calls to <paramref name="method"/> stay pending until <see cref="Release"/> or <see cref="Fail"/>.</summary>
    public void Hold(string method) => heldMethods.Add(method);

    /// <summary>Completes the oldest pending call to <paramref name="method"/> with its normal result.</summary>
    public void Release(string method)
    {
        var (call, completion) = TakeHeld(method);
        completion.SetResult(Respond(method, call.Arguments));
    }

    public void Fail(string method, Exception error) => TakeHeld(method).Completion.SetException(error);

    public IReadOnlyList<object?[]> CallsTo(string method) =>
        Calls.Where(call => call.Method == method).Select(call => call.Arguments).ToList();

    public void Report(params GeckoTabState[] tabs) => SnapshotReceived?.Invoke(new GeckoSnapshot(true, tabs, []));

    public void ReportEvents(GeckoTabState[] tabs, params string[] events) =>
        SnapshotReceived?.Invoke(new GeckoSnapshot(true, tabs, events));

    /// <summary>Gecko quits underneath Mosaic.</summary>
    public void Crash() => Stop();

    private (GeckoCall Call, TaskCompletionSource<JsonNode?> Completion) TakeHeld(string method)
    {
        var index = heldCalls.FindIndex(held => held.Call.Method == method);
        Assert.True(index >= 0, $"No pending call to {method}.");
        var held = heldCalls[index];
        heldCalls.RemoveAt(index);
        return held;
    }

    private JsonNode? Respond(string method, object?[] arguments)
    {
        if (Handlers.TryGetValue(method, out var handler)) return handler(arguments);
        return method switch
        {
            "openTab" or "openExtensionPage" => JsonValue.Create(nextTabId++),
            "installExtension" => new JsonObject { ["active"] = true },
            _ => JsonValue.Create(true),
        };
    }

    private void SetStatus(GeckoStatus status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke();
    }
}

internal static class Snapshots
{
    public static GeckoTabState Tab(
        int id, string url, string title = "", bool loading = false, bool canGoBack = false,
        bool canGoForward = false, bool selected = false, int blocked = 0) =>
        new(id, url, title, loading, canGoBack, canGoForward, selected, blocked);
}

internal sealed class FakeChromiumBrowser(string initialUrl) : IChromiumBrowser
{
    public bool IsShown { get; set; }
    public bool IsClosed { get; private set; }
    public List<string> Loads { get; } = [initialUrl];
    public List<string> Actions { get; } = [];

    public event Action<string>? TitleChanged;
    public event Action<string>? UrlChanged;
    public event Action<ChromiumLoadState>? LoadStateChanged;
    public event Action<string>? NewTabRequested;
    public event Action<int>? BlockedCountChanged;
    public event Action<string>? Failed;

    public void Load(string url) => Loads.Add(url);
    public void GoBack() => Actions.Add("back");
    public void GoForward() => Actions.Add("forward");
    public void Reload() => Actions.Add("reload");
    public void Stop() => Actions.Add("stop");
    public void Focus() => Actions.Add("focus");
    public void Close() => IsClosed = true;

    public void RaiseTitle(string title) => TitleChanged?.Invoke(title);
    public void RaiseUrl(string url) => UrlChanged?.Invoke(url);
    public void RaiseLoadState(bool loading, bool canGoBack = false, bool canGoForward = false) =>
        LoadStateChanged?.Invoke(new ChromiumLoadState(loading, canGoBack, canGoForward));
    public void RaiseNewTab(string url) => NewTabRequested?.Invoke(url);
    public void RaiseBlockedCount(int count) => BlockedCountChanged?.Invoke(count);
    public void RaiseFailed(string reason) => Failed?.Invoke(reason);
}

internal sealed class FakeChromiumEngine : IChromiumEngine
{
    public bool IsAvailable { get; set; } = true;
    public string? UnavailableReason { get; set; }
    public List<FakeChromiumBrowser> Created { get; } = [];

    public IChromiumBrowser CreateBrowser(string url)
    {
        Assert.True(IsAvailable, "The model must not create browsers while Chromium is unavailable.");
        var browser = new FakeChromiumBrowser(url);
        Created.Add(browser);
        return browser;
    }
}

/// <summary>
/// An in-memory extension library. A "package" is UTF-8 text <c>name|version</c>; the ID is the
/// one the caller knows, or <see cref="FileId"/> for packages that arrive without one.
/// </summary>
internal sealed class FakeExtensionLibrary : IExtensionLibrary
{
    public const string FileId = "ffffffffffffffffffffffffffffffff";

    private readonly List<InstalledExtension> installed = [];

    public Exception? InstallFailure { get; set; }
    public Exception? RemoveFailure { get; set; }
    public int? InstallThread { get; private set; }

    public IReadOnlyList<InstalledExtension> Extensions =>
        [.. installed.OrderBy(extension => extension.Name, StringComparer.OrdinalIgnoreCase)];

    public string GeckoDirectoryFor(string id) => $"/library/{id}/gecko";
    public string ChromiumDirectoryFor(string id) => $"/library/{id}/chromium";

    public string? IconFileFor(InstalledExtension extension) =>
        extension.IconPath is { } icon ? $"{GeckoDirectoryFor(extension.Id)}/{icon}" : null;

    public InstalledExtension Install(byte[] package, string? knownId)
    {
        InstallThread = Environment.CurrentManagedThreadId;
        if (InstallFailure is { } failure) throw failure;
        var fields = Encoding.UTF8.GetString(package).Split('|');
        return Add(knownId ?? FileId, fields[0], fields.ElementAtOrDefault(1) ?? "1.0");
    }

    /// <summary>Puts an extension in the library directly, as if installed in an earlier run.</summary>
    public InstalledExtension Add(
        string id, string name, string version = "1.0", bool enabled = true,
        IReadOnlyList<CompatibilityNote>? notes = null, bool hasPopup = false, bool hasOptions = false)
    {
        var existing = installed.FindIndex(extension => extension.Id == id);
        var record = new InstalledExtension
        {
            Id = id,
            GeckoId = ManifestConverter.GeckoIdFor(id),
            Name = name,
            Version = version,
            ManifestVersion = 3,
            // Replacing an installed extension keeps its enabled state.
            Enabled = existing >= 0 ? installed[existing].Enabled : enabled,
            Notes = notes ?? [],
            IconPath = "icon.png",
            HasPopup = hasPopup,
            HasOptions = hasOptions,
        };
        if (existing >= 0) installed[existing] = record;
        else installed.Add(record);
        return record;
    }

    public void Remove(string id)
    {
        if (RemoveFailure is { } failure) throw failure;
        installed.RemoveAll(extension => extension.Id == id);
    }

    public void SetEnabled(bool enabled, string id)
    {
        var index = installed.FindIndex(extension => extension.Id == id);
        if (index >= 0) installed[index] = installed[index] with { Enabled = enabled };
    }
}

/// <summary>A Chromium profile that identifies an extension by <c>cr:&lt;folder&gt;</c>.</summary>
internal sealed class FakeChromiumExtensionHost : IChromiumExtensionHost
{
    public bool IsReady { get; private set; }
    public Func<Task>? OnReady { get; set; }

    /// <summary>What the profile holds: Chromium ID → folder it was loaded from.</summary>
    public Dictionary<string, string> Profile { get; } = [];
    public List<string> Loads { get; } = [];
    public List<string> Unloads { get; } = [];
    public HashSet<string> RefusedFolders { get; } = [];
    public bool ListFails { get; set; }

    public static string IdFor(string folder) => $"cr:{folder}";

    /// <summary>The first Chromium browser came up: the profile exists and the manager is told.</summary>
    public async Task BecomeReadyAsync()
    {
        IsReady = true;
        if (OnReady is { } ready) await ready();
    }

    public Task<IReadOnlyCollection<string>> ListAsync() =>
        ListFails
            ? Task.FromException<IReadOnlyCollection<string>>(new InvalidOperationException("The profile is gone."))
            : Task.FromResult<IReadOnlyCollection<string>>(Profile.Keys.ToList());

    public Task<string> LoadAsync(string folder)
    {
        Loads.Add(folder);
        if (RefusedFolders.Contains(folder))
        {
            return Task.FromException<string>(new InvalidOperationException("Manifest version 2 is not supported."));
        }
        Profile[IdFor(folder)] = folder;
        return Task.FromResult(IdFor(folder));
    }

    public Task UnloadAsync(string chromiumId)
    {
        Unloads.Add(chromiumId);
        Profile.Remove(chromiumId);
        return Task.CompletedTask;
    }
}

/// <summary>Answers Chrome Web Store downloads from a script instead of the network.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    /// <summary>The response to every request; by default a package named "Downloaded".</summary>
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
        _ => Task.FromResult(Package("Downloaded|2.0"));

    public static HttpResponseMessage Package(string text) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Respond(request);
    }
}

/// <summary>
/// Stand-ins for the address and Web Store rules, which belong to another part of the code base
/// and are tested there. Simple enough to predict in a test, close enough to behave like the real ones.
/// </summary>
internal static partial class TestRules
{
    public const string DarkReader = "eimadpbcbfnmbkopoojfekhnkhdbieeh";

    public static BrowserRules Create() => new()
    {
        ResolveAddress = Resolve,
        IsAddress = IsAddress,
        WebStoreExtensionId = ExtensionId,
        WebStoreDownloadUrl = id => new Uri($"https://store.test/crx/{id}"),
        NextSearchEngine = engine =>
            SearchEngine.BuiltIn[(SearchEngine.BuiltIn.ToList().IndexOf(engine) + 1) % SearchEngine.BuiltIn.Count],
    };

    private static bool IsAddress(string input) =>
        input.Contains("://", StringComparison.Ordinal) || (input.Contains('.') && !input.Contains(' '));

    private static string? Resolve(string input, SearchEngine engine)
    {
        var text = input.Trim();
        if (text.Length == 0) return null;
        if (!IsAddress(text)) return $"https://search.test/{engine.Id}?q={Uri.EscapeDataString(text)}";
        return text.Contains("://", StringComparison.Ordinal) ? text : $"https://{text}";
    }

    private static string? ExtensionId(string input) =>
        ExtensionIdPattern().Match(input) is { Success: true } match ? match.Value : null;

    [GeneratedRegex("(?<![a-p])[a-p]{32}(?![a-p])")]
    private static partial Regex ExtensionIdPattern();
}
