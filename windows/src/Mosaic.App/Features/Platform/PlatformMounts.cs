using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Mosaic.Core.Platform;
using Mosaic.Core.Shell;

namespace Mosaic.App.Features.Platform;

/// <summary>
/// Plugs the platform area into the app: the Engine, Extensions, Languages and About &amp; Updates
/// settings pages, the "extensions" toolbar component, the "Mozilla engine not installed" overlay,
/// and a start-up hook that activates the platform services (safe engine switching, languages for
/// Gecko, automation, the update schedule) and installs a downloaded update as the window closes.
/// Registration only stores delegates: nothing here runs before the first window is shown.
/// </summary>
internal static class PlatformMounts
{
    /// <summary>Mosaic's window, for file pickers and closing it for Restart to Update.</summary>
    internal static Window? Window { get; private set; }

    [ModuleInitializer]
    internal static void Register()
    {
        FeatureMounts.SettingsPages[SettingsCategory.Engine] = model => new EngineSettingsPage(model);
        FeatureMounts.SettingsPages[SettingsCategory.Extensions] = model => new ExtensionsSettingsPage(model);
        FeatureMounts.SettingsPages[SettingsCategory.Languages] = model => new LanguagesSettingsPage(model);
        FeatureMounts.SettingsPages[SettingsCategory.About] = model => new AboutSettingsPage(model);
        FeatureMounts.Components["extensions"] = model => new ExtensionToolbarButtons(model);
        FeatureMounts.Overlays.Add(("platform.geckoMissing", model => new GeckoMissingOverlay(model)));
        FeatureMounts.Startup.Add(("platform", Start));
    }

    private static void Start(Mosaic.Core.Browser.BrowserModel model, Window window)
    {
        Window = window;
        var platform = model.Platform;
        try
        {
            platform.Options = new PlatformOptions { SystemLanguages = SystemLanguages };
        }
        catch (InvalidOperationException)
        {
            // Another area touched the services first; the default (Windows' display language) applies.
        }
        model.ActivatePlatform();
        platform.Languages.ChromiumApplied = PlatformChromium.Applied;
        platform.Updater.QuitRequested += window.Close;
        window.Closed += (_, _) =>
        {
            // Hands a downloaded update to the helper, which finishes after Mosaic and its
            // engines' processes have exited.
            platform.Updater.InstallIfReady(model.Gecko.ProcessId is { } gecko ? [gecko] : []);
            platform.Store.FlushAsync().Wait(TimeSpan.FromSeconds(1));
        };
    }

    /// <summary>The user's languages from Settings › Time &amp; language, most preferred first.</summary>
    private static IReadOnlyList<string> SystemLanguages()
    {
        try
        {
            return [.. global::Windows.System.UserProfile.GlobalizationPreferences.Languages];
        }
        catch (Exception)
        {
            return [global::System.Globalization.CultureInfo.CurrentUICulture.Name];
        }
    }
}
