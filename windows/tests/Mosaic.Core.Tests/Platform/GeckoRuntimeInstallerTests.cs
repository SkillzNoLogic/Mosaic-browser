using System.Net;
using Mosaic.Core.Platform;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Platform;

/// <summary>Port of <c>RuntimeInstallTests.swift</c> (state machine, messages) and <c>CodeSignatureTests</c> for Authenticode.</summary>
public class RuntimeInstallMachineTests
{
    private static RuntimeInstallPhase Next(RuntimeInstallPhase phase, RuntimeInstallEvent change) => RuntimeInstallMachine.Next(phase, change);

    [Fact]
    public void FreshInstallRunsThrough()
    {
        var phase = Next(RuntimeInstallPhase.Idle, RuntimeInstallEvent.Start);
        Assert.Equal(new RuntimeInstallPhase.Downloading(0, null), phase);
        phase = Next(phase, new RuntimeInstallEvent.Progress(50, 100));
        Assert.Equal(0.5, phase.Fraction);
        Assert.True(phase.IsCancellable);
        phase = Next(phase, RuntimeInstallEvent.Downloaded);
        Assert.Equal(RuntimeInstallPhase.Verifying, phase);
        phase = Next(phase, new RuntimeInstallEvent.Verified(EngineRunning: false));
        Assert.Equal(RuntimeInstallPhase.Installing, phase);
        Assert.False(phase.IsCancellable);
        phase = Next(phase, new RuntimeInstallEvent.Installed("153.8.0"));
        Assert.Equal(new RuntimeInstallPhase.Finished("153.8.0"), phase);
        Assert.False(phase.IsActive);
    }

    [Fact]
    public void UpdateStopsTheEngineBeforeInstalling()
    {
        var phase = Next(RuntimeInstallPhase.Verifying, new RuntimeInstallEvent.Verified(EngineRunning: true));
        Assert.Equal(RuntimeInstallPhase.StoppingEngine, phase);
        Assert.False(phase.IsCancellable);
        Assert.Equal(RuntimeInstallPhase.Installing, Next(phase, RuntimeInstallEvent.EngineStopped));
    }

    [Fact]
    public void CancelOnlyBeforeFilesMove()
    {
        Assert.Equal(RuntimeInstallPhase.Cancelled, Next(new RuntimeInstallPhase.Downloading(1, 2), RuntimeInstallEvent.Cancel));
        Assert.Equal(RuntimeInstallPhase.Cancelled, Next(RuntimeInstallPhase.Verifying, RuntimeInstallEvent.Cancel));
        Assert.Equal(RuntimeInstallPhase.Installing, Next(RuntimeInstallPhase.Installing, RuntimeInstallEvent.Cancel));
        Assert.Equal(RuntimeInstallPhase.StoppingEngine, Next(RuntimeInstallPhase.StoppingEngine, RuntimeInstallEvent.Cancel));
        Assert.Equal(RuntimeInstallPhase.Idle, Next(RuntimeInstallPhase.Idle, RuntimeInstallEvent.Cancel));
    }

    [Fact]
    public void LateEventsAfterCancelAreIgnored()
    {
        var cancelled = RuntimeInstallPhase.Cancelled;
        Assert.Equal(cancelled, Next(cancelled, new RuntimeInstallEvent.Progress(9, 10)));
        Assert.Equal(cancelled, Next(cancelled, RuntimeInstallEvent.Downloaded));
        Assert.Equal(cancelled, Next(cancelled, new RuntimeInstallEvent.Fail(new RuntimeInstallFailure.Offline())));
        Assert.Equal(cancelled, Next(cancelled, new RuntimeInstallEvent.Installed("1")));
    }

    [Fact]
    public void FailuresAndRestart()
    {
        var failed = Next(new RuntimeInstallPhase.Downloading(0, null), new RuntimeInstallEvent.Fail(new RuntimeInstallFailure.Offline()));
        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.Offline()), failed);
        Assert.Equal(new RuntimeInstallPhase.Downloading(0, null), Next(failed, RuntimeInstallEvent.Start));
        Assert.Equal(RuntimeInstallPhase.Idle, Next(failed, RuntimeInstallEvent.Reset));
        Assert.Equal(RuntimeInstallPhase.Verifying, Next(RuntimeInstallPhase.Verifying, RuntimeInstallEvent.Start));
        Assert.Equal(RuntimeInstallPhase.Installing, Next(RuntimeInstallPhase.Installing, RuntimeInstallEvent.Reset));
    }

    [Fact]
    public void MessagesArePlainLanguage()
    {
        RuntimeInstallFailure[] failures =
        [
            new RuntimeInstallFailure.Offline(), new RuntimeInstallFailure.Network("timed out"), new RuntimeInstallFailure.Server(503),
            new RuntimeInstallFailure.UntrustedSource("evil.example"), new RuntimeInstallFailure.NotEnoughSpace(800),
            new RuntimeInstallFailure.DamagedDownload(), new RuntimeInstallFailure.ExtractionFailed("exit code 2"),
            new RuntimeInstallFailure.NoFirefoxInDownload(), new RuntimeInstallFailure.SignatureInvalid("x"),
            new RuntimeInstallFailure.WrongSigner(null), new RuntimeInstallFailure.WrongSigner("CN=Someone"),
            new RuntimeInstallFailure.WrongArchitecture("ARM64"), new RuntimeInstallFailure.NotGeckoBrowser(),
            new RuntimeInstallFailure.EngineBusy(), new RuntimeInstallFailure.CopyFailed("disk full"),
            new RuntimeInstallFailure.RemoveFailed("busy"), new RuntimeInstallFailure.Unsupported(),
        ];
        foreach (var failure in failures)
        {
            Assert.True(failure.Message.Length > 30, failure.Message);
            Assert.True(failure.Message.EndsWith('.') || failure.Message.EndsWith(')'), failure.Message);
        }
        Assert.Equal("Downloading Firefox ESR… 12 MB of 70 MB", new RuntimeInstallPhase.Downloading(12_400_000, 70_000_000).Summary);
        Assert.Equal("Downloading Firefox ESR… 3 MB", new RuntimeInstallPhase.Downloading(3_000_000, null).Summary);
    }

    [Fact]
    public void SpaceCheck()
    {
        Assert.True(RuntimeInstallRequirements.HasEnoughSpace(null));
        Assert.False(RuntimeInstallRequirements.HasEnoughSpace(200_000_000));
        Assert.True(RuntimeInstallRequirements.HasEnoughSpace(5_000_000_000));
    }

    [Fact]
    public void OnlyAValidMozillaSignatureIsTrusted()
    {
        var mozilla = SignerExpectation.MozillaFirefox;
        var valid = new AuthenticodeResult(AuthenticodeStatus.Valid, "", "CN=Mozilla Corporation, O=Mozilla Corporation, L=San Francisco, S=California, C=US", "Mozilla Corporation");
        Assert.IsType<SignatureVerdict.Trusted>(mozilla.Evaluate(valid));
        Assert.Null(mozilla.Evaluate(valid).AsFailure());

        var microsoft = valid with { SignerSubject = "CN=Microsoft Corporation, O=Microsoft Corporation", SignerOrganisation = "Microsoft Corporation" };
        Assert.Equal(new RuntimeInstallFailure.WrongSigner("CN=Microsoft Corporation, O=Microsoft Corporation"), mozilla.Evaluate(microsoft).AsFailure());
        // The name must be the organisation attribute exactly, not merely contain it.
        Assert.IsType<SignatureVerdict.WrongSigner>(mozilla.Evaluate(valid with { SignerOrganisation = "Mozilla Corporation Ltd" }));
        Assert.IsType<SignatureVerdict.WrongSigner>(mozilla.Evaluate(valid with { SignerOrganisation = null }));
        Assert.Equal(new RuntimeInstallFailure.WrongSigner(null), mozilla.Evaluate(new AuthenticodeResult(AuthenticodeStatus.NotSigned)).AsFailure());
        // A Mozilla certificate on a file changed after signing is not trusted.
        Assert.Equal(new RuntimeInstallFailure.SignatureInvalid("TRUST_E_BAD_DIGEST"),
            mozilla.Evaluate(valid with { Status = AuthenticodeStatus.Invalid, Detail = "TRUST_E_BAD_DIGEST" }).AsFailure());
        Assert.IsType<RuntimeInstallFailure.Unsupported>(mozilla.Evaluate(new AuthenticodeResult(AuthenticodeStatus.Unsupported)).AsFailure());
    }
}

/// <summary>The installer end to end, with the network, signature check and unpacking replaced.</summary>
public class GeckoRuntimeInstallerTests
{
    private sealed class Rig : IDisposable
    {
        public string Root { get; } = UpdatePackageTests.TestFolder();
        public StubHttpHandler Http { get; } = new();
        public FakeVerifier Verifier { get; } = new();
        public FakeExtractor Extractor { get; } = new();
        public int ProcessesInFolder { get; set; }
        public bool EngineRunning { get; set; }
        public int Stops { get; private set; }
        public int EngineChanges { get; private set; }
        public MachineArchitecture Architecture { get; init; } = MachineArchitecture.X64;
        public GeckoRuntimeInstaller Installer { get; }
        public PlatformStore Store { get; }

        public Rig(MachineArchitecture architecture = MachineArchitecture.X64)
        {
            Architecture = architecture;
            Extractor.Machine = PeImage.MachineFor(architecture);
            Http.Respond = request => Task.FromResult(request.RequestUri!.Host == "download.mozilla.org"
                ? Redirect("https://download-installer.cdn.mozilla.net/pub/firefox/releases/140.5.0esr/win64/en-US/Firefox%20Setup%20140.5.0esr.exe")
                : Installer(PeImage.MachineX64));
            Store = new PlatformStore(Path.Combine(Root, "platform.json"));
            Installer = new GeckoRuntimeInstaller(Store, new GeckoInstallerOptions
            {
                EngineDirectory = Path.Combine(Root, "Engines", "Firefox"),
                Architecture = architecture,
                HttpHandler = Http,
                Verifier = Verifier,
                Extractor = Extractor,
                ProcessesInFolder = _ => ProcessesInFolder,
            })
            {
                Host = new GeckoRuntimeHost
                {
                    IsEngineRunning = () => EngineRunning,
                    StopEngineAsync = () =>
                    {
                        Stops++;
                        EngineRunning = false;
                        return Task.CompletedTask;
                    },
                    EngineChanged = () => EngineChanges++,
                },
            };
        }

        public string Engines => Path.Combine(Root, "Engines");

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    internal static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    internal static HttpResponseMessage Installer(ushort machine)
    {
        var path = Path.Combine(UpdatePackageTests.TestFolder(), "setup.exe");
        FakePe.Write(path, machine);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) };
    }

    internal sealed class FakeVerifier : IAuthenticodeVerifier
    {
        public List<string> Checked { get; } = [];
        public Func<string, AuthenticodeResult> Result { get; set; } = _ =>
            new AuthenticodeResult(AuthenticodeStatus.Valid, "", "CN=Mozilla Corporation, O=Mozilla Corporation", "Mozilla Corporation");

        public AuthenticodeResult Verify(string path)
        {
            lock (Checked) Checked.Add(Path.GetFileName(path));
            return Result(path);
        }
    }

    internal sealed class FakeExtractor : IFirefoxExtractor
    {
        public ushort Machine { get; set; } = PeImage.MachineX64;
        public int ExitCode { get; set; }
        public bool WithoutFirefox { get; set; }
        public string Version { get; set; } = "140.5.0";
        public TaskCompletionSource? Gate { get; set; }
        public string? Arguments { get; private set; }

        public int Extract(string installer, string destination, CancellationToken cancellation)
        {
            Arguments = destination;
            Gate?.Task.Wait(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (ExitCode != 0 || WithoutFirefox) return ExitCode;
            var core = Directory.CreateDirectory(Path.Combine(destination, "core")).FullName;
            FakePe.Write(Path.Combine(core, "firefox.exe"), Machine);
            File.WriteAllText(Path.Combine(core, "xul.dll"), "");
            File.WriteAllText(Path.Combine(core, "application.ini"), $"[App]\nVendor=Mozilla\nName=Firefox\nVersion={Version}\n\n[Gecko]\nMinVersion={Version}\n");
            File.WriteAllText(Path.Combine(destination, "setup.exe"), "");
            return 0;
        }
    }

    [Fact]
    public void InstallsVerifiedFirefoxAndCleansUp() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        var phases = new List<string>();
        rig.Installer.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(GeckoRuntimeInstaller.Phase)) phases.Add(rig.Installer.Phase.Name); };

        rig.Installer.Install();
        await rig.Installer.Work;

        Assert.Equal(new RuntimeInstallPhase.Finished("140.5.0"), rig.Installer.Phase);
        Assert.Equal("Firefox ESR 140.5.0 is installed.", rig.Installer.Phase.Summary);
        Assert.Equal(["downloading", "verifying", "installing", "finished"], phases.Distinct());
        // Both the installer and the unpacked firefox.exe were checked.
        Assert.Equal(["firefox-esr-setup.exe", "firefox.exe"], rig.Verifier.Checked);
        // Followed the redirector to the CDN, nowhere else.
        Assert.Equal(["download.mozilla.org", "download-installer.cdn.mozilla.net"], rig.Http.Requests.Select(uri => uri.Host));
        Assert.Contains("os=win64&", rig.Http.Requests[0].Query);
        var engine = Path.Combine(rig.Engines, "Firefox");
        Assert.True(File.Exists(Path.Combine(engine, "firefox.exe")));
        Assert.Equal(GeckoRuntimeInstaller.PoliciesJson, File.ReadAllText(Path.Combine(engine, "distribution", "policies.json")));
        Assert.Equal("140.5.0", rig.Installer.InstalledRuntime?.Version);
        Assert.True(rig.Installer.InstalledRuntime?.IsManaged);
        Assert.Equal(1, rig.EngineChanges);
        Assert.Equal(0, rig.Stops);
        // Nothing left behind but the engine.
        Assert.Equal(["Firefox"], Directory.GetDirectories(rig.Engines).Select(Path.GetFileName));
    });

    [Fact]
    public void Arm64PcsGetTheArm64Build() => TestLoop.Run(async () =>
    {
        using var rig = new Rig(MachineArchitecture.Arm64);
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.IsType<RuntimeInstallPhase.Finished>(rig.Installer.Phase);
        Assert.Contains("os=win64-aarch64", rig.Http.Requests[0].Query);
    });

    [Fact]
    public void AnUpdateStopsTheRunningEngineAndReplacesTheOldOne() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Installer.Install();
        await rig.Installer.Work;
        File.WriteAllText(Path.Combine(rig.Engines, "Firefox", "old-marker"), "");
        rig.EngineRunning = true;
        rig.Extractor.Version = "140.6.0";
        var phases = new List<string>();
        rig.Installer.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(GeckoRuntimeInstaller.Phase)) phases.Add(rig.Installer.Phase.Name); };

        rig.Installer.Install();
        await rig.Installer.Work;

        Assert.Equal(new RuntimeInstallPhase.Finished("140.6.0"), rig.Installer.Phase);
        Assert.Contains("stoppingEngine", phases);
        Assert.Equal(1, rig.Stops);
        Assert.False(File.Exists(Path.Combine(rig.Engines, "Firefox", "old-marker")));
        Assert.Equal("140.6.0", rig.Installer.InstalledRuntime?.Version);
        Assert.Equal(["Firefox"], Directory.GetDirectories(rig.Engines).Select(Path.GetFileName));
    });

    [Fact]
    public void AnEngineThatWillNotQuitIsReportedAndTheOldOneKept() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Installer.Install();
        await rig.Installer.Work;
        rig.ProcessesInFolder = 1;  // a firefox.exe from the engine folder keeps running

        rig.Installer.Install();
        await rig.Installer.Work;

        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.EngineBusy()), rig.Installer.Phase);
        Assert.True(File.Exists(Path.Combine(rig.Engines, "Firefox", "firefox.exe")));
        Assert.Equal(["Firefox"], Directory.GetDirectories(rig.Engines).Select(Path.GetFileName));
    });

    [Fact]
    public void RedirectsAwayFromMozillaAreRefused() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Http.Respond = _ => Task.FromResult(Redirect("http://download-installer.cdn.mozilla.net/x.exe"));
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.UntrustedSource("download-installer.cdn.mozilla.net")), rig.Installer.Phase);

        rig.Http.Respond = _ => Task.FromResult(Redirect("https://mozilla.org.evil.example/x.exe"));
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.UntrustedSource("mozilla.org.evil.example")), rig.Installer.Phase);
        Assert.False(Directory.Exists(Path.Combine(rig.Engines, "Firefox")));
        Assert.False(Directory.Exists(Path.Combine(rig.Engines, ".staging")));
    });

    [Fact]
    public void ServerErrorsAndErrorPagesAreReported() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.Server(503)), rig.Installer.Phase);

        rig.Http.Respond = _ => Task.FromResult(StubHttpHandler.Package("<html>captive portal</html>"));
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.DamagedDownload()), rig.Installer.Phase);
        Assert.Empty(rig.Verifier.Checked);
    });

    [Fact]
    public void SomethingNotSignedByMozillaIsNeverInstalled() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Verifier.Result = _ => new AuthenticodeResult(AuthenticodeStatus.Valid, "", "CN=Evil Ltd, O=Evil Ltd", "Evil Ltd");
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.WrongSigner("CN=Evil Ltd, O=Evil Ltd")), rig.Installer.Phase);
        Assert.Null(rig.Extractor.Arguments);  // never even unpacked

        // A valid installer whose unpacked firefox.exe fails its own check.
        rig.Verifier.Result = path => Path.GetFileName(path) == "firefox.exe"
            ? new AuthenticodeResult(AuthenticodeStatus.Invalid, "the file was changed after it was signed (TRUST_E_BAD_DIGEST)", "CN=Mozilla Corporation", "Mozilla Corporation")
            : new AuthenticodeResult(AuthenticodeStatus.Valid, "", "CN=Mozilla Corporation", "Mozilla Corporation");
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.SignatureInvalid("the file was changed after it was signed (TRUST_E_BAD_DIGEST)")), rig.Installer.Phase);
        Assert.False(Directory.Exists(Path.Combine(rig.Engines, "Firefox")));
        Assert.False(Directory.Exists(Path.Combine(rig.Engines, ".staging")));
        Assert.Equal(0, rig.EngineChanges);
    });

    [Fact]
    public void WrongArchitectureFailedExtractionAndMissingFirefoxAreReported() => TestLoop.Run(async () =>
    {
        using var rig = new Rig(MachineArchitecture.Arm64);
        rig.Extractor.Machine = PeImage.MachineX64;
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.WrongArchitecture("ARM64")), rig.Installer.Phase);

        rig.Extractor.ExitCode = 2;
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.ExtractionFailed("exit code 2")), rig.Installer.Phase);

        rig.Extractor.ExitCode = 0;
        rig.Extractor.WithoutFirefox = true;
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.Equal(new RuntimeInstallPhase.Failed(new RuntimeInstallFailure.NoFirefoxInDownload()), rig.Installer.Phase);
        Assert.False(Directory.Exists(Path.Combine(rig.Engines, ".staging")));
    });

    [Fact]
    public void CancellingLeavesNothingBehind() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Extractor.Gate = new TaskCompletionSource();
        rig.Installer.Install();
        // Wait until it is unpacking (the verifying phase).
        for (var i = 0; i < 200 && rig.Extractor.Arguments is null; i++) await Task.Delay(10);
        Assert.Equal(RuntimeInstallPhase.Verifying, rig.Installer.Phase);
        Assert.True(Directory.Exists(Path.Combine(rig.Engines, ".staging")));

        rig.Installer.Cancel();
        await rig.Installer.Work;

        Assert.Equal(RuntimeInstallPhase.Cancelled, rig.Installer.Phase);
        Assert.Equal("Cancelled. Nothing was changed.", rig.Installer.Phase.Summary);
        Assert.False(Directory.Exists(Path.Combine(rig.Engines, "Firefox")));
        Assert.False(Directory.Exists(Path.Combine(rig.Engines, ".staging")));
        Assert.Equal(0, rig.EngineChanges);
    });

    [Fact]
    public void ALocalInstallerSkipsTheDownloadButNotTheChecks() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        var local = Path.Combine(rig.Root, "Firefox Setup 140.5.0esr.exe");
        FakePe.Write(local, PeImage.MachineX64);
        rig.Installer.Install(local);
        await rig.Installer.Work;
        Assert.IsType<RuntimeInstallPhase.Finished>(rig.Installer.Phase);
        Assert.Empty(rig.Http.Requests);
        Assert.Equal(["firefox-esr-setup.exe", "firefox.exe"], rig.Verifier.Checked);
        Assert.True(File.Exists(local));  // the given file is copied, never moved
    });

    [Fact]
    public void RemovesTheEngineButKeepsTheProfile() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Installer.Install();
        await rig.Installer.Work;
        rig.EngineRunning = true;

        await rig.Installer.RemoveAsync();

        Assert.Equal(1, rig.Stops);
        Assert.Null(rig.Installer.InstalledRuntime);
        Assert.Equal("The Mozilla engine was removed. Its browsing data was kept.", rig.Installer.Message);
        Assert.Empty(Directory.GetDirectories(rig.Engines));
        Assert.Equal(RuntimeInstallPhase.Idle, rig.Installer.Phase);
        Assert.Equal(2, rig.EngineChanges);
    });

    [Fact]
    public void ChecksMozillaForTheCurrentEsr() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Http.Respond = _ => Task.FromResult(StubHttpHandler.Package("""{"FIREFOX_ESR": "140.6.0esr", "LATEST_FIREFOX_VERSION": "146.0"}"""));
        await rig.Installer.CheckForUpdateAsync();
        Assert.Equal(SoftwareVersion.Parse("140.6.0"), rig.Installer.LatestVersion);
        Assert.Equal("The current Firefox ESR is 140.6.0.", rig.Installer.Message);
        Assert.Equal("https://product-details.mozilla.org/1.0/firefox_versions.json", rig.Http.Requests.Single().ToString());

        // Installed 140.5.0: an update is available, and the result is remembered.
        rig.Http.Respond = request => Task.FromResult(request.RequestUri!.Host == "download.mozilla.org"
            ? Redirect("https://download-installer.cdn.mozilla.net/x.exe") : Installer(PeImage.MachineX64));
        rig.Installer.Install();
        await rig.Installer.Work;
        Assert.True(rig.Installer.IsUpdateAvailable);
        await rig.Store.FlushAsync();
        var reopened = new GeckoRuntimeInstaller(new PlatformStore(rig.Store.Path), new GeckoInstallerOptions { EngineDirectory = Path.Combine(rig.Engines, "Firefox") });
        Assert.Equal(SoftwareVersion.Parse("140.6.0"), reopened.LatestVersion);
        Assert.True(reopened.IsUpdateAvailable);

        rig.Http.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await rig.Installer.CheckForUpdateAsync();
        Assert.Equal("Mozilla’s version information could not be read (HTTP 500). Try again later.", rig.Installer.Message);
    });
}

/// <summary>The real thing, on Windows: WinVerifyTrust, and Mozilla's own installer from its servers.</summary>
public class WindowsSignatureTests
{
    [WindowsFact]
    public void TrustsMicrosoftSignedRuntimeFilesAndNamesTheSigner()
    {
        // coreclr.dll ships with an embedded Authenticode signature by Microsoft.
        var coreclr = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "coreclr.dll");
        var result = new WinTrustVerifier().Verify(coreclr);
        Assert.True(result.Status == AuthenticodeStatus.Valid, $"{result.Status}: {result.Detail}");
        Assert.Equal("Microsoft Corporation", result.SignerOrganisation);
        Assert.Contains("O=Microsoft Corporation", result.SignerSubject);
        // Validly signed, but not by Mozilla.
        Assert.Equal(new RuntimeInstallFailure.WrongSigner(result.SignerSubject), SignerExpectation.MozillaFirefox.Evaluate(result).AsFailure());
    }

    [WindowsFact]
    public void RejectsAlteredAndUnsignedFiles()
    {
        var folder = UpdatePackageTests.TestFolder();
        var coreclr = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "coreclr.dll");
        var altered = Path.Combine(folder, "coreclr-altered.dll");
        var bytes = File.ReadAllBytes(coreclr);
        bytes[bytes.Length / 2] ^= 0xFF;  // inside the signed content
        File.WriteAllBytes(altered, bytes);
        var tampered = new WinTrustVerifier().Verify(altered);
        Assert.Equal(AuthenticodeStatus.Invalid, tampered.Status);
        Assert.IsType<RuntimeInstallFailure.SignatureInvalid>(SignerExpectation.MozillaFirefox.Evaluate(tampered).AsFailure());

        var unsigned = FakePe.Write(Path.Combine(folder, "unsigned.exe"), PeImage.MachineX64);
        Assert.Equal(AuthenticodeStatus.NotSigned, new WinTrustVerifier().Verify(unsigned).Status);
        Assert.NotEqual(AuthenticodeStatus.Valid, new WinTrustVerifier().Verify(Path.Combine(folder, "missing.exe")).Status);
    }

    [LiveWindowsFact]
    public void InstallsTheRealFirefoxEsrFromMozilla() => TestLoop.Run(async () =>
    {
        var root = UpdatePackageTests.TestFolder();
        try
        {
            var installer = new GeckoRuntimeInstaller(new PlatformStore(null), new GeckoInstallerOptions
            {
                EngineDirectory = Path.Combine(root, "Engines", "Firefox"),
            });
            var phases = new List<string>();
            installer.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(GeckoRuntimeInstaller.Phase) && phases.LastOrDefault() != installer.Phase.Name) phases.Add(installer.Phase.Name);
            };
            installer.Install();
            await installer.Work;

            Assert.True(installer.Phase is RuntimeInstallPhase.Finished, installer.Phase.Summary);
            Assert.Equal(["downloading", "verifying", "installing", "finished"], phases);
            var runtime = installer.InstalledRuntime;
            Assert.NotNull(runtime);
            Assert.True(PeImage.IsNative(PeImage.Machine(runtime.ExecutablePath), MachineArchitectures.Current));
            Assert.Equal("Mozilla Corporation", new WinTrustVerifier().Verify(runtime.ExecutablePath).SignerOrganisation);
            Assert.True(File.Exists(Path.Combine(root, "Engines", "Firefox", "distribution", "policies.json")));
            Assert.Equal(["Firefox"], Directory.GetDirectories(Path.Combine(root, "Engines")).Select(Path.GetFileName));

            await installer.CheckForUpdateAsync();
            Assert.NotNull(installer.LatestVersion);
            Assert.False(installer.IsUpdateAvailable, installer.Message);
            Console.WriteLine($"Installed Firefox {runtime.Version}; Mozilla's current ESR: {installer.LatestVersion}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });
}
