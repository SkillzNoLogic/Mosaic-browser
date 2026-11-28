using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Mosaic.Core.Platform;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Platform;

/// <summary>
/// A release feed on 127.0.0.1, as <c>MOSAIC_UPDATE_API</c> allows: <c>/latest</c> answers like
/// GitHub's API, <c>/files/…</c> serves the assets.
/// </summary>
internal sealed class LocalReleaseFeed : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    private readonly Task loop;

    public LocalReleaseFeed()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        loop = Task.Run(ServeAsync);
    }

    public int Port { get; }
    public string LatestUrl => $"http://127.0.0.1:{Port}/latest";
    public string? LatestJson { get; set; }
    public List<string> Requests { get; } = [];

    public string Add(string name, byte[] content)
    {
        lock (files) files[name] = content;
        return $"http://127.0.0.1:{Port}/files/{Uri.EscapeDataString(name)}";
    }

    /// <summary>A GitHub-shaped release whose assets are the files added so far.</summary>
    public void Publish(string tag)
    {
        var assets = new JsonArray();
        lock (files)
        {
            foreach (var (name, content) in files)
            {
                assets.Add(new JsonObject
                {
                    ["name"] = name,
                    ["size"] = content.Length,
                    ["browser_download_url"] = $"http://127.0.0.1:{Port}/files/{Uri.EscapeDataString(name)}",
                    ["url"] = $"http://127.0.0.1:{Port}/api/{Uri.EscapeDataString(name)}",
                });
            }
        }
        LatestJson = new JsonObject
        {
            ["tag_name"] = tag,
            ["name"] = $"Mosaic {tag}",
            ["html_url"] = $"https://github.com/SkillzNoLogic/Mosaic-browser/releases/tag/{tag}",
            ["published_at"] = "2026-11-28T10:00:00Z",
            ["body"] = "Test release",
            ["assets"] = assets,
        }.ToJsonString();
    }

    private async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            var path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
            lock (Requests) Requests.Add(path);
            byte[]? body = null;
            if (path == "/latest" && LatestJson is { } json) body = Encoding.UTF8.GetBytes(json);
            else if (path.StartsWith("/files/", StringComparison.Ordinal))
            {
                lock (files) files.TryGetValue(path["/files/".Length..], out body);
            }
            context.Response.StatusCode = body is null ? 404 : 200;
            if (body is not null)
            {
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
            }
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        listener.Stop();
        listener.Close();
        try { loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}

/// <summary>
/// Mosaic's self-update end to end, against a local feed and a zip signed with a throwaway key
/// (injected through the internal option; the app's own key is fixed in the code).
/// </summary>
public class MosaicUpdaterTests
{
    private sealed class Rig : IDisposable
    {
        public string Root { get; } = UpdatePackageTests.TestFolder();
        public LocalReleaseFeed Feed { get; } = new();
        public TestSigningKey Key { get; } = new();
        public List<UpdatePackage.HelperRequest> Launched { get; } = [];
        public PlatformStore Store { get; }
        public MosaicUpdater Updater { get; }
        public UpdateChecker Checker { get; }
        public string Install { get; }
        public ManualTimeProvider Time { get; } = new();

        public Rig(string currentVersion = "0.0.9", string? publicKey = null)
        {
            Install = Directory.CreateDirectory(Path.Combine(Root, "Apps", "Mosaic")).FullName;
            FakePe.Write(Path.Combine(Install, "Mosaic.exe"), PeImage.MachineFor(MachineArchitectures.Process));
            Store = new PlatformStore(Path.Combine(Root, "Data", "platform.json"));
            var options = new UpdaterOptions
            {
                InstallDirectory = Install,
                DownloadDirectory = Path.Combine(Root, "Data", "Updates"),
                CurrentVersion = currentVersion,
                Environment = name => name == ReleaseCheck.FeedVariable ? Feed.LatestUrl : null,
                ProcessId = 4242,
                Arguments = ["--mosaic-automation"],
                LaunchHelper = request =>
                {
                    Launched.Add(request);
                    return true;
                },
                Time = Time,
                PublicKey = publicKey ?? Key.PublicKey,
            };
            Updater = new MosaicUpdater(Store, options);
            Checker = new UpdateChecker(Store, Updater, runtime: null, options);
        }

        /// <summary>
        /// A Windows release zip: Mosaic.exe for this architecture and, as Mosaic.dll, an assembly
        /// whose product is "Mosaic" at the build's version (Mosaic.Core.dll, built from the same
        /// Directory.Build.props as the app).
        /// </summary>
        public byte[] Package(ushort? machine = null, bool topFolder = false, string assemblyName = "Mosaic.dll")
        {
            var folder = Directory.CreateDirectory(Path.Combine(Root, "package-" + Guid.NewGuid().ToString("N"))).FullName;
            var content = topFolder ? Directory.CreateDirectory(Path.Combine(folder, "Mosaic")).FullName : folder;
            FakePe.Write(Path.Combine(content, "Mosaic.exe"), machine ?? PeImage.MachineFor(MachineArchitectures.Process));
            File.Copy(typeof(UpdatePackage).Assembly.Location, Path.Combine(content, assemblyName));
            Directory.CreateDirectory(Path.Combine(content, "gecko"));
            File.WriteAllText(Path.Combine(content, "gecko", "agent.sys.mjs"), "// new agent");
            var zip = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
            ZipFile.CreateFromDirectory(folder, zip);
            return File.ReadAllBytes(zip);
        }

        public void Publish(byte[] zip, string? signature = null)
        {
            var name = UpdatePackage.ZipName(BuildVersion, MachineArchitectures.Process);
            Feed.Add(name, zip);
            Feed.Add(name + ".sig", Encoding.UTF8.GetBytes(signature ?? Key.Sign(zip)));
            Feed.Publish("v" + BuildVersion.NumberString);
        }

        public void Dispose()
        {
            Feed.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The version the test package carries (Directory.Build.props).</summary>
    private static SoftwareVersion BuildVersion =>
        SoftwareVersion.Parse(FileVersionInfo.GetVersionInfo(typeof(UpdatePackage).Assembly.Location).ProductVersion)!;

    [Fact]
    public void FindsDownloadsVerifiesAndStagesASignedRelease() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Publish(rig.Package());

        await rig.Checker.CheckNowAsync();
        var available = Assert.IsType<ReleaseOutcome.Available>(rig.Checker.Outcome);
        Assert.Equal(BuildVersion, available.Release.Version);
        Assert.Contains("/latest", rig.Feed.Requests);

        var phases = new List<string>();
        rig.Updater.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MosaicUpdater.Phase)) phases.Add(rig.Updater.Phase.Name); };
        rig.Updater.Download(available.Release);
        await rig.Updater.Work;

        Assert.Equal(new UpdatePhase.Ready(BuildVersion.NumberString), rig.Updater.Phase);
        Assert.Equal(["downloading", "verifying", "ready"], phases.Distinct());
        var staged = rig.Updater.StagedFolder!;
        Assert.Equal(Path.Combine(rig.Root, "Apps", $".Mosaic.update-{BuildVersion.NumberString}"), staged);
        Assert.True(File.Exists(Path.Combine(staged, "Mosaic.exe")));
        Assert.Equal("// new agent", File.ReadAllText(Path.Combine(staged, "gecko", "agent.sys.mjs")));
        // The zip and signature are gone once unpacked; the install folder is untouched until quit.
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(rig.Root, "Data", "Updates")));
        Assert.False(File.Exists(Path.Combine(rig.Install, "gecko", "agent.sys.mjs")));

        // Installs as Mosaic quits: the helper gets this process, both folders and the arguments.
        Assert.True(rig.Updater.InstallIfReady([77, 0, 77]));
        var request = Assert.Single(rig.Launched);
        Assert.Equal(4242, request.ProcessId);
        Assert.Equal(rig.Install, request.Target);
        Assert.Equal(staged, request.Staged);
        Assert.False(request.Relaunch);
        Assert.Equal(["--mosaic-automation"], request.Arguments);
        Assert.Equal([77], request.WaitFor);
        // Only once.
        Assert.False(rig.Updater.InstallIfReady());
    });

    [Fact]
    public void RestartToUpdateAsksTheAppToQuitAndRelaunches() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Publish(rig.Package(topFolder: true));
        await rig.Checker.CheckNowAsync();
        var quits = 0;
        rig.Updater.QuitRequested += () =>
        {
            quits++;
            rig.Updater.InstallIfReady();  // what the app's window does as it closes
        };
        rig.Updater.RestartToUpdate();  // nothing ready yet
        Assert.Equal(0, quits);

        rig.Updater.Download(((ReleaseOutcome.Available)rig.Checker.Outcome!).Release);
        await rig.Updater.Work;
        Assert.IsType<UpdatePhase.Ready>(rig.Updater.Phase);
        // A zip with one top-level folder is flattened.
        Assert.True(File.Exists(Path.Combine(rig.Updater.StagedFolder!, "Mosaic.exe")));

        rig.Updater.RestartToUpdate();
        Assert.Equal(1, quits);
        Assert.True(Assert.Single(rig.Launched).Relaunch);
    });

    [Fact]
    public void AnythingNotSignedWithTheReleaseKeyIsDiscarded() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        var zip = rig.Package();
        rig.Publish(zip, signature: new TestSigningKey().Sign(zip));
        await rig.Checker.CheckNowAsync();
        rig.Updater.Download(((ReleaseOutcome.Available)rig.Checker.Outcome!).Release);
        await rig.Updater.Work;

        Assert.Equal(new UpdatePhase.Failed("The download is not signed with Mosaic’s release key, so it was discarded."), rig.Updater.Phase);
        Assert.Null(rig.Updater.StagedFolder);
        Assert.Equal(["Mosaic"], Directory.GetDirectories(Path.Combine(rig.Root, "Apps")).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(rig.Root, "Data", "Updates")));
        Assert.False(rig.Updater.InstallIfReady());
        Assert.Empty(rig.Launched);
    });

    [Fact]
    public void TheRealKeyRejectsATestSignedRelease() => TestLoop.Run(async () =>
    {
        // As the shipped app would see a zip signed by anyone but the release machine.
        using var rig = new Rig(publicKey: UpdatePackage.SigningPublicKey);
        rig.Publish(rig.Package());
        await rig.Checker.CheckNowAsync();
        rig.Updater.Download(((ReleaseOutcome.Available)rig.Checker.Outcome!).Release);
        await rig.Updater.Work;
        Assert.IsType<UpdatePhase.Failed>(rig.Updater.Phase);
        Assert.Contains("release key", rig.Updater.Phase.Detail);
    });

    [Fact]
    public void ATamperedZipIsDiscarded() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        var zip = rig.Package();
        var signature = rig.Key.Sign(zip);
        zip[zip.Length / 2] ^= 0x01;
        rig.Publish(zip, signature);
        await rig.Checker.CheckNowAsync();
        rig.Updater.Download(((ReleaseOutcome.Available)rig.Checker.Outcome!).Release);
        await rig.Updater.Work;
        Assert.Contains("not signed with Mosaic’s release key", rig.Updater.Phase.Detail);
    });

    [Fact]
    public void ASignedPackageMustStillBeMosaicOfThatVersionForThisProcessor() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        var other = MachineArchitectures.Process == MachineArchitecture.Arm64 ? PeImage.MachineX64 : PeImage.MachineArm64;
        rig.Publish(rig.Package(machine: other));
        await rig.Checker.CheckNowAsync();
        var release = ((ReleaseOutcome.Available)rig.Checker.Outcome!).Release;
        rig.Updater.Download(release);
        await rig.Updater.Work;
        Assert.Contains("not built for this PC’s processor", rig.Updater.Phase.Detail);
        Assert.Equal(["Mosaic"], Directory.GetDirectories(Path.Combine(rig.Root, "Apps")).Select(Path.GetFileName).Where(name => !name!.StartsWith('.')));

        rig.Publish(rig.Package(assemblyName: "Other.dll"));
        rig.Updater.Download(release);
        await rig.Updater.Work;
        Assert.Contains("not a Mosaic for Windows package", rig.Updater.Phase.Detail);
    });

    [Fact]
    public void NotNewerThanThisBuildIsUpToDate() => TestLoop.Run(async () =>
    {
        using var rig = new Rig(currentVersion: BuildVersion.NumberString);
        rig.Publish(rig.Package());
        await rig.Checker.CheckNowAsync();
        Assert.IsType<ReleaseOutcome.UpToDate>(rig.Checker.Outcome);
        Assert.NotNull(rig.Checker.LastCheck);
    });

    [Fact]
    public void AReleaseWithoutAWindowsZipCannotBeInstalled() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Feed.Add("Mosaic-9.0.0-macos-arm64.zip", [1, 2, 3]);
        rig.Feed.Publish("v9.0.0");
        await rig.Checker.CheckNowAsync();
        rig.Updater.Download(((ReleaseOutcome.Available)rig.Checker.Outcome!).Release);
        Assert.Contains("has no signed update for Windows", rig.Updater.Phase.Detail);
    });

    [Fact]
    public void AutomaticUpdatesDownloadWhatTheCheckFindsAndImplyDailyChecks() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Publish(rig.Package());
        Assert.False(rig.Updater.Automatic);
        Assert.Equal(UpdateCheckFrequency.Manual, rig.Checker.Frequency);

        // Daily checks, and the check that is due at once finds the release and downloads it.
        rig.Updater.Automatic = true;
        Assert.Equal(UpdateCheckFrequency.Daily, rig.Checker.Frequency);
        for (var i = 0; i < 300 && rig.Updater.Phase is not (UpdatePhase.Ready or UpdatePhase.Failed); i++) await Task.Delay(20);
        Assert.IsType<UpdatePhase.Ready>(rig.Updater.Phase);
        Assert.IsType<ReleaseOutcome.Available>(rig.Checker.Outcome);

        await rig.Store.FlushAsync();
        var reopened = new PlatformStore(rig.Store.Path);
        Assert.True(reopened.GetBool(MosaicUpdater.AutomaticKey));
        Assert.Equal("daily", reopened.GetString(UpdateChecker.FrequencyKey));
        Assert.NotNull(reopened.GetDate(UpdateChecker.LastCheckKey));
    });

    [Fact]
    public void TheScheduleChecksOnlyWhenDue() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        rig.Publish(rig.Package());
        rig.Checker.StartSchedule();
        await rig.Time.AdvanceAsync(TimeSpan.FromSeconds(31));
        await Task.Delay(200);
        Assert.DoesNotContain("/latest", rig.Feed.Requests);  // manual: never on its own

        rig.Checker.Frequency = UpdateCheckFrequency.Daily;  // due at once: never checked
        for (var i = 0; i < 100 && rig.Checker.Outcome is null; i++) await Task.Delay(20);
        Assert.IsType<ReleaseOutcome.Available>(rig.Checker.Outcome);
        var checks = rig.Feed.Requests.Count(path => path == "/latest");

        await rig.Time.AdvanceAsync(TimeSpan.FromHours(1));
        await Task.Delay(200);
        Assert.Equal(checks, rig.Feed.Requests.Count(path => path == "/latest"));
        rig.Checker.Dispose();
    });

    [Fact]
    public void CleansUpWhatAnEarlierRunLeftBehind() => TestLoop.Run(async () =>
    {
        using var rig = new Rig();
        var apps = Path.Combine(rig.Root, "Apps");
        Directory.CreateDirectory(Path.Combine(apps, ".Mosaic.update-0.2.0"));
        Directory.CreateDirectory(Path.Combine(apps, "Mosaic.previous-1234"));
        Directory.CreateDirectory(Path.Combine(apps, "Other"));
        Directory.CreateDirectory(Path.Combine(rig.Root, "Data", "Updates", "0.2.0"));
        File.WriteAllText(Path.Combine(rig.Root, "Data", "Updates", "install.log"), "installed");
        await rig.Updater.CleanLeftoversAsync();
        Assert.Equal(["Mosaic", "Other"], Directory.GetDirectories(apps).Select(Path.GetFileName).Order());
        Assert.Equal(["install.log"], Directory.GetFileSystemEntries(Path.Combine(rig.Root, "Data", "Updates")).Select(Path.GetFileName));
    });

    [Fact]
    public void LocationIsAssessedFromTheInstallFolder()
    {
        using var rig = new Rig();
        Assert.Null(rig.Updater.LocationProblem);
        var dev = new MosaicUpdater(new PlatformStore(null), new UpdaterOptions { InstallDirectory = Path.Combine(rig.Root, "bin") });
        Assert.IsType<UpdateLocationProblem.NotAPackage>(dev.LocationProblem);
        dev.Download(new Release(SoftwareVersion.Parse("9.0")!, "x", null, null, "", []));
        Assert.Equal(new UpdatePhase.Failed(new UpdateLocationProblem.NotAPackage().Message), dev.Phase);
    }
}
