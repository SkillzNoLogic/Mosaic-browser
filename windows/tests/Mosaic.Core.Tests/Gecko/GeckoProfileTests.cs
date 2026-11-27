using System.Text.Json.Nodes;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Tests.Gecko;

/// <summary>Port of <c>GeckoProfileTests</c> in <c>Tests/MosaicCoreTests/ExtensionStoreTests.swift</c>.</summary>
public class GeckoProfileTests
{
    [Fact]
    public void WritesPreferencesStylesheetAndGeometry()
    {
        using var profile = new TemporaryDirectory();

        GeckoProfile.Prepare(profile.Path, new GeckoRect(10, 20, 800, 600));

        var prefs = File.ReadAllText(profile.File("user.js"));
        Assert.Equal(GeckoProfile.UserJs, prefs);
        Assert.Contains("""user_pref("devtools.debugger.remote-enabled", true);""", prefs);
        Assert.Contains("""user_pref("xpinstall.signatures.required", false);""", prefs);
        Assert.Contains("""user_pref("browser.link.open_newwindow", 3);""", prefs);

        var css = File.ReadAllText(Path.Combine(profile.Path, "chrome", "userChrome.css"));
        Assert.Contains("#navigator-toolbox", css);

        var window = JsonNode.Parse(File.ReadAllText(profile.File("xulstore.json")))!
            ["chrome://browser/content/browser.xhtml"]!["main-window"]!.AsObject();
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["screenX"] = "10", ["screenY"] = "20", ["width"] = "800", ["height"] = "600", ["sizemode"] = "normal",
            },
            window.ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<string>()));
    }

    [Fact]
    public void LeavesGeometryAloneWhenUnknown()
    {
        using var profile = new TemporaryDirectory();

        GeckoProfile.Prepare(Path.Combine(profile.Path, "not", "yet", "there"));

        Assert.True(File.Exists(Path.Combine(profile.Path, "not", "yet", "there", "user.js")));
        Assert.False(File.Exists(Path.Combine(profile.Path, "not", "yet", "there", "xulstore.json")));
    }

    [Theory, InlineData(true), InlineData(false)]
    public void EveryPreferenceLineIsWellFormed(bool nativeTitleBar)
    {
        var lines = GeckoProfile.BuildUserJs(nativeTitleBar).Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        // Every line is a comment, blank, or a complete user_pref call.
        Assert.All(lines, line => Assert.True(
            line.Length == 0 || line.StartsWith("//") || (line.StartsWith("user_pref(\"") && line.EndsWith(");")), line));

        // No preference is set twice: the later one would silently win.
        var names = lines.Where(line => line.StartsWith("user_pref(")).Select(line => line.Split('"')[1]).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void TitleBarModeFollowsThePlatform()
    {
        // Windows: native title bar, which hidechrome removes without leaving a border behind.
        Assert.Contains("""user_pref("browser.tabs.inTitlebar", 0);""", GeckoProfile.BuildUserJs(nativeTitleBar: true));
        // macOS: as the macOS app has it.
        Assert.Contains("""user_pref("browser.tabs.inTitlebar", 1);""", GeckoProfile.BuildUserJs(nativeTitleBar: false));

        var expected = OperatingSystem.IsMacOS() ? 1 : 0;
        Assert.Contains($"user_pref(\"browser.tabs.inTitlebar\", {expected});", GeckoProfile.UserJs);
    }

    [Fact]
    public void TheDebuggingServerIsPinnedToLoopback() =>
        Assert.Contains("""user_pref("devtools.debugger.force-local", true);""", GeckoProfile.UserJs);

    [Fact]
    public void TheLauncherProcessIsNotSwitchedOff() =>
        // That preference is mirrored into the registry per installation and would change the
        // user's own Firefox; see the remarks on GeckoEngine.
        Assert.DoesNotContain("launcherProcess", GeckoProfile.UserJs);

    [Fact]
    public void AProfileNobodyHoldsIsNotLocked()
    {
        using var profile = new TemporaryDirectory();
        Assert.False(GeckoProfile.IsLocked(profile.Path));

        // Gecko leaves the file behind when it exits; only an open handle means "in use".
        File.WriteAllText(profile.File("parent.lock"), "");
        Assert.False(GeckoProfile.IsLocked(profile.Path));
    }

    [Fact]
    public void AProfileWhoseLockFileIsHeldOpenIsLocked()
    {
        using var profile = new TemporaryDirectory();
        // What Gecko does on Windows: keep parent.lock open with no sharing while it runs.
        using (new FileStream(profile.File("parent.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(GeckoProfile.IsLocked(profile.Path));
        }
        Assert.False(GeckoProfile.IsLocked(profile.Path));
    }

    [Fact]
    public async Task StoppingWaitsForTheProfileLockToBeReleased()
    {
        // A browser process ended by the operating system lets go of its lock a moment after
        // the launcher is gone; the shutdown waits for that rather than reporting Gecko gone.
        using var profile = new TemporaryDirectory();
        var held = new FileStream(profile.File("parent.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(400);
            await held.DisposeAsync();
        });

        Assert.True(await GeckoEngine.WaitForProfileReleaseAsync(profile.Path, TimeSpan.FromSeconds(5)));
        Assert.True(release.IsCompleted);
        Assert.False(GeckoProfile.IsLocked(profile.Path));
    }

    [Fact]
    public async Task WaitingForAProfileThatStaysLockedGivesUp()
    {
        using var profile = new TemporaryDirectory();
        using var held = new FileStream(profile.File("parent.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.False(await GeckoEngine.WaitForProfileReleaseAsync(profile.Path, TimeSpan.FromMilliseconds(300)));
    }
}
