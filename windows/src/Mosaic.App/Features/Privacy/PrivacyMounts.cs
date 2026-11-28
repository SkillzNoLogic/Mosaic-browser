using System.Runtime.CompilerServices;
using Mosaic.Core.Privacy;
using Mosaic.Core.Shell;

namespace Mosaic.App.Features.Privacy;

/// <summary>
/// Plugs the privacy area into the app: Settings › Privacy &amp; Security, the "blocker" toolbar
/// component, the <c>privacy</c> key of automation's <c>dump</c>, and the start-up wiring of the
/// Chromium side (<see cref="ChromiumPrivacy"/>). Registration only stores delegates; nothing runs
/// before the window is up.
/// </summary>
internal static class PrivacyMounts
{
    /// <summary>The Chromium side, once started; the winui area's WebView2 hooks reach it through <see cref="ChromiumPrivacy"/>'s statics.</summary>
    internal static ChromiumPrivacy? Chromium { get; private set; }

    [ModuleInitializer]
    internal static void Register()
    {
        FeatureMounts.SettingsPages[SettingsCategory.Privacy] = model => new PrivacySettingsPage(model);
        FeatureMounts.Components["blocker"] = model => new BlockerButton(model);
        FeatureMounts.Startup.Add(("privacy", (model, window) =>
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            Chromium = new ChromiumPrivacy(
                () => model.Privacy,
                () => model.Blocker.IsAvailable ? model.Blocker.Filter : null,
                () => handle,
                model.ChromiumAvailable);
            PrivacyAutomation.RegisterState();
            model.PrivacySettingsRequested += () => ShellBridge.ShowSettings(model, SettingsCategory.Privacy);
            model.StartPrivacy(Chromium);
        }));
    }
}
