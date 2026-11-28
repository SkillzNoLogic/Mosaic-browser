using System.Diagnostics;
using System.Text;
using Mosaic.Core.Platform;

namespace Mosaic.Core.Tests.Platform;

/// <summary>Runs only on Windows (the real WinVerifyTrust, PowerShell, process handling).</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Needs Windows.";
    }
}

/// <summary>
/// Runs on a Windows CI runner (or with MOSAIC_LIVE_TESTS=1 on Windows): tests that download from
/// Mozilla, which a developer's test run should not do every time.
/// </summary>
internal sealed class LiveWindowsFactAttribute : FactAttribute
{
    public LiveWindowsFactAttribute()
    {
        var live = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" || Environment.GetEnvironmentVariable("MOSAIC_LIVE_TESTS") == "1";
        if (!OperatingSystem.IsWindows() || !live) Skip = "Downloads from Mozilla; runs on the Windows CI runner (or set MOSAIC_LIVE_TESTS=1 on Windows).";
    }
}

/// <summary>Port of <c>UpdatePackageTests.swift</c>, plus the Windows helper.</summary>
public class UpdatePackageTests
{
    [Fact]
    public void AcceptsOnlyTheRightSignature()
    {
        var key = new TestSigningKey();
        var data = Encoding.UTF8.GetBytes("Mosaic update");
        var signature = key.Sign(data);
        Assert.True(UpdatePackage.Verify(data, signature + "\n", key.PublicKey));
        Assert.False(UpdatePackage.Verify(Encoding.UTF8.GetBytes("Mosaic updatf"), signature, key.PublicKey));
        Assert.False(UpdatePackage.Verify(data, signature, new TestSigningKey().PublicKey));
        Assert.False(UpdatePackage.Verify(data, "not base64!", key.PublicKey));
        Assert.False(UpdatePackage.Verify(data, "", key.PublicKey));
        Assert.False(UpdatePackage.Verify(data, Convert.ToBase64String(new byte[64]), key.PublicKey));
        Assert.False(UpdatePackage.Verify(data, signature, Convert.ToBase64String(new byte[31])));
        // The real key never signed this.
        Assert.False(UpdatePackage.Verify(data, signature));
    }

    [Fact]
    public void MatchesRfc8032AndCryptoKit()
    {
        // RFC 8032 §7.1, test 1 (empty message).
        var publicKey = Convert.ToBase64String(Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"));
        var signature = Convert.ToBase64String(Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"));
        Assert.True(UpdatePackage.Verify([], signature, publicKey));

        // Signed on a Mac with CryptoKit's Curve25519.Signing, which Scripts/sign-update.swift uses.
        Assert.True(UpdatePackage.Verify(Encoding.UTF8.GetBytes("Mosaic update signed by CryptoKit"),
            "01vyxG+qLelnZtnev6OKPCopAakAbkTx7t9P7nBMpxxf5kk81HPr5AouE+R2tNW3J/prq0yIWoZmInz/vnS6Ag==",
            "Q6sklf7ad4jYTrw8pmoHgWO1IiXsaGq8Bx38270C6cY="));
    }

    [Fact]
    public void CompiledInKeyIsTheMacOSKey()
    {
        Assert.Equal("Z61pZlb7FV58rHTravn/GMwYPCiFZA5/QSvpFehHi/c=", UpdatePackage.SigningPublicKey);
        var raw = Convert.FromBase64String(UpdatePackage.SigningPublicKey);
        Assert.Equal(32, raw.Length);
        Assert.True(Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.ValidatePublicKeyFull(raw, 0));
        var macOS = Path.Combine(RepositoryRoot(), "Sources", "MosaicCore", "Platform", "UpdatePackage.swift");
        if (File.Exists(macOS)) Assert.Contains($"\"{UpdatePackage.SigningPublicKey}\"", File.ReadAllText(macOS));
    }

    [Fact]
    public void LocationProblems()
    {
        var temp = Path.Combine("C:", "Users", "me", "AppData", "Local", "Temp");
        Assert.Null(UpdatePackage.LocationProblem(Path.Combine("C:", "Users", "me", "Mosaic"), temp, hasExecutable: true, folderWritable: true));
        Assert.IsType<UpdateLocationProblem.Temporary>(UpdatePackage.LocationProblem(
            Path.Combine(temp, "Temp1_Mosaic-0.3.1-windows-x64.zip"), temp, hasExecutable: true, folderWritable: true));
        Assert.IsType<UpdateLocationProblem.NotAPackage>(UpdatePackage.LocationProblem(Path.Combine("C:", "src", "bin"), temp, hasExecutable: false, folderWritable: true));
        var notWritable = Assert.IsType<UpdateLocationProblem.FolderNotWritable>(UpdatePackage.LocationProblem(
            Path.Combine("C:", "Program Files", "Mosaic"), temp, hasExecutable: true, folderWritable: false));
        Assert.Contains("Program Files", notWritable.Message);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\Program Files\", "\"C:\\Program Files\\\\\"")]
    [InlineData(@"a\\b", @"a\\b")]
    [InlineData("tab\there", "\"tab\there\"")]
    public void QuotesArgumentsTheWayWindowsSplitsThem(string argument, string expected) =>
        Assert.Equal(expected, WindowsCommandLine.Quote(argument));

    [Fact]
    public void PowerShellLiteralsDoubleEveryKindOfSingleQuote()
    {
        Assert.Equal("'it''s'", UpdatePackage.PowerShellLiteral("it's"));
        Assert.Equal("'a\u2019\u2019b'", UpdatePackage.PowerShellLiteral("a\u2019b"));
        Assert.Equal("'$env:x `n'", UpdatePackage.PowerShellLiteral("$env:x `n"));
    }

    [Fact]
    public void HelperScriptCarriesTheRequest()
    {
        var request = new UpdatePackage.HelperRequest(4242, @"C:\Apps\Mosaic\", @"C:\Apps\.Mosaic.update-0.3.1", Relaunch: true,
            ["--mosaic-automation", "it's here"], [77, 78], @"C:\Data\install.log");
        var script = UpdatePackage.HelperScript(request);
        Assert.StartsWith("function Invoke-MosaicUpdate", script);
        Assert.Contains("-ProcessId 4242 -Target 'C:\\Apps\\Mosaic' -Staged 'C:\\Apps\\.Mosaic.update-0.3.1' -Relaunch 'relaunch'", script);
        Assert.Contains("-Arguments '--mosaic-automation \"it''s here\"'", script);
        Assert.Contains("-WaitFor '77,78'", script);
        var arguments = UpdatePackage.HelperArguments(request);
        Assert.Equal("-EncodedCommand", arguments[^2]);
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(arguments[^1])));
        // Within Windows' command-line limit with room to spare.
        Assert.True(string.Join(' ', arguments).Length < 30_000);
    }

    [Fact]
    public void ReadsThePEMachine()
    {
        var directory = TestFolder();
        var arm = FakePe.Write(Path.Combine(directory, "arm.exe"), PeImage.MachineArm64);
        var x64 = FakePe.Write(Path.Combine(directory, "x64.exe"), PeImage.MachineX64);
        File.WriteAllText(Path.Combine(directory, "page.html"), "<html>Not found</html>");
        Assert.Equal(PeImage.MachineArm64, PeImage.Machine(arm));
        Assert.Equal(PeImage.MachineX64, PeImage.Machine(x64));
        Assert.Null(PeImage.Machine(Path.Combine(directory, "page.html")));
        Assert.Null(PeImage.Machine(Path.Combine(directory, "missing.exe")));
        Assert.True(PeImage.IsNative(PeImage.MachineArm64, MachineArchitecture.Arm64));
        Assert.False(PeImage.IsNative(PeImage.MachineX64, MachineArchitecture.Arm64));
        // The test host itself is a real PE for this process's architecture (on Windows).
        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } host)
        {
            Assert.True(PeImage.IsNative(PeImage.Machine(host), MachineArchitectures.Process));
        }
    }

    [Fact]
    public void ReadsTheVersionWithoutBuildMetadata()
    {
        var version = MosaicVersion.Read(typeof(UpdatePackage).Assembly);
        Assert.DoesNotContain("+", version);
        Assert.NotNull(SoftwareVersion.Parse(version));
    }

    // ---- The helper, for real (Windows) ----

    [WindowsFact]
    public void HelperWaitsForMosaicAndItsHelpersThenSwapsAndRelaunches()
    {
        var root = TestFolder();
        var (target, staged) = Installs(root);
        File.Copy(System32("PING.EXE"), Path.Combine(target, "mosaic-stand-in.exe"));
        File.Copy(System32("PING.EXE"), Path.Combine(target, "child-stand-in.exe"));
        // The new "Mosaic.exe" is cmd.exe, so the relaunch leaves a file behind.
        File.Copy(System32("cmd.exe"), Path.Combine(staged, "Mosaic.exe"));
        var marker = Path.Combine(root, "relaunched.txt");

        using var mosaic = Process.Start(new ProcessStartInfo(Path.Combine(target, "mosaic-stand-in.exe"), "-n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(target, "child-stand-in.exe"), "-n 6 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var log = Path.Combine(root, "install.log");
        var exitCode = RunHelper(new UpdatePackage.HelperRequest(mosaic.Id, target, staged, Relaunch: true,
            ["/c", "copy", "/y", "nul", marker], [], log));

        var output = File.Exists(log) ? File.ReadAllText(log) : "(no log)";
        Assert.True(exitCode == 0, output);
        Assert.True(mosaic.HasExited && child.HasExited, output);
        // The child outlived "Mosaic" by about three seconds and was waited for, not stopped.
        Assert.Equal(0, child.ExitCode);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "version.txt")));
        Assert.False(Directory.Exists(staged));
        Assert.Equal(["Mosaic"], Directory.GetDirectories(Path.Combine(root, "Apps")).Select(Path.GetFileName));
        Assert.Contains("installed", output);
        WaitFor(() => File.Exists(marker), TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(marker), output);
    }

    [WindowsFact]
    public void HelperStopsProcessesThatOutliveMosaic()
    {
        var root = TestFolder();
        var (target, staged) = Installs(root);
        File.Copy(System32("PING.EXE"), Path.Combine(target, "straggler.exe"));
        using var straggler = Process.Start(new ProcessStartInfo(Path.Combine(target, "straggler.exe"), "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var log = Path.Combine(root, "install.log");
        try
        {
            // Mosaic itself has already gone (a process ID nobody has).
            var exitCode = RunHelper(new UpdatePackage.HelperRequest(int.MaxValue - 7, target, staged, Relaunch: false, [], [], log));
            var output = File.ReadAllText(log);
            Assert.True(exitCode == 0, output);
            Assert.True(straggler.HasExited, output);
            Assert.Contains("outlived Mosaic", output);
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "version.txt")));
        }
        finally
        {
            if (!straggler.HasExited) straggler.Kill();
        }
    }

    [WindowsFact]
    public void HelperRestoresTheOldVersionWhenTheNewOneCannotBeMovedIn()
    {
        var root = TestFolder();
        var (target, staged) = Installs(root);
        Directory.Delete(staged, recursive: true);
        var log = Path.Combine(root, "install.log");
        var exitCode = RunHelper(new UpdatePackage.HelperRequest(int.MaxValue - 7, target, staged, Relaunch: false, [], [], log));
        Assert.Equal(1, exitCode);
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "version.txt")));
        Assert.Contains("old version was restored", File.ReadAllText(log));
        Assert.Equal(["Mosaic"], Directory.GetDirectories(Path.Combine(root, "Apps")).Select(Path.GetFileName));
    }

    private static (string Target, string Staged) Installs(string root)
    {
        var target = Directory.CreateDirectory(Path.Combine(root, "Apps", "Mosaic")).FullName;
        var staged = Directory.CreateDirectory(Path.Combine(root, "Apps", ".Mosaic.update-9.9.9")).FullName;
        File.WriteAllText(Path.Combine(target, "version.txt"), "old");
        File.WriteAllText(Path.Combine(staged, "version.txt"), "new");
        return (target, staged);
    }

    private static int RunHelper(UpdatePackage.HelperRequest request)
    {
        var start = new ProcessStartInfo(UpdatePackage.PowerShellPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in UpdatePackage.HelperArguments(request)) start.ArgumentList.Add(argument);
        using var helper = Process.Start(start)!;
        var output = helper.StandardOutput.ReadToEndAsync();
        Assert.True(helper.WaitForExit(90_000), "The helper did not finish within 90 seconds.");
        Assert.NotNull(output.Result);
        return helper.ExitCode;
    }

    private static string System32(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    private static void WaitFor(Func<bool> condition, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < limit) Thread.Sleep(100);
    }

    internal static string TestFolder() =>
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-data", "platform-" + Guid.NewGuid().ToString("N"))).FullName;

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Sources"))) directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}

/// <summary>Writes the smallest file <see cref="PeImage"/> reads as a Windows executable for a given machine.</summary>
internal static class FakePe
{
    public static string Write(string path, ushort machine)
    {
        var bytes = new byte[0x200];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(bytes, 0x80);
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
