using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Platform;

/// <summary>Runs Firefox's installer to unpack its files (and nothing else).</summary>
public interface IFirefoxExtractor
{
    /// <summary>Unpacks <paramref name="installer"/> into <paramref name="destination"/>; returns the exit code. Blocking.</summary>
    /// <exception cref="OperationCanceledException">Cancelled; the installer process has been stopped.</exception>
    int Extract(string installer, string destination, CancellationToken cancellation);
}

/// <summary>
/// Firefox's full installer is a self-extracting 7-Zip archive whose documented <c>/ExtractDir</c>
/// option unpacks the application files and exits without installing anything: no registry
/// entries, shortcuts, services, scheduled tasks or default-browser registration (the same step
/// <c>windows/scripts/bootstrap.ps1</c> runs). Two details of the stub matter (Mozilla's
/// <c>other-licenses/7zstub/…/SfxSetup.cpp</c>): it takes everything after <c>/ExtractDir=</c>
/// verbatim as the folder, so the path must not be quoted and nothing may follow it; and it shows
/// no UI with the option and reports failure through its exit code.
/// </summary>
public sealed class FirefoxInstallerExtractor : IFirefoxExtractor
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public int Extract(string installer, string destination, CancellationToken cancellation)
    {
        if (destination.Contains('"')) throw new ArgumentException($"Unexpected quote character in path: {destination}");
        var start = new ProcessStartInfo(installer)
        {
            // The trailing separator makes the value a folder whether or not the stub normalises it.
            Arguments = "/ExtractDir=" + destination.TrimEnd('\\') + "\\",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installer) ?? "",
        };
        using var process = Process.Start(start) ?? throw new RuntimeInstallException(new RuntimeInstallFailure.ExtractionFailed("it did not start"));
        var deadline = DateTime.UtcNow + Timeout;
        while (!process.WaitForExit(200))
        {
            if (cancellation.IsCancellationRequested || DateTime.UtcNow > deadline)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
                process.WaitForExit(5000);
                cancellation.ThrowIfCancellationRequested();
                throw new RuntimeInstallException(new RuntimeInstallFailure.ExtractionFailed("it took longer than five minutes"));
            }
        }
        return process.ExitCode;
    }
}

/// <summary>What the installer needs from the browser: the running engine and the tabs that wait for it.</summary>
public sealed class GeckoRuntimeHost
{
    /// <summary>True while a Gecko engine is running (or starting) from the folder being replaced.</summary>
    public Func<bool> IsEngineRunning { get; init; } = () => false;

    /// <summary>Quits the engine and waits for its processes; throws <see cref="RuntimeInstallException"/> if it will not quit.</summary>
    public Func<Task> StopEngineAsync { get; init; } = () => Task.CompletedTask;

    /// <summary>The runtime changed (installed, updated, removed): find it again and reopen waiting tabs.</summary>
    public Action EngineChanged { get; init; } = () => { };
}

/// <summary>
/// Installs, updates and removes Mosaic's private Gecko runtime (Firefox ESR) from inside the app.
/// Windows counterpart of <c>Sources/Mosaic/Features/Platform/GeckoRuntimeInstaller.swift</c>,
/// porting <c>windows/scripts/bootstrap.ps1</c>'s Gecko step.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Download Firefox ESR's full installer for this PC's architecture from Mozilla over HTTPS
/// (progress, cancellable; every redirect must stay on Mozilla's HTTPS servers).</item>
/// <item>Check the installer's Authenticode signature (valid, signer Mozilla Corporation), unpack it
/// with <c>/ExtractDir</c> into a staging folder beside the destination, and check the unpacked
/// <c>firefox.exe</c> the same way, plus its processor architecture and that it is a Gecko build.
/// Only verified files are installed. A <c>distribution\policies.json</c> keeps this private copy
/// from updating itself (Windows background update tasks) or asking to be the default browser.</item>
/// <item>Quit the running engine if there is one (its profile is separate and kept), move the old
/// engine aside, move the new one into <c>%LOCALAPPDATA%\Mosaic\Engines\Firefox</c>, delete the old
/// one, and reopen Mozilla tabs that were waiting, without relaunching Mosaic.</item>
/// </list>
/// Nothing is left behind on cancel or failure: every attempt lives in its own folder under
/// <c>Engines\.staging</c>, which is deleted whatever happens (and at the next attempt, should
/// Mosaic have been killed meanwhile).
/// <para>Call members on the UI thread; heavy work runs on the thread pool.</para>
/// </remarks>
public sealed class GeckoRuntimeInstaller : ObservableObject
{
    private const string LastCheckKey = "geckoRuntimeLastCheck";
    private const string LatestKey = "geckoRuntimeLatestESR";

    private readonly PlatformStore store;
    private readonly GeckoInstallerOptions options;
    private readonly HttpClient http;
    private RuntimeInstallPhase phase = RuntimeInstallPhase.Idle;
    private SoftwareVersion? latestVersion;
    private DateTimeOffset? lastCheck;
    private bool isChecking;
    private string? message;
    private GeckoRuntime? installed;
    private bool installedKnown;
    private CancellationTokenSource? cancellation;
    private Task work = Task.CompletedTask;
    private bool loaded;

    public GeckoRuntimeInstaller(PlatformStore store, GeckoInstallerOptions? options = null)
    {
        this.store = store;
        this.options = options ?? new GeckoInstallerOptions();
        http = this.options.HttpHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;  // per-request timeouts below
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mosaic", MosaicVersion.Current is { Length: > 0 } v ? v : "0"));
    }

    public GeckoRuntimeHost Host { get; set; } = new();

    public RuntimeInstallPhase Phase
    {
        get => phase;
        private set
        {
            if (!Set(ref phase, value)) return;
            Notify(nameof(IsActive));
        }
    }

    public bool IsActive => phase.IsActive;

    /// <summary>Latest Firefox ESR according to Mozilla, after a check.</summary>
    public SoftwareVersion? LatestVersion { get { Load(); return latestVersion; } private set => Set(ref latestVersion, value); }

    public DateTimeOffset? LastCheck { get { Load(); return lastCheck; } private set => Set(ref lastCheck, value); }

    public bool IsChecking { get => isChecking; private set => Set(ref isChecking, value); }

    /// <summary>Result of the last update check or removal, in words.</summary>
    public string? Message { get => message; private set => Set(ref message, value); }

    /// <summary>The folder of the private runtime (contains firefox.exe once installed).</summary>
    public string EngineDirectory => options.EngineDirectory;

    public string ExecutablePath => Path.Combine(options.EngineDirectory, "firefox.exe");

    /// <summary>The private runtime, if installed. Read from disk once, then after each change.</summary>
    public GeckoRuntime? InstalledRuntime
    {
        get
        {
            if (!installedKnown)
            {
                installed = GeckoRuntime.FromExecutable(ExecutablePath, isManaged: true);
                installedKnown = true;
            }
            return installed;
        }
    }

    public SoftwareVersion? InstalledVersion => SoftwareVersion.Parse(InstalledRuntime?.Version);

    public bool IsUpdateAvailable => InstalledVersion is { } current && LatestVersion is { } latest && latest > current;

    /// <summary>Finished work, for tests and automation.</summary>
    public Task Work => work;

    private string StagingRoot => Path.Combine(Path.GetDirectoryName(options.EngineDirectory.TrimEnd('\\', '/')) ?? options.EngineDirectory, ".staging");

    // ---- Install / update ----

    /// <summary>
    /// Installs (or replaces) the private runtime: Mozilla's latest ESR, or the Firefox installer
    /// at <paramref name="localInstaller"/> (automated checks with a pre-downloaded installer; it
    /// must pass the same signature checks).
    /// </summary>
    public void Install(string? localInstaller = null)
    {
        if (phase.IsActive) return;
        Message = null;
        Transition(RuntimeInstallEvent.Start);
        var previous = work;
        var source = new CancellationTokenSource();
        cancellation = source;
        work = RunAsync(previous, localInstaller, source.Token);
    }

    public void Cancel()
    {
        if (!phase.IsCancellable) return;
        Transition(RuntimeInstallEvent.Cancel);
        cancellation?.Cancel();
    }

    public void ResetMessage()
    {
        if (!phase.IsActive) Transition(RuntimeInstallEvent.Reset);
    }

    private void Transition(RuntimeInstallEvent change) => Phase = RuntimeInstallMachine.Next(phase, change);

    private bool IsCancelled(CancellationToken token) => token.IsCancellationRequested || phase is RuntimeInstallPhase.CancelledPhase;

    private async Task RunAsync(Task previous, string? localInstaller, CancellationToken token)
    {
        // A cancelled attempt may still be cleaning up.
        try { await previous; } catch { /* reported by that attempt */ }
        var attempt = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(() =>
            {
                // Leftovers of attempts interrupted by a crash or a forced quit.
                DeleteQuietly(StagingRoot);
                Directory.CreateDirectory(attempt);
            }, CancellationToken.None);
            if (!RuntimeInstallRequirements.HasEnoughSpace(AvailableSpace(attempt)))
            {
                throw new RuntimeInstallException(new RuntimeInstallFailure.NotEnoughSpace((int)(RuntimeInstallRequirements.NeededBytes / 1_000_000)));
            }

            // 1. Download (or take the given installer).
            var installer = Path.Combine(attempt, "firefox-esr-setup.exe");
            if (localInstaller is not null)
            {
                await Task.Run(() => File.Copy(localInstaller, installer), token);
                Transition(new RuntimeInstallEvent.Progress(1, 1));
            }
            else
            {
                await DownloadAsync(options.DownloadUrl ?? FirefoxProductDetails.EsrDownloadUrl(options.Architecture), installer, token);
            }
            token.ThrowIfCancellationRequested();
            Transition(RuntimeInstallEvent.Downloaded);

            // 2. Verify, unpack, verify again.
            var (folder, version) = await Task.Run(() => VerifyAndExtract(installer, attempt, token), CancellationToken.None);
            if (IsCancelled(token)) return;

            // 3. Swap it in, quitting the engine first if it is running.
            var running = Host.IsEngineRunning() || EngineProcessesRunning();
            Transition(new RuntimeInstallEvent.Verified(running));
            if (running)
            {
                await Host.StopEngineAsync();
                if (EngineProcessesRunning() && !await WaitForEngineProcessesAsync(TimeSpan.FromSeconds(5)))
                {
                    throw new RuntimeInstallException(new RuntimeInstallFailure.EngineBusy());
                }
                Transition(RuntimeInstallEvent.EngineStopped);
            }
            await Task.Run(() => Replace(options.EngineDirectory, folder, attempt), CancellationToken.None);
            installedKnown = false;
            Transition(new RuntimeInstallEvent.Installed(version));
            NotifyInstalled();
            Host.EngineChanged();
        }
        catch (OperationCanceledException)
        {
            Transition(RuntimeInstallEvent.Cancel);
        }
        catch (RuntimeInstallException error)
        {
            if (!IsCancelled(token)) Transition(new RuntimeInstallEvent.Fail(error.Failure));
            else Transition(RuntimeInstallEvent.Cancel);
        }
        catch (HttpRequestException error)
        {
            if (!IsCancelled(token)) Transition(new RuntimeInstallEvent.Fail(RuntimeInstallFailure.FromHttpError(error)));
        }
        catch (Exception error)
        {
            // Disk errors mostly; whatever it is, the attempt ends and says why.
            if (!IsCancelled(token)) Transition(new RuntimeInstallEvent.Fail(new RuntimeInstallFailure.CopyFailed(error.Message)));
        }
        finally
        {
            await Task.Run(() =>
            {
                DeleteQuietly(attempt);
                // Only succeeds once empty, so a parallel attempt's folder is never touched.
                try { Directory.Delete(StagingRoot); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }, CancellationToken.None);
        }
    }

    private async Task DownloadAsync(Uri url, string destination, CancellationToken token)
    {
        var context = SynchronizationContext.Current;
        var current = url;
        for (var hop = 0; ; hop++)
        {
            if (!FirefoxProductDetails.IsTrustedDownloadUrl(current))
            {
                throw new RuntimeInstallException(new RuntimeInstallFailure.UntrustedSource(current.IsAbsoluteUri ? current.Host : current.ToString()));
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new RuntimeInstallException(new RuntimeInstallFailure.Network("the server did not answer in time"));
            }
            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= 10 || response.Headers.Location is not { } location)
                    {
                        throw new RuntimeInstallException(new RuntimeInstallFailure.Server(status));
                    }
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }
                if (status != 200) throw new RuntimeInstallException(new RuntimeInstallFailure.Server(status));

                var expected = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long received = 0;
                var lastReport = Stopwatch.StartNew();
                while (true)
                {
                    int read;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        // A stalled transfer ends the attempt rather than hanging it.
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        try
                        {
                            read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            throw new RuntimeInstallException(new RuntimeInstallFailure.Network("the download stalled"));
                        }
                    }
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                    if (lastReport.ElapsedMilliseconds >= 100)
                    {
                        lastReport.Restart();
                        Report(context, received, expected);
                    }
                }
                Report(context, received, expected);
                if (expected is { } length && received != length)
                {
                    throw new RuntimeInstallException(new RuntimeInstallFailure.Network("the download was cut short"));
                }
                return;
            }
        }
    }

    private void Report(SynchronizationContext? context, long received, long? expected)
    {
        void Apply() => Transition(new RuntimeInstallEvent.Progress(received, expected));
        if (context is null) Apply();
        else context.Post(_ => Apply(), null);
    }

    /// <summary>Checks the installer, unpacks it, and checks what came out. Runs on the thread pool.</summary>
    private (string Folder, string Version) VerifyAndExtract(string installer, string attempt, CancellationToken token)
    {
        var expectation = SignerExpectation.MozillaFirefox;
        // An error page or a truncated file is not a program; say so rather than "not signed".
        if (PeImage.Machine(installer) is null) throw new RuntimeInstallException(new RuntimeInstallFailure.DamagedDownload());
        if (expectation.Evaluate(options.Verifier.Verify(installer)).AsFailure() is { } installerFailure)
        {
            throw new RuntimeInstallException(installerFailure);
        }
        token.ThrowIfCancellationRequested();

        var staging = Path.Combine(attempt, "extracted");
        Directory.CreateDirectory(staging);
        var exitCode = options.Extractor.Extract(installer, staging, token);
        token.ThrowIfCancellationRequested();
        if (exitCode != 0) throw new RuntimeInstallException(new RuntimeInstallFailure.ExtractionFailed($"exit code {exitCode}"));

        // The archive holds the application in a "core" folder next to the installer program.
        var source = new[] { Path.Combine(staging, "core"), staging }
            .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "firefox.exe")))
            ?? throw new RuntimeInstallException(new RuntimeInstallFailure.NoFirefoxInDownload());
        var executable = Path.Combine(source, "firefox.exe");
        if (expectation.Evaluate(options.Verifier.Verify(executable)).AsFailure() is { } executableFailure)
        {
            throw new RuntimeInstallException(executableFailure);
        }
        if (!PeImage.IsNative(PeImage.Machine(executable), options.Architecture))
        {
            throw new RuntimeInstallException(new RuntimeInstallFailure.WrongArchitecture(options.Architecture == MachineArchitecture.Arm64 ? "ARM64" : "x64"));
        }
        var runtime = GeckoRuntime.FromExecutable(executable) ?? throw new RuntimeInstallException(new RuntimeInstallFailure.NotGeckoBrowser());

        // Enterprise policies in the application's own "distribution" folder apply to this copy only.
        var distribution = Directory.CreateDirectory(Path.Combine(source, "distribution")).FullName;
        File.WriteAllText(Path.Combine(distribution, "policies.json"), PoliciesJson);
        return (source, runtime.Version);
    }

    /// <summary>The policies bootstrap.ps1 writes: no self-update, no default-browser check or agent.</summary>
    public const string PoliciesJson = """{"policies":{"DisableAppUpdate":true,"DontCheckDefaultBrowser":true,"DisableDefaultBrowserAgent":true}}""";

    /// <summary>Moves <paramref name="staged"/> to <paramref name="destination"/>, keeping the old runtime until the new one is in place.</summary>
    private static void Replace(string destination, string staged, string scratch)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination.TrimEnd('\\', '/'))!);
        var old = Path.Combine(scratch, "previous-Firefox");
        var hadOld = Directory.Exists(destination);
        try
        {
            if (hadOld) MoveWithRetry(destination, old);
            MoveWithRetry(staged, destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (hadOld && !Directory.Exists(destination) && Directory.Exists(old))
            {
                try { Directory.Move(old, destination); } catch (Exception restore) when (restore is IOException or UnauthorizedAccessException) { }
            }
            throw new RuntimeInstallException(new RuntimeInstallFailure.CopyFailed(error.Message));
        }
    }

    /// <summary>Windows keeps a file busy for a moment after its process exits (and while a virus scanner reads it).</summary>
    private static void MoveWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    // ---- The running engine ----

    /// <summary>Whether any process runs from the engine folder (a Mosaic that crashed can leave one behind).</summary>
    private bool EngineProcessesRunning() => options.ProcessesInFolder(options.EngineDirectory) > 0;

    private async Task<bool> WaitForEngineProcessesAsync(TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < limit)
        {
            if (!EngineProcessesRunning()) return true;
            await Task.Delay(100);
        }
        return !EngineProcessesRunning();
    }

    /// <summary>Counts processes whose executable is inside <paramref name="folder"/> (Windows; zero elsewhere).</summary>
    public static int CountProcessesInFolder(string folder)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        var count = 0;
        foreach (var name in new[] { "firefox", "plugin-container", "crashreporter", "updater", "default-browser-agent", "pingsender" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) count++;
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Another user's or an elevated process: not Mosaic's engine.
                    }
                }
            }
        }
        return count;
    }

    // ---- Remove ----

    /// <summary>
    /// Deletes the private runtime. Mozilla's profile (cookies, extension data) is kept, so
    /// reinstalling picks up where it left off.
    /// </summary>
    public async Task RemoveAsync()
    {
        if (phase.IsActive || !Directory.Exists(options.EngineDirectory)) return;
        Message = null;
        try
        {
            if (Host.IsEngineRunning() || EngineProcessesRunning())
            {
                await Host.StopEngineAsync();
                if (!await WaitForEngineProcessesAsync(TimeSpan.FromSeconds(5)))
                {
                    throw new RuntimeInstallException(new RuntimeInstallFailure.EngineBusy());
                }
            }
            var scratch = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
            await Task.Run(() =>
            {
                Directory.CreateDirectory(scratch);
                MoveWithRetry(options.EngineDirectory, Path.Combine(scratch, "Firefox"));
                DeleteQuietly(scratch);
                try { Directory.Delete(StagingRoot); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            });
            Message = "The Mozilla engine was removed. Its browsing data was kept.";
            Transition(RuntimeInstallEvent.Reset);
        }
        catch (RuntimeInstallException error)
        {
            Message = error.Failure.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Message = new RuntimeInstallFailure.RemoveFailed(error.Message).Message;
        }
        installedKnown = false;
        NotifyInstalled();
        Host.EngineChanged();
    }

    // ---- Update check ----

    /// <summary>Asks Mozilla which Firefox ESR is current.</summary>
    public async Task CheckForUpdateAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, options.VersionsUrl);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await http.SendAsync(request, timeout.Token);
            var status = (int)response.StatusCode;
            var body = status == 200 ? await response.Content.ReadAsStringAsync(timeout.Token) : "";
            if (status != 200 || FirefoxProductDetails.EsrVersion(body) is not { } latest)
            {
                Message = $"Mozilla’s version information could not be read (HTTP {status}). Try again later.";
                return;
            }
            LatestVersion = latest;
            LastCheck = options.Time.GetUtcNow();
            store.SetDate(LastCheckKey, lastCheck);
            store.Set(LatestKey, latest.Original);
            Message = InstalledVersion is { } current
                ? latest > current
                    ? $"Firefox ESR {latest.NumberString} is available (installed: {current.NumberString})."
                    : $"The Mozilla engine is up to date (Firefox ESR {current.NumberString})."
                : $"The current Firefox ESR is {latest.NumberString}.";
            Notify(nameof(IsUpdateAvailable));
        }
        catch (HttpRequestException error)
        {
            Message = RuntimeInstallFailure.FromHttpError(error).Message;
        }
        catch (OperationCanceledException)
        {
            Message = new RuntimeInstallFailure.Network("Mozilla did not answer in time").Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void Load()
    {
        if (loaded) return;
        loaded = true;
        lastCheck = store.GetDate(LastCheckKey);
        latestVersion = SoftwareVersion.Parse(store.GetString(LatestKey));
    }

    private void NotifyInstalled()
    {
        Notify(nameof(InstalledRuntime));
        Notify(nameof(InstalledVersion));
        Notify(nameof(IsUpdateAvailable));
    }

    private static long? AvailableSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void DeleteQuietly(string folder)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An installer that was just stopped can hold its files for a moment.
                Thread.Sleep(200);
            }
        }
    }
}

/// <summary>Where and how the installer works; tests replace the network, the signature check and the unpacking.</summary>
public sealed class GeckoInstallerOptions
{
    /// <summary>The private runtime's folder. Default: <see cref="AppPaths.ManagedGecko"/>.</summary>
    public string EngineDirectory { get; init; } = AppPaths.ManagedGecko;

    public MachineArchitecture Architecture { get; init; } = MachineArchitectures.Current;

    /// <summary>Overrides Mozilla's download address (tests).</summary>
    public Uri? DownloadUrl { get; init; }

    public Uri VersionsUrl { get; init; } = FirefoxProductDetails.VersionsUrl;

    /// <summary>Transport; must not follow redirects itself (the installer checks every hop).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    public IAuthenticodeVerifier Verifier { get; init; } = new WinTrustVerifier();

    public IFirefoxExtractor Extractor { get; init; } = new FirefoxInstallerExtractor();

    /// <summary>How many processes run from a folder; see <see cref="GeckoRuntimeInstaller.CountProcessesInFolder"/>.</summary>
    public Func<string, int> ProcessesInFolder { get; init; } = GeckoRuntimeInstaller.CountProcessesInFolder;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}
