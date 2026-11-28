using System.Runtime.CompilerServices;
using Mosaic.Core.Engine;

namespace Mosaic.App.Features.Engine;

/// <summary>
/// The engine area's plugs: the page overlays (find bar, permission prompts, pop-up notices,
/// JavaScript dialogs, with the page shortcuts), the WebView2 hooks for each Chromium tab, the
/// window services, and the <c>page.*</c> automation commands.
/// </summary>
internal static class EngineMounts
{
    [ModuleInitializer]
    internal static void Register()
    {
        // Only delegates are stored here; nothing runs before the first window.
        EngineAutomation.Register();
        FeatureMounts.Overlays.Add(("engine.page", model => new PageOverlayView(model)));
        FeatureMounts.Startup.Add(("engine", (model, window) =>
        {
            // Created now (not on first use) so the engines' permission requests, pop-ups and
            // dialogs are Mosaic's from the first page on. No file is read until one is needed.
            model.Pages.Host = new EngineHost(model, window);
            _ = new ChromiumPageBridge(model);
        }));
    }
}
