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
        AddIcons();
        FeatureMounts.Overlays.Add(("engine.page", model => new PageOverlayView(model)));
        FeatureMounts.Startup.Add(("engine", (model, window) =>
        {
            // Created now (not on first use) so the engines' permission requests, pop-ups and
            // dialogs are Mosaic's from the first page on. No file is read until one is needed.
            model.Pages.Host = new EngineHost(model, window);
            _ = new ChromiumPageBridge(model);
        }));
    }

    /// <summary>
    /// Names the engine area's menus use that the system area's table lacks (yet); only added
    /// when missing, so the system area's choice wins once it has one. Same glyph in both fonts.
    /// </summary>
    private static void AddIcons()
    {
        (string Name, int CodePoint)[] glyphs =
        [
            ("arrow.triangle.swap", 0xE8AB),                     // Switch
            ("camera.viewfinder", 0xE722),                       // Camera
            ("doc.viewfinder", 0xE7C3),                          // Page
            ("doc.richtext", 0xEA90),                            // PDF
            ("plus.magnifyingglass", 0xE8A3),                    // ZoomIn
            ("minus.magnifyingglass", 0xE71F),                   // ZoomOut
            ("1.magnifyingglass", 0xE71E),                       // Zoom
            ("printer", 0xE749),                                 // Print
            ("chevron.left.forwardslash.chevron.right", 0xE943), // Code
        ];
        foreach (var (name, codePoint) in glyphs)
        {
            if (!System.Icons.Has(name)) System.Icons.Add(name, codePoint, codePoint);
        }
    }
}
