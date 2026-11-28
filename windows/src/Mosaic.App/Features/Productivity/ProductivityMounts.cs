using System.Runtime.CompilerServices;
using Mosaic.Core.Productivity;
using Mosaic.Core.Shell;

namespace Mosaic.App.Features.Productivity;

/// <summary>
/// Plugs the productivity area into the app: the Shortcuts &amp; Gestures settings page, the
/// new-tab page (Speed Dial), the toolbar's reader button, the command palette and the
/// window's keyboard accelerators. Only stores delegates (nothing runs before the window).
/// </summary>
internal static class ProductivityMounts
{
    [ModuleInitializer]
    internal static void Register()
    {
        FeatureMounts.SettingsPages[SettingsCategory.Shortcuts] = model => new ShortcutsSettingsPage(model);
        // mosaic://newtab, and the same view for a shell that embeds Speed Dial in its start page.
        FeatureMounts.InternalPages["newtab"] = model => new SpeedDialView(model);
        FeatureMounts.Components["speedDial"] = model => new SpeedDialView(model);
        FeatureMounts.Components["reader"] = model => new ReaderButton(model);
        FeatureMounts.Startup.Add(("productivity", ProductivityStartup.Start));

        ProductivityAutomation.Register();
        PageScripting.Resolver = WebView2ScriptHost.Resolve;
        ProductivityIntegration.CopyText = Clipboard.CopyText;
        FaviconCache.Reencode = IconImaging.ReencodeAsync;
        PaletteRegistry.Register("mosaic.pages", InternalPagesPalette.Actions);
    }
}
