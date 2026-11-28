using Mosaic.Core.Browser;
using Mosaic.Core.Extensions;

namespace Mosaic.Core.Platform;

/// <summary>Replaces parts of the platform services (tests; the app's language list). Set before first use.</summary>
public sealed class PlatformOptions
{
    public GeckoInstallerOptions? Installer { get; init; }
    public UpdaterOptions? Updater { get; init; }

    /// <summary>Windows' preferred languages, most preferred first (the app reads them from the system).</summary>
    public Func<IReadOnlyList<string>>? SystemLanguages { get; init; }
}

/// <summary>
/// The platform area's services: the Gecko runtime installer, per-site engine rules, web-content
/// languages, Mosaic's update check and updater, and safe engine switching. Owned by
/// <see cref="BrowserModel.Platform"/>. Port of <c>PlatformServices.swift</c>.
/// </summary>
/// <remarks>
/// Everything is created on first use, so none of it is on Mosaic's start-up path: the model only
/// allocates this object while it is constructed.
/// </remarks>
public sealed class PlatformServices
{
    private PlatformOptions options = new();
    private PlatformStore? store;
    private EngineRuleStore? rules;
    private LanguageSettings? languages;
    private GeckoRuntimeInstaller? runtime;
    private MosaicUpdater? updater;
    private UpdateChecker? updates;

    internal PlatformServices(string? dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    /// <summary>
    /// The folder holding the model's <c>settings.json</c> (<c>%LOCALAPPDATA%\Mosaic</c> in the app);
    /// everything the platform area keeps lives under it. Null when it could not be determined, in
    /// which case preferences stay in memory and nothing is written.
    /// </summary>
    public string? DataDirectory { get; }

    /// <summary>Options for the services; must be set before they are first used.</summary>
    public PlatformOptions Options
    {
        get => options;
        set
        {
            if (runtime is not null || updater is not null || languages is not null)
            {
                throw new InvalidOperationException("Platform options must be set before the services are first used.");
            }
            options = value;
        }
    }

    public PlatformStore Store => store ??= new PlatformStore(DataDirectory is null ? null : Path.Combine(DataDirectory, PlatformStore.FileName));

    public EngineRuleStore Rules => rules ??= new EngineRuleStore(Store);

    public LanguageSettings Languages => languages ??= CreateLanguages();

    public EngineSwitchTracker Switches { get; } = new();

    /// <summary>The installer, connected to the model's engine.</summary>
    public GeckoRuntimeInstaller Runtime => runtime ??= CreateRuntime();

    public MosaicUpdater Updater => updater ??= new MosaicUpdater(Store, UpdaterOptions);

    public UpdateChecker Updates => updates ??= new UpdateChecker(Store, Updater, Runtime, UpdaterOptions);

    /// <summary>Set by <see cref="BrowserModel.ActivatePlatform"/> when the app starts.</summary>
    public bool IsActive { get; internal set; }

    /// <summary>Whether the installer, updater or checker exist yet (dump avoids creating them in tests).</summary>
    public bool HasRuntime => runtime is not null;
    public bool HasUpdater => updater is not null;

    /// <summary>
    /// Keep the page on screen until the other engine has loaded it. Off in a bare model (the
    /// winui area's tests describe the direct switch); the app turns it on as it starts.
    /// </summary>
    public bool SafeEngineSwitching { get; set; }

    /// <summary>The extension library's folder, for reading permissions.</summary>
    public string ExtensionsDirectory => DataDirectory is null ? AppPaths.Extensions : Path.Combine(DataDirectory, "Extensions");

    /// <summary>What <paramref name="extension"/> asked for, from its converted manifest.</summary>
    public ExtensionPermissions PermissionsOf(InstalledExtension extension) =>
        ExtensionPermissions.Read(Path.Combine(ExtensionsDirectory, extension.Id, "gecko"));

    private UpdaterOptions UpdaterOptions => options.Updater ?? new UpdaterOptions
    {
        DownloadDirectory = Path.Combine(DataDirectory ?? AppPaths.DataRoot, "Updates"),
    };

    /// <summary>The model's side of the installer (its engine and tabs); set by the model.</summary>
    internal Func<GeckoRuntimeHost>? RuntimeHost { get; set; }

    /// <summary>Raised when the language list changes (the model passes it to Gecko).</summary>
    internal event Action? LanguagesChanged;

    private GeckoRuntimeInstaller CreateRuntime()
    {
        var installer = new GeckoRuntimeInstaller(Store, options.Installer ?? new GeckoInstallerOptions
        {
            EngineDirectory = DataDirectory is null ? AppPaths.ManagedGecko : Path.Combine(DataDirectory, "Engines", "Firefox"),
        });
        if (RuntimeHost is { } host) installer.Host = host();
        return installer;
    }

    private LanguageSettings CreateLanguages()
    {
        var created = new LanguageSettings(Store, options.SystemLanguages);
        created.Changed += () => LanguagesChanged?.Invoke();
        return created;
    }
}

/// <summary>
/// Tabs being moved to the other engine. While a tab is listed it still shows its page in the old
/// engine; the other engine is loading the same address out of sight. Port of <c>EngineSwitchTracker</c>.
/// </summary>
public sealed class EngineSwitchTracker : ObservableObject
{
    public sealed record Pending(EngineKind Target, string Url, DateTimeOffset Started, CancellationTokenSource Cancellation);

    private readonly Dictionary<Guid, Pending> pending = [];
    private string? lastOutcome;

    public IReadOnlyDictionary<Guid, Pending> All => pending;

    /// <summary>What happened to the most recent switch, for automation and diagnostics.</summary>
    public string? LastOutcome { get => lastOutcome; private set => Set(ref lastOutcome, value); }

    public EngineKind? TargetOf(BrowserTab tab) => pending.TryGetValue(tab.Id, out var entry) ? entry.Target : null;

    internal void Begin(BrowserTab tab, Pending entry)
    {
        if (pending.Remove(tab.Id, out var previous)) previous.Cancellation.Cancel();
        pending[tab.Id] = entry;
        LastOutcome = $"switching to {entry.Target.Id()}";
        Notify(nameof(All));
    }

    internal void End(BrowserTab tab, string outcome)
    {
        pending.Remove(tab.Id);
        LastOutcome = outcome;
        Notify(nameof(All));
    }

    internal void Cancel(BrowserTab tab)
    {
        if (!pending.Remove(tab.Id, out var entry)) return;
        entry.Cancellation.Cancel();
        LastOutcome = "cancelled";
        Notify(nameof(All));
    }
}
