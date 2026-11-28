using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Platform;

/// <summary>Where Mosaic's own update stands.</summary>
public abstract record UpdatePhase
{
    public static readonly UpdatePhase Idle = new IdlePhase();

    public sealed record IdlePhase : UpdatePhase;
    public sealed record Downloading(string Version, double? Fraction) : UpdatePhase;
    public sealed record Verifying(string Version) : UpdatePhase;
    /// <summary>Downloaded and verified; installs when Mosaic quits or on Restart to Update.</summary>
    public sealed record Ready(string Version) : UpdatePhase;
    public sealed record Failed(string Message) : UpdatePhase;

    public bool IsBusy => this is Downloading or Verifying;

    public string Name => this switch
    {
        Downloading => "downloading",
        Verifying => "verifying",
        Ready => "ready",
        Failed => "failed",
        _ => "idle",
    };

    public string Detail => this switch
    {
        Downloading d => $"{d.Version} {(d.Fraction is { } f ? f.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "?")}",
        Verifying v => v.Version,
        Ready r => r.Version,
        Failed f => f.Message,
        _ => "",
    };
}

/// <summary>Where Mosaic is installed and how it runs, for the updater. Tests point it at throwaway folders.</summary>
public sealed class UpdaterOptions
{
    /// <summary>The folder holding Mosaic.exe (the one that gets replaced).</summary>
    public string InstallDirectory { get; init; } = AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>Where downloads are kept until verified: <c>%LOCALAPPDATA%\Mosaic\Updates</c>.</summary>
    public string DownloadDirectory { get; init; } = Path.Combine(AppPaths.DataRoot, "Updates");

    public string CurrentVersion { get; init; } = MosaicVersion.Current;

    public MachineArchitecture Architecture { get; init; } = MachineArchitectures.Process;

    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Environment lookup, for the test feed (<c>MOSAIC_UPDATE_API</c>).</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Mosaic's process and the arguments to relaunch it with.</summary>
    public int ProcessId { get; init; } = System.Environment.ProcessId;

    public IReadOnlyList<string> Arguments { get; init; } = System.Environment.GetCommandLineArgs().Skip(1).ToArray();

    /// <summary>Starts the helper; returns false if it could not. Tests capture the request instead.</summary>
    public Func<UpdatePackage.HelperRequest, bool>? LaunchHelper { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The release key. Only tests (through <c>InternalsVisibleTo</c>) can set another one.</summary>
    internal string PublicKey { get; init; } = UpdatePackage.SigningPublicKey;
}

/// <summary>
/// Downloads, verifies and installs new Mosaic releases (Settings › About &amp; Updates). Windows
/// counterpart of <c>Sources/Mosaic/Features/Platform/MosaicUpdater.swift</c>.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><see cref="UpdateChecker"/> finds a newer release on GitHub.</item>
/// <item><see cref="Download"/> fetches the release's zip for this architecture and its <c>.sig</c>,
/// checks the Ed25519 signature against Mosaic's release key (<see cref="UpdatePackage"/>; anything
/// else is discarded), unpacks it into a hidden folder beside the install folder (same volume, so
/// the final step is a rename), and checks that it is Mosaic, of that version, newer than this one,
/// built for this processor.</item>
/// <item>The update is installed when Mosaic quits (<see cref="InstallIfReady"/>, called as the
/// window closes), or at once with <see cref="RestartToUpdate"/>: a helper waits for Mosaic and its
/// child processes to exit, swaps the folders (restoring the old one if anything fails) and, for a
/// restart, starts the new version.</item>
/// </list>
/// With automatic updates on, a newly found release is downloaded in the background and installed
/// the next time Mosaic quits. No GitHub token is needed: the repository is public.
/// </remarks>
public sealed class MosaicUpdater : ObservableObject
{
    public const string AutomaticKey = "updateAutoInstall";

    private readonly PlatformStore store;
    private readonly UpdaterOptions options;
    private readonly HttpClient http;
    private UpdatePhase phase = UpdatePhase.Idle;
    private bool? automatic;
    private UpdateLocationProblem? locationProblem;
    private bool locationAssessed;
    private string? stagedFolder;
    private CancellationTokenSource? cancellation;
    private bool relaunchAfterInstall;
    private bool helperLaunched;
    private Task work = Task.CompletedTask;

    public MosaicUpdater(PlatformStore store, UpdaterOptions? options = null)
    {
        this.store = store;
        this.options = options ?? new UpdaterOptions();
        http = this.options.HttpHandler is { } handler ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mosaic", this.options.CurrentVersion));
    }

    /// <summary>Asks the app to quit (Restart to Update); the app closes its window, which calls <see cref="InstallIfReady"/>.</summary>
    public event Action? QuitRequested;

    /// <summary>Set by <see cref="UpdateChecker"/>: turning automatic updates on implies daily checks.</summary>
    internal UpdateChecker? Checker { get; set; }

    public UpdatePhase Phase { get => phase; private set => Set(ref phase, value); }

    public string CurrentVersion => options.CurrentVersion;

    public string InstallDirectory => options.InstallDirectory;

    public MachineArchitecture Architecture => options.Architecture;

    /// <summary>Download and install new releases without asking.</summary>
    public bool Automatic
    {
        get => automatic ??= store.GetBool(AutomaticKey) ?? false;
        set
        {
            if (Automatic == value) return;
            automatic = value;
            store.Set(AutomaticKey, value);
            Notify();
            if (value && Checker is { Frequency: UpdateCheckFrequency.Manual } checker) checker.Frequency = UpdateCheckFrequency.Daily;
        }
    }

    /// <summary>Why this copy of Mosaic cannot replace itself, or null if it can. Checked on first use (it touches the disk).</summary>
    public UpdateLocationProblem? LocationProblem
    {
        get
        {
            if (!locationAssessed)
            {
                locationAssessed = true;
                locationProblem = AssessLocation(options.InstallDirectory);
            }
            return locationProblem;
        }
    }

    /// <summary>The verified, unpacked release waiting to be installed.</summary>
    public string? StagedFolder => stagedFolder;

    /// <summary>The current download, for tests and automation.</summary>
    public Task Work => work;

    public string StagingFolderFor(string version) =>
        Path.Combine(Path.GetDirectoryName(options.InstallDirectory) ?? options.InstallDirectory,
            $".{Path.GetFileName(options.InstallDirectory)}.update-{version}");

    public string HelperLogPath => Path.Combine(options.DownloadDirectory, "install.log");

    private static UpdateLocationProblem? AssessLocation(string installDirectory)
    {
        var hasExecutable = File.Exists(Path.Combine(installDirectory, UpdatePackage.ExecutableName));
        return UpdatePackage.LocationProblem(installDirectory, Path.GetTempPath(), hasExecutable,
            hasExecutable && CanWrite(installDirectory) && Path.GetDirectoryName(installDirectory) is { } parent && CanWrite(parent));
    }

    private static bool CanWrite(string folder)
    {
        var probe = Path.Combine(folder, $".mosaic-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- Download and verify ----

    /// <summary>Called by <see cref="UpdateChecker"/> when it finds a release newer than this one.</summary>
    public void ReleaseFound(Release release)
    {
        if (!Automatic || LocationProblem is not null) return;
        if (phase is UpdatePhase.Ready ready && ready.Version == release.Version.NumberString) return;
        if (phase.IsBusy) return;
        Download(release);
    }

    public void Download(Release release)
    {
        if (phase.IsBusy) return;
        if (LocationProblem is { } problem)
        {
            Phase = new UpdatePhase.Failed(problem.Message);
            return;
        }
        if (ReleaseCheck.UpdateAssets(release.Assets, release.Version, options.Architecture) is not { } assets)
        {
            Phase = new UpdatePhase.Failed($"Mosaic {release.Version.NumberString} has no signed update for Windows ({options.Architecture.Id()}). Download it from the releases page instead.");
            return;
        }
        var version = release.Version.NumberString;
        Phase = new UpdatePhase.Downloading(version, 0);
        var source = new CancellationTokenSource();
        cancellation = source;
        work = RunAsync(release, assets.Zip, assets.Signature, source.Token);
    }

    public void Cancel() => cancellation?.Cancel();

    private async Task RunAsync(Release release, ReleaseAsset zip, ReleaseAsset signature, CancellationToken token)
    {
        var version = release.Version.NumberString;
        var downloads = Path.Combine(options.DownloadDirectory, version);
        try
        {
            var staged = await FetchAndVerifyAsync(release, zip, signature, downloads, token);
            stagedFolder = staged;
            Phase = new UpdatePhase.Ready(version);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Phase = UpdatePhase.Idle;
        }
        catch (UpdateException error)
        {
            Phase = new UpdatePhase.Failed(error.Message);
        }
        catch (HttpRequestException error)
        {
            Phase = new UpdatePhase.Failed($"The update could not be downloaded ({error.Message}).");
        }
        catch (Exception error)
        {
            Phase = new UpdatePhase.Failed($"The update could not be prepared ({error.Message}).");
        }
        finally
        {
            // The zip and signature are not needed once unpacked (or once rejected).
            await Task.Run(() => GeckoRuntimeInstaller.DeleteQuietly(downloads), CancellationToken.None);
        }
    }

    private async Task<string> FetchAndVerifyAsync(Release release, ReleaseAsset zip, ReleaseAsset signature, string folder, CancellationToken token)
    {
        var version = release.Version.NumberString;
        var context = SynchronizationContext.Current;
        await Task.Run(() =>
        {
            GeckoRuntimeInstaller.DeleteQuietly(folder);
            Directory.CreateDirectory(folder);
        }, token);

        var signatureFile = Path.Combine(folder, signature.Name);
        await DownloadAsync(signature, signatureFile, null, token);
        var signatureText = await File.ReadAllTextAsync(signatureFile, token);

        var zipFile = Path.Combine(folder, zip.Name);
        await DownloadAsync(zip, zipFile, fraction =>
        {
            void Apply()
            {
                if (phase is UpdatePhase.Downloading) Phase = new UpdatePhase.Downloading(version, fraction);
            }
            if (context is null) Apply();
            else context.Post(_ => Apply(), null);
        }, token);
        token.ThrowIfCancellationRequested();

        Phase = new UpdatePhase.Verifying(version);
        var publicKey = options.PublicKey;
        var signed = await Task.Run(() => UpdatePackage.VerifyFile(zipFile, signatureText, publicKey), token);
        if (!signed) throw new UpdateException("The download is not signed with Mosaic’s release key, so it was discarded.");

        var staged = StagingFolderFor(version);
        var current = SoftwareVersion.Parse(options.CurrentVersion);
        var architecture = options.Architecture;
        await Task.Run(() =>
        {
            GeckoRuntimeInstaller.DeleteQuietly(staged);
            try
            {
                // ExtractToDirectory refuses entries that would land outside the folder.
                ZipFile.ExtractToDirectory(zipFile, staged);
            }
            catch (Exception error) when (error is InvalidDataException or IOException)
            {
                GeckoRuntimeInstaller.DeleteQuietly(staged);
                throw new UpdateException($"The update could not be unpacked ({error.Message}).");
            }
            try
            {
                File.SetAttributes(staged, File.GetAttributes(staged) | FileAttributes.Hidden);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            var root = PackageRoot(staged);
            if (root != staged)
            {
                // A zip with one top-level folder: keep that folder's content as the install folder.
                var flattened = staged + ".flat";
                Directory.Move(root, flattened);
                GeckoRuntimeInstaller.DeleteQuietly(staged);
                Directory.Move(flattened, staged);
            }
            CheckPackage(staged, release.Version, current, architecture);
        }, token);
        return staged;
    }

    /// <summary>The folder inside the unpacked zip that holds Mosaic.exe.</summary>
    private static string PackageRoot(string unpacked)
    {
        if (File.Exists(Path.Combine(unpacked, UpdatePackage.ExecutableName))) return unpacked;
        var folders = Directory.GetDirectories(unpacked);
        if (folders.Length == 1 && Directory.GetFiles(unpacked).Length == 0
            && File.Exists(Path.Combine(folders[0], UpdatePackage.ExecutableName)))
        {
            return folders[0];
        }
        return unpacked;
    }

    /// <summary>Is the unpacked folder Mosaic, of the release's version, newer than this one, for this processor?</summary>
    internal static void CheckPackage(string folder, SoftwareVersion expected, SoftwareVersion? current, MachineArchitecture architecture)
    {
        var executable = Path.Combine(folder, UpdatePackage.ExecutableName);
        var assembly = Path.Combine(folder, UpdatePackage.AssemblyFileName);
        if (!File.Exists(executable) || !File.Exists(assembly))
        {
            throw new UpdateException($"The download is not a Mosaic for Windows package (no {UpdatePackage.ExecutableName}).");
        }
        var info = FileVersionInfo.GetVersionInfo(assembly);
        var built = SoftwareVersion.Parse(info.ProductVersion) ?? SoftwareVersion.Parse(info.FileVersion);
        if (built is null || built != expected || !string.Equals(info.ProductName, "Mosaic", StringComparison.Ordinal))
        {
            throw new UpdateException($"The downloaded app is not Mosaic {expected.NumberString}.");
        }
        if (current is not null && built <= current) throw new UpdateException("The download is not newer than the Mosaic you have.");
        if (!PeImage.IsNative(PeImage.Machine(executable), architecture))
        {
            throw new UpdateException($"The download is not built for this PC’s processor ({architecture.Id()}).");
        }
    }

    private async Task DownloadAsync(ReleaseAsset asset, string destination, Action<double?>? progress, CancellationToken token)
    {
        if (asset.BrowserUrl is not { } url) throw new UpdateException($"The release has no download link for {asset.Name}.");
        var current = url;
        for (var hop = 0; ; hop++)
        {
            // Never from HTTPS down to plain HTTP (a local test feed may be HTTP throughout).
            if (current.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && ReleaseCheck.IsAcceptableFeedOverride(current)))
            {
                throw new UpdateException("The update download was redirected to an insecure address, so Mosaic stopped it.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location && hop < 10)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            if (status != 200)
            {
                throw new UpdateException(status == 404
                    ? "GitHub did not find the update file (HTTP 404)."
                    : $"The update download failed (HTTP {status}).");
            }
            var expected = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long received = 0;
            var lastReport = Stopwatch.StartNew();
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                if (progress is not null && lastReport.ElapsedMilliseconds >= 150)
                {
                    lastReport.Restart();
                    progress(expected > 0 ? (double)received / expected.Value : null);
                }
            }
            progress?.Invoke(1);
            if (expected is { } length && received != length) throw new UpdateException("The update download was cut short.");
            return;
        }
    }

    // ---- Install ----

    /// <summary>Quits Mosaic, installs the downloaded update and starts the new version.</summary>
    public void RestartToUpdate()
    {
        if (phase is not UpdatePhase.Ready) return;
        relaunchAfterInstall = true;
        QuitRequested?.Invoke();
    }

    /// <summary>
    /// Called while Mosaic quits (its window closing): hands a downloaded update to the helper,
    /// which finishes once this process and its children have exited. Returns whether a helper was started.
    /// </summary>
    /// <param name="waitFor">Other processes to wait for, such as the Gecko engine's main process.</param>
    public bool InstallIfReady(IEnumerable<int>? waitFor = null)
    {
        if (phase is not UpdatePhase.Ready || helperLaunched || stagedFolder is not { } staged || !Directory.Exists(staged)) return false;
        var request = new UpdatePackage.HelperRequest(
            options.ProcessId, options.InstallDirectory, staged, relaunchAfterInstall, options.Arguments,
            [.. (waitFor ?? []).Where(id => id > 0).Distinct()], HelperLogPath);
        try
        {
            Directory.CreateDirectory(options.DownloadDirectory);
            File.WriteAllText(HelperLogPath, $"Mosaic {options.CurrentVersion} → {Path.GetFileName(staged)}{System.Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        helperLaunched = (options.LaunchHelper ?? LaunchHelper)(request);
        return helperLaunched;
    }

    /// <summary>Starts Windows PowerShell with the helper, detached and without a window.</summary>
    public static bool LaunchHelper(UpdatePackage.HelperRequest request)
    {
        try
        {
            var start = new ProcessStartInfo(UpdatePackage.PowerShellPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (var argument in UpdatePackage.HelperArguments(request)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            Trace.TraceError($"Mosaic: could not start the update installer: {error.Message}");
            return false;
        }
    }

    /// <summary>
    /// Deletes what earlier runs left: downloads never finished, an unpacked update never installed,
    /// and a previous version the helper could not delete. Runs off the UI thread.
    /// </summary>
    public Task CleanLeftoversAsync() => Task.Run(() =>
    {
        if (phase is not UpdatePhase.IdlePhase) return;
        // Folders only: install.log tells what the last update did.
        try
        {
            if (Directory.Exists(options.DownloadDirectory))
            {
                foreach (var folder in Directory.GetDirectories(options.DownloadDirectory)) GeckoRuntimeInstaller.DeleteQuietly(folder);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var install = options.InstallDirectory;
        if (Path.GetDirectoryName(install) is not { } parent || !Directory.Exists(parent)) return;
        var name = Path.GetFileName(install);
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(parent, $".{name}.update-*")
                .Concat(Directory.EnumerateDirectories(parent, $"{name}.previous-*")))
            {
                GeckoRuntimeInstaller.DeleteQuietly(folder);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    });
}

/// <summary>A failure of the update with a message for the person.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Checks GitHub for a newer Mosaic release: on request, or daily if the person chose so (or
/// turned on automatic updates). A daily check also asks Mozilla whether a newer Firefox ESR
/// exists for the private runtime. Port of <c>UpdateChecker</c>.
/// </summary>
public sealed class UpdateChecker : ObservableObject, IDisposable
{
    public const string FrequencyKey = "updateCheckFrequency";
    public const string LastCheckKey = "updateLastCheck";

    /// <summary>How long after start the first scheduled check waits, so it never competes with start-up.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

    private readonly PlatformStore store;
    private readonly MosaicUpdater updater;
    private readonly GeckoRuntimeInstaller? runtime;
    private readonly UpdaterOptions options;
    private readonly HttpClient http;
    private UpdateCheckFrequency? frequency;
    private bool isChecking;
    private ReleaseOutcome? outcome;
    private DateTimeOffset? lastCheck;
    private bool lastCheckLoaded;
    private ITimer? timer;

    public UpdateChecker(PlatformStore store, MosaicUpdater updater, GeckoRuntimeInstaller? runtime, UpdaterOptions? options = null)
    {
        this.store = store;
        this.updater = updater;
        this.runtime = runtime;
        this.options = options ?? new UpdaterOptions();
        updater.Checker = this;
        http = this.options.HttpHandler is { } handler ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mosaic", this.options.CurrentVersion));
    }

    public UpdateCheckFrequency Frequency
    {
        get => frequency ??= UpdateCheckFrequencies.FromId(store.GetString(FrequencyKey)) ?? UpdateCheckFrequency.Manual;
        set
        {
            if (Frequency == value) return;
            frequency = value;
            store.Set(FrequencyKey, value.Id());
            Notify();
            RunIfDue();
        }
    }

    public bool IsChecking { get => isChecking; private set => Set(ref isChecking, value); }

    public ReleaseOutcome? Outcome { get => outcome; private set => Set(ref outcome, value); }

    public DateTimeOffset? LastCheck
    {
        get
        {
            if (!lastCheckLoaded)
            {
                lastCheckLoaded = true;
                lastCheck = store.GetDate(LastCheckKey);
            }
            return lastCheck;
        }
        private set
        {
            lastCheckLoaded = true;
            Set(ref lastCheck, value);
        }
    }

    /// <summary>Where releases are looked up (GitHub, or the test feed).</summary>
    public Uri FeedUrl => ReleaseCheck.FeedUrl(options.Environment);

    /// <summary>Hourly look at whether a daily check is due (nothing is fetched unless it is); the first look after <see cref="FirstCheckDelay"/>.</summary>
    public void StartSchedule()
    {
        timer?.Dispose();
        var context = SynchronizationContext.Current;
        timer = options.Time.CreateTimer(_ =>
        {
            if (context is null) Tick();
            else context.Post(_ => Tick(), null);
        }, null, FirstCheckDelay, TimeSpan.FromHours(1));
    }

    private bool cleaned;

    private void Tick()
    {
        if (!cleaned)
        {
            cleaned = true;
            _ = updater.CleanLeftoversAsync();
        }
        RunIfDue();
    }

    private void RunIfDue()
    {
        var effective = updater.Automatic ? UpdateCheckFrequency.Daily : Frequency;
        if (!effective.IsDue(LastCheck, options.Time.GetUtcNow()) || isChecking) return;
        _ = RunScheduledAsync();
    }

    private async Task RunScheduledAsync()
    {
        await CheckNowAsync();
        if (runtime is not null) await runtime.CheckForUpdateAsync();
    }

    public async Task CheckNowAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Outcome = ReleaseCheck.Outcome((int)response.StatusCode, body, SoftwareVersion.Parse(options.CurrentVersion));
            if (Outcome is ReleaseOutcome.Available available) updater.ReleaseFound(available.Release);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            Outcome = new ReleaseOutcome.Failed(error is TaskCanceledException ? "GitHub did not answer in time." : error.Message);
        }
        finally
        {
            LastCheck = options.Time.GetUtcNow();
            store.SetDate(LastCheckKey, lastCheck);
            IsChecking = false;
        }
    }

    public void Dispose() => timer?.Dispose();
}
