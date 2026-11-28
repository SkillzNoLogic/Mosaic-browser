using Microsoft.UI.Dispatching;
using Mosaic.Core.WindowsIntegration;
using Windows.UI.ViewManagement;

namespace Mosaic.App.Features.System;

/// <summary>
/// Feeds <see cref="SystemAppearanceMonitor"/> from Windows: UISettings (app light/dark mode,
/// accent colours, transparency, animations, text scale) and AccessibilitySettings (high
/// contrast). Started on the first subscription or read, not at startup. Windows raises these
/// events on background threads; readings are taken and published on the UI thread.
/// </summary>
internal static class AppearanceListener
{
    // Kept alive for the process: the events stop when these are collected.
    private static UISettings? settings;
    private static AccessibilitySettings? accessibility;
    private static DispatcherQueue? dispatcher;

    public static string Status { get; private set; } = "not started";

    public static void Start()
    {
        dispatcher = SystemIntegration.Dispatcher ?? DispatcherQueue.GetForCurrentThread();
        try
        {
            settings = new UISettings();
            accessibility = new AccessibilitySettings();
            settings.ColorValuesChanged += (_, _) => Refresh();
            settings.AdvancedEffectsEnabledChanged += (_, _) => Refresh();
            settings.TextScaleFactorChanged += (_, _) => Refresh();
            accessibility.HighContrastChanged += (_, _) => Refresh();
            Status = "listening";
            Read();
        }
        catch (Exception error)
        {
            Status = "failed: " + error.Message;
        }
    }

    private static void Refresh()
    {
        if (dispatcher is null || dispatcher.HasThreadAccess) Read();
        else dispatcher.TryEnqueue(Read);
    }

    private static void Read()
    {
        if (settings is null || accessibility is null) return;
        try
        {
            static string Hex(Windows.UI.Color c) => SystemAppearance.Hex(c.R, c.G, c.B);
            var background = settings.GetColorValue(UIColorType.Background);
            var accent = new Dictionary<string, string>
            {
                ["accent"] = Hex(settings.GetColorValue(UIColorType.Accent)),
                ["light1"] = Hex(settings.GetColorValue(UIColorType.AccentLight1)),
                ["light2"] = Hex(settings.GetColorValue(UIColorType.AccentLight2)),
                ["light3"] = Hex(settings.GetColorValue(UIColorType.AccentLight3)),
                ["dark1"] = Hex(settings.GetColorValue(UIColorType.AccentDark1)),
                ["dark2"] = Hex(settings.GetColorValue(UIColorType.AccentDark2)),
                ["dark3"] = Hex(settings.GetColorValue(UIColorType.AccentDark3)),
            };
            var highContrast = accessibility.HighContrast;
            SystemAppearanceMonitor.Update(new SystemAppearance(
                Dark: background.R + background.G + background.B < 384,  // the app-mode background is black in dark mode
                HighContrast: highContrast,
                HighContrastScheme: highContrast ? accessibility.HighContrastScheme : null,
                Accent: accent,
                TransparencyEnabled: settings.AdvancedEffectsEnabled,
                AnimationsEnabled: settings.AnimationsEnabled,
                TextScale: settings.TextScaleFactor));
        }
        catch (Exception error)
        {
            Status = "failed: " + error.Message;
        }
    }
}
