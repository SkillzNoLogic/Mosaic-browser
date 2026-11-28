using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Browser;
using Mosaic.Core.Performance;

namespace Mosaic.App.Features.Performance;

/// <summary>
/// The perf area's wiring: start-up marks that need no other area's code, and the
/// <c>performance</c> key of the automation <c>dump</c> (see <see cref="StartupTrace"/>).
/// </summary>
internal static class PerformanceMounts
{
    /// <summary>
    /// Runs as Mosaic.App's module is loaded, just before <c>Program.Main</c>. Stores two
    /// delegates and a timestamp; nothing else, so that it costs start-up nothing measurable.
    /// </summary>
    [ModuleInitializer]
    internal static void Register()
    {
        StartupTrace.Mark(StartupTrace.Main);
        FeatureMounts.Startup.Add(("perf", Started));
        // With automation on, System.Text.Json is loaded for the pipe anyway, so the dump key
        // can be there from the first request. Otherwise it is registered once the window is up,
        // keeping that assembly off the start-up path of a normal launch.
        if (Array.IndexOf(Environment.GetCommandLineArgs(), Automation.Flag) >= 0) RegisterDumpState();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterDumpState() =>
        AutomationRegistry.RegisterState("performance", _ => StartupTrace.ToJson());

    /// <summary>The startup hook: called once, on the UI thread, with the model and the window.</summary>
    private static void Started(BrowserModel model, Window window)
    {
        RegisterDumpState();
        WatchWindow(window);
        StartupProbe.Attach(model);
    }

    private static void WatchWindow(Window window)
    {
        if (!StartupTrace.Has(StartupTrace.WindowActivated))
        {
            window.Activated += Activated;

            void Activated(object sender, WindowActivatedEventArgs args)
            {
                window.Activated -= Activated;
                StartupTrace.Mark(StartupTrace.WindowActivated);
            }
        }

        if (!StartupTrace.Has(StartupTrace.FirstFrame))
        {
            // Rendering fires for every frame while anyone listens (and keeps the compositor
            // busy), so the handler removes itself at once.
            CompositionTarget.Rendering += Rendered;

            static void Rendered(object? sender, object args)
            {
                CompositionTarget.Rendering -= Rendered;
                StartupTrace.Mark(StartupTrace.FirstFrame);
            }
        }
    }
}
