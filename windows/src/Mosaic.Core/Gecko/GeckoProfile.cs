using System.Globalization;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Gecko;

/// <summary>
/// The dedicated Gecko profile Mosaic runs its engine with, so the user's own Firefox profile
/// is never read or changed.
/// </summary>
/// <remarks>Port of <c>GeckoProfile</c> in <c>Sources/MosaicCore/Gecko/GeckoRuntime.swift</c>.</remarks>
public static class GeckoProfile
{
    /// <summary>Writes the files that must be in place before Gecko starts (user.js, chrome/userChrome.css,
    /// and xulstore.json when an initial rectangle is given).</summary>
    /// <remarks>
    /// The rectangle is written in the units Gecko persists window geometry in, which are CSS
    /// pixels. On a display scaled above 100% that makes it an approximate opening position only;
    /// it is exact again as soon as the window is embedded or <c>setRect</c> is called.
    /// </remarks>
    public static void Prepare(string profileDirectory, GeckoRect? initialRect = null)
    {
        var chrome = Path.Combine(profileDirectory, "chrome");
        Directory.CreateDirectory(chrome);
        File.WriteAllText(Path.Combine(profileDirectory, "user.js"), UserJs);
        File.WriteAllText(Path.Combine(chrome, "userChrome.css"), UserChromeCss);

        // Open the window where it will be shown, instead of wherever Gecko fancies.
        if (initialRect is { } rect)
        {
            static JsonNode Text(int value) => value.ToString(CultureInfo.InvariantCulture);
            var geometry = new JsonObject
            {
                ["chrome://browser/content/browser.xhtml"] = new JsonObject
                {
                    ["main-window"] = new JsonObject
                    {
                        ["screenX"] = Text(rect.X),
                        ["screenY"] = Text(rect.Y),
                        ["width"] = Text(rect.Width),
                        ["height"] = Text(rect.Height),
                        ["sizemode"] = "normal",
                    },
                },
            };
            File.WriteAllText(Path.Combine(profileDirectory, "xulstore.json"), geometry.ToJsonString());
        }
    }

    /// <summary>Preferences applied on every start, for the platform Mosaic is running on.</summary>
    public static string UserJs => BuildUserJs(nativeTitleBar: !OperatingSystem.IsMacOS());

    /// <summary>Hides every piece of Firefox UI so only web content is left in the window.</summary>
    public static string UserChromeCss => """
        /* Written by Mosaic on every launch; edits here are overwritten. */
        /* Removed from layout entirely. Merely collapsing it leaves, on macOS, Gecko's native
           window buttons (positioned from the title-bar button box inside it) poking into
           the corner of the page as a few stray pixels. On Windows everything Gecko could
           still draw up there is inside this box as well: the tab strip, the caption
           buttons, and the menu bar that the Alt key reveals. */
        #navigator-toolbox {
          display: none !important;
        }
        #sidebar-box, #sidebar-splitter, #sidebar-main, #sidebar-launcher-splitter,
        #ai-window-box, #ai-window-splitter {
          display: none !important;
        }
        #browser, #appcontent, #tabbrowser-tabbox, #tabbrowser-tabpanels, .browserStack {
          margin: 0 !important;
          border: none !important;
          border-radius: 0 !important;
          box-shadow: none !important;
        }
        :root {
          --tabpanel-background-color: transparent !important;
        }

        """;

    /// <summary>
    /// True while a running Gecko holds this profile, as far as that can be observed.
    /// </summary>
    /// <remarks>
    /// On Windows Gecko keeps <c>parent.lock</c> open with no sharing for as long as it runs
    /// (<c>CreateFileW(…, 0 /* no sharing */, …)</c> in toolkit/profile/nsProfileLock.cpp), so
    /// failing to open that file is proof of a live Gecko on the profile. Elsewhere Gecko uses an
    /// <c>fcntl</c> lock on <c>.parentlock</c>, which cannot be probed from managed code, and this
    /// returns false: there the engine notices a held profile after launching instead.
    /// </remarks>
    internal static bool IsLocked(string profileDirectory)
    {
        var lockFile = Path.Combine(profileDirectory, "parent.lock");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var probe = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true; // sharing violation: somebody has it open
        }
        catch (UnauthorizedAccessException)
        {
            return false; // unreadable for an unrelated reason; Gecko will report it better than we can
        }
    }

    /// <param name="nativeTitleBar">
    /// Which of Gecko's two title-bar modes leaves a clean borderless window once the agent sets
    /// <c>hidechrome</c>; see the comment on <c>browser.tabs.inTitlebar</c> in the output.
    /// </param>
    internal static string BuildUserJs(bool nativeTitleBar) => $$"""
        // Written by Mosaic on every launch; edits here are overwritten.

        // -- Remote control: lets Mosaic load its agent into Gecko over the debugging server.
        user_pref("devtools.debugger.remote-enabled", true);
        user_pref("devtools.chrome.enabled", true);
        user_pref("devtools.debugger.prompt-connection", false);
        // On Windows the server is a TCP port (Gecko has no socket-path transport there).
        // This keeps it on the loopback interface whatever Gecko's default becomes.
        user_pref("devtools.debugger.force-local", true);

        // -- Mosaic draws the tabs and toolbar, so Gecko's own are hidden (chrome/userChrome.css).
        user_pref("toolkit.legacyUserProfileCustomizations.stylesheets", true);
        // The agent makes the window borderless with the "hidechrome" attribute. Which title-bar
        // mode that combines with cleanly differs by platform. macOS: tabs in the title bar (1),
        // so there is no separate title bar to begin with. Windows: a native title bar (0).
        // With tabs in the title bar Gecko computes its own non-client area from system
        // metrics, and once hidechrome removes the real frame it would keep reserving a
        // resize border's width of dead pixels along the left, right and bottom edges
        // (nsWindow::NormalWindowNonClientOffset, widget/windows/nsWindow.cpp). A native
        // title bar is sized by Windows from the window's styles, and hidechrome clears them.
        user_pref("browser.tabs.inTitlebar", {{(nativeTitleBar ? 0 : 1)}});
        user_pref("sidebar.revamp", false);
        // Full-screen video fills Gecko's window (Mosaic's content area) rather than asking the
        // window manager for a full-screen window. On Windows that request would also restore
        // the window styles hidechrome removed when leaving full screen, under an embedding host.
        user_pref("full-screen-api.ignore-widgets", true);
        // Gecko stops painting windows it believes are covered. That is worked out for top-level
        // windows only (widget/windows/WinWindowOcclusionTracker.cpp enumerates them), so a
        // window embedded in Mosaic's would keep whatever verdict it had when it was embedded.
        user_pref("widget.windows.window_occlusion_tracking.enabled", false);

        // -- One window, tabs only: Mosaic mirrors Gecko's tab list.
        user_pref("browser.link.open_newwindow", 3);
        user_pref("browser.link.open_newwindow.restriction", 0);
        user_pref("browser.tabs.closeWindowWithLastTab", false);
        user_pref("browser.tabs.warnOnClose", false);
        user_pref("browser.warnOnQuit", false);
        // Mosaic owns the tab list. This matters most on Windows, where an engine that outlives
        // a crashed Mosaic is killed outright and would otherwise offer to restore its session.
        user_pref("browser.sessionstore.resume_from_crash", false);
        user_pref("browser.sessionstore.max_resumed_crashes", 0);
        user_pref("browser.startup.page", 0);
        user_pref("browser.startup.homepage", "about:blank");
        user_pref("browser.newtabpage.enabled", false);

        // -- No first-run pages, default-browser prompts or tours.
        user_pref("browser.shell.checkDefaultBrowser", false);
        user_pref("browser.aboutwelcome.enabled", false);
        user_pref("browser.preonboarding.enabled", false);
        user_pref("browser.startup.homepage_override.mstone", "ignore");
        user_pref("startup.homepage_welcome_url", "");
        user_pref("startup.homepage_welcome_url.additional", "");
        user_pref("startup.homepage_override_url", "");
        user_pref("browser.uitour.enabled", false);
        user_pref("datareporting.policy.dataSubmissionEnabled", false);
        user_pref("datareporting.healthreport.uploadEnabled", false);
        user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);
        // Effective on macOS and Linux. On Windows this setting is stored per installation, not
        // per profile (toolkit/modules/UpdateUtils.sys.mjs), so the line is ignored there and
        // updates follow whatever the Firefox installation is configured to do.
        user_pref("app.update.auto", false);

        // -- Nothing this profile does may leak into the user's own Firefox, which can be the
        //    very same installation.
        // A new profile can register the installation to start with the operating system
        // (browser/components/defaultlaunchonlogin); claim that has been dealt with already.
        user_pref("browser.startup.windowsLaunchOnLogin.alreadyApplied", true);
        // Experiments switch features on by rewriting preferences, including ones set here.
        user_pref("app.shield.optoutstudies.enabled", false);
        // Windows taskbar jump lists belong to the installation, so sites visited in Mosaic
        // would show up under the user's Firefox icon.
        user_pref("browser.taskbar.lists.enabled", false);

        // -- Converted Chrome extensions are unsigned by definition.
        user_pref("xpinstall.signatures.required", false);
        user_pref("extensions.autoDisableScopes", 0);
        user_pref("extensions.manifestV3.enabled", true);

        """;
}
