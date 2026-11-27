using Mosaic.Core.Gecko;

namespace Mosaic.Core.Tests.Gecko;

public class GeckoRuntimeTests
{
    private const string ApplicationIni = """
        ; This file is not used. If you modify it and want the application to use
        ; your modifications, move it under the browser/ subdirectory.
        [App]
        Vendor=Mozilla
        Name=Firefox
        RemotingName=firefox-esr
        Version=153.8.0
        BuildID=20261117185949

        [Gecko]
        MinVersion=153.8.0
        MaxVersion=153.8.0
        """;

    private static string WindowsInstall(TemporaryDirectory root, string executable = "firefox.exe", string? ini = ApplicationIni)
    {
        File.WriteAllText(root.File(executable), "");
        File.WriteAllText(root.File("xul.dll"), "");
        if (ini is not null) File.WriteAllText(root.File("application.ini"), ini);
        return root.File(executable);
    }

    [Fact]
    public void LookupPrefersTheOverrideThenTheManagedCopyThenInstalledBrowsers()
    {
        var roots = new[] { Path.Combine("C:", "Program Files"), Path.Combine("C:", "Users", "me", "AppData", "Local") };

        var candidates = GeckoRuntime.CandidatesFor(Path.Combine("D:", "ff", "firefox.exe"), roots);

        Assert.Equal(Path.Combine("D:", "ff", "firefox.exe"), candidates[0]);
        Assert.Equal(GeckoRuntime.ManagedExecutablePath, candidates[1]);
        Assert.Equal(Path.Combine(AppPaths.ManagedGecko, "firefox.exe"), candidates[1]);
        Assert.Equal(Path.Combine(roots[0], "Mozilla Firefox", "firefox.exe"), candidates[2]);
        Assert.Contains(Path.Combine(roots[0], "Firefox Developer Edition", "firefox.exe"), candidates);
        Assert.Contains(Path.Combine(roots[0], "Firefox Nightly", "firefox.exe"), candidates);
        Assert.Contains(Path.Combine(roots[0], "LibreWolf", "librewolf.exe"), candidates);
        Assert.Contains(Path.Combine(roots[0], "Zen Browser", "zen.exe"), candidates);
        Assert.Contains(Path.Combine(roots[1], "Mozilla Firefox", "firefox.exe"), candidates);
        // Everything under the first root comes before anything under the second.
        Assert.True(candidates.ToList().FindLastIndex(path => path.StartsWith(roots[0])) <
                    candidates.ToList().FindIndex(path => path.StartsWith(roots[1])));
    }

    [Fact]
    public void WithoutAnOverrideTheManagedCopyComesFirst()
    {
        Assert.Equal(GeckoRuntime.ManagedExecutablePath, GeckoRuntime.CandidatesFor(null, [])[0]);
        Assert.Equal(GeckoRuntime.ManagedExecutablePath, GeckoRuntime.CandidatesFor("  ", [])[0]);
        Assert.Contains(GeckoRuntime.ManagedExecutablePath, GeckoRuntime.Candidates());
    }

    [Fact]
    public void DescribesAWindowsInstallFromItsApplicationIni()
    {
        using var install = new TemporaryDirectory();
        var executable = WindowsInstall(install);

        var runtime = GeckoRuntime.FromExecutable(executable);

        Assert.Equal(new GeckoRuntime(executable, "Firefox", "153.8.0", false), runtime);
        Assert.True(GeckoRuntime.FromExecutable(executable, isManaged: true)!.IsManaged);
    }

    [Fact]
    public void DescribesAForkByItsOwnName()
    {
        using var install = new TemporaryDirectory();
        var executable = WindowsInstall(install, "librewolf.exe", "[App]\r\nName = LibreWolf\r\n\r\n[Gecko]\r\nMinVersion=1\r\n");

        var runtime = GeckoRuntime.FromExecutable(executable);

        Assert.Equal("LibreWolf", runtime!.Name);
        Assert.Equal("?", runtime.Version);
    }

    [Fact]
    public void RecognisesTheExecutableInsideAMacOSBundle()
    {
        using var bundle = new TemporaryDirectory();
        var macOS = Directory.CreateDirectory(Path.Combine(bundle.Path, "Firefox.app", "Contents", "MacOS")).FullName;
        var resources = Directory.CreateDirectory(Path.Combine(bundle.Path, "Firefox.app", "Contents", "Resources")).FullName;
        File.WriteAllText(Path.Combine(macOS, "firefox"), "");
        File.WriteAllText(Path.Combine(macOS, "XUL"), "");
        File.WriteAllText(Path.Combine(resources, "application.ini"), ApplicationIni);

        var runtime = GeckoRuntime.FromExecutable(Path.Combine(macOS, "firefox"));

        Assert.Equal("Firefox", runtime!.Name);
        Assert.Equal("153.8.0", runtime.Version);
    }

    [Fact]
    public void AFolderThatMerelyHasTheRightNameIsNotARuntime()
    {
        using var install = new TemporaryDirectory();
        Assert.Null(GeckoRuntime.FromExecutable(""));
        Assert.Null(GeckoRuntime.FromExecutable(install.File("firefox.exe")));
        Assert.Null(GeckoRuntime.FromExecutable(install.Path));

        // An executable alone, with no engine library beside it.
        File.WriteAllText(install.File("firefox.exe"), "");
        File.WriteAllText(install.File("application.ini"), ApplicationIni);
        Assert.Null(GeckoRuntime.FromExecutable(install.File("firefox.exe")));
    }

    [Fact]
    public void AnInstallWithoutAGeckoManifestIsNotARuntime()
    {
        using var missing = new TemporaryDirectory();
        Assert.Null(GeckoRuntime.FromExecutable(WindowsInstall(missing, ini: null)));

        using var foreign = new TemporaryDirectory();
        Assert.Null(GeckoRuntime.FromExecutable(WindowsInstall(foreign, ini: "[App]\nName=Something\nVersion=1\n")));
    }

    [Fact]
    public void LocatesTheFirstCandidateThatIsReal()
    {
        using var install = new TemporaryDirectory();
        var executable = WindowsInstall(install);

        var runtime = GeckoRuntime.LocateAmong([install.File("missing.exe"), executable]);

        Assert.Equal(executable, runtime!.ExecutablePath);
        Assert.False(runtime.IsManaged);
        Assert.Null(GeckoRuntime.LocateAmong([install.File("missing.exe")]));
    }
}
