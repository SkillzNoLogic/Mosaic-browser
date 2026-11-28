using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Shell;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// Settings › Accessibility (port of macOS's <c>AccessibilitySettingsPane</c>): motion,
/// transparency, interface size, tab close buttons and focus rings, with Windows's own settings
/// (animation effects, transparency effects, contrast themes) respected and linked.
/// </summary>
internal sealed class AccessibilitySettingsPage : Grid
{
    public AccessibilitySettingsPage(ShellRoot shell)
    {
        var settings = shell.Model.ShellSettings;
        var theme = shell.Theme;
        var kit = new SettingsKit(theme);
        static string State(bool on) => on ? "on" : "off";
        var overrides = Enum.GetValues<AccessibilityOverride>().Select(o => (o, o.Title())).ToList();

        kit.Section("Motion and Transparency");
        kit.Card("Reduce motion", $"Turns off sliding and fading in Mosaic’s interface. Windows › Animation effects: {State(theme.SystemAnimations)}.",
            SettingsKit.Combo(overrides, settings.ReduceMotion, o => settings.ReduceMotion = o, "Reduce motion"));
        kit.Card("Reduce transparency", $"Replaces Mica and Acrylic with solid surfaces. Windows › Transparency effects: {State(theme.SystemTransparencyEffects)}.",
            SettingsKit.Combo(overrides, settings.ReduceTransparency, o => settings.ReduceTransparency = o, "Reduce transparency"));

        kit.Section("Text and Controls");
        kit.Card("Interface size", "Enlarges the toolbar, tabs, sidebar and their text. To enlarge web pages, use page zoom (Ctrl+Plus).",
            SettingsKit.Radios(Enum.GetValues<TextSize>().Select(s => (s, s.Title())), settings.TextSize, s => settings.TextSize = s, "Interface size"));
        kit.Card("Always show close buttons on tabs", null,
            SettingsKit.Toggle(settings.AlwaysShowTabCloseButtons, on => settings.AlwaysShowTabCloseButtons = on, "Always show close buttons on tabs"));

        kit.Section("Contrast");
        kit.Card("Contrast themes", theme.SystemHighContrast
                ? "A contrast theme is on: Mosaic uses its colours everywhere, and Mica, gradients and the accent step aside."
                : "Mosaic follows Windows contrast themes: turn one on in Windows Settings and Mosaic switches to its colours.",
            SettingsKit.Button("Contrast Themes…", () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:easeofaccess-highcontrast"))));

        kit.Section("Keyboard");
        kit.Card("Prominent focus rings", "Draws a thick ring in the accent colour around the focused button, field or tab.",
            SettingsKit.Toggle(settings.ProminentFocusRings, on => settings.ProminentFocusRings = on, "Prominent focus rings"));
        kit.Note("Tab and Shift+Tab reach every control; arrow keys move within the settings list. Ctrl+L, Alt+D or F6 focus the address bar, "
            + "Ctrl+T opens a tab, Ctrl+1–9 select tabs, Alt+F opens the menu, Ctrl+Shift+, switches vertical tabs, Ctrl+, opens Settings and Esc closes them.");
        Children.Add(kit.Scroller());
    }
}
