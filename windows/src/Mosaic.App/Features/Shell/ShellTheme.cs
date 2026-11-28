using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// The colours, metrics and switches every shell view draws with, for the current Mosaic,
/// light/dark, accent, contrast theme and the Windows/Accessibility settings (port of macOS's
/// <c>ShellTheme</c>, <c>ShellColors</c> and <c>ShellMetrics</c>).
/// </summary>
/// <remarks>
/// The brushes are long-lived: a change of light/dark or accent updates their colours in place,
/// so every view that uses them follows without being rebuilt. In a contrast theme they take
/// the system colours and Mosaic's own colours (accent, backgrounds) step aside.
/// </remarks>
public sealed class ShellTheme
{
    private readonly UISettings ui = new();
    private readonly AccessibilitySettings accessibility = new();

    public SolidColorBrush Accent { get; } = new(Colors.DodgerBlue);
    /// <summary>Accent for text and icons on the chrome (lighter in dark mode, for contrast).</summary>
    public SolidColorBrush AccentText { get; } = new(Colors.DodgerBlue);
    public SolidColorBrush OnAccent { get; } = new(Colors.White);
    public SolidColorBrush AccentSubtle { get; } = new(Colors.Transparent);
    public SolidColorBrush PrimaryText { get; } = new(Colors.Black);
    public SolidColorBrush SecondaryText { get; } = new(Colors.Gray);
    public SolidColorBrush DisabledText { get; } = new(Colors.Gray);
    /// <summary>Fill of controls on the chrome (address field, segmented controls).</summary>
    public SolidColorBrush ControlFill { get; } = new(Colors.Transparent);
    public SolidColorBrush ControlStroke { get; } = new(Colors.Transparent);
    public SolidColorBrush Hover { get; } = new(Colors.Transparent);
    public SolidColorBrush Pressed { get; } = new(Colors.Transparent);
    public SolidColorBrush Divider { get; } = new(Colors.Transparent);
    /// <summary>The selected tab of rounded and segmented strips.</summary>
    public SolidColorBrush SelectedTab { get; } = new(Colors.White);
    public SolidColorBrush SidebarSelection { get; } = new(Colors.White);
    /// <summary>Chrome's darker frame behind the tabs.</summary>
    public SolidColorBrush Frame { get; } = new(Colors.LightGray);
    /// <summary>Chrome's toolbar and the selected tab that merges into it.</summary>
    public SolidColorBrush Surface { get; } = new(Colors.White);
    /// <summary>Opaque panel for things drawn over the page (suggestions, peeking sidebar, notices).</summary>
    public SolidColorBrush OverlayPanel { get; } = new(Colors.White);
    /// <summary>Behind Mosaic's own pages and Settings (opaque).</summary>
    public SolidColorBrush PageBackground { get; } = new(Colors.White);
    public SolidColorBrush SettingsSidebar { get; } = new(Colors.White);
    public SolidColorBrush Card { get; } = new(Colors.White);
    public SolidColorBrush CardStroke { get; } = new(Colors.Transparent);
    /// <summary>The window's own colour when there is no Mica (solid material, transparency off, Windows 10).</summary>
    public SolidColorBrush WindowSolid { get; } = new(Colors.White);
    public SolidColorBrush Transparent { get; } = new(Colors.Transparent);

    public bool IsDark { get; private set; }
    public bool HighContrast { get; private set; }
    public bool ReduceMotion { get; private set; }
    public bool ReduceTransparency { get; private set; }
    /// <summary>Mica on the window and Acrylic on transient surfaces are in use.</summary>
    public bool Translucent { get; private set; }
    public bool ProminentFocusRings { get; private set; }
    public bool AlwaysShowTabCloseButtons { get; private set; }
    public Color AccentColor { get; private set; }
    public ShellMetrics Metrics { get; private set; } = new(1, ShellDensity.Regular);
    public MosaicLayout Layout { get; private set; } = MosaicLayout.Factory(MosaicPresetID.Chrome);

    public double CornerRadius => Layout.Appearance.CornerRadius;

    /// <summary>Raised on the UI thread when a Windows setting the theme follows changes (contrast, accent, effects).</summary>
    public event Action? SystemSettingsChanged;

    public ShellTheme()
    {
        ui.ColorValuesChanged += (_, _) => SystemSettingsChanged?.Invoke();
        ui.AdvancedEffectsEnabledChanged += (_, _) => SystemSettingsChanged?.Invoke();
        accessibility.HighContrastChanged += (_, _) => SystemSettingsChanged?.Invoke();
    }

    /// <summary>Windows's "Transparency effects" setting.</summary>
    public bool SystemTransparencyEffects
    {
        get
        {
            try { return ui.AdvancedEffectsEnabled; } catch { return true; }
        }
    }

    /// <summary>Windows's "Animation effects" setting.</summary>
    public bool SystemAnimations
    {
        get
        {
            try { return ui.AnimationsEnabled; } catch { return true; }
        }
    }

    public bool SystemHighContrast
    {
        get
        {
            try { return accessibility.HighContrast; } catch { return false; }
        }
    }

    /// <summary>Whether Windows draws Mica here (Windows 11); Windows 10 gets solid surfaces.</summary>
    public static bool MicaSupported
    {
        get
        {
            try { return Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported(); } catch { return false; }
        }
    }

    /// <summary>Recomputes everything for <paramref name="layout"/>; <paramref name="dark"/> is the effective theme.</summary>
    public void Update(MosaicLayout layout, ShellSettings settings, bool dark)
    {
        Layout = layout;
        HighContrast = SystemHighContrast;
        ReduceMotion = settings.ReduceMotion.Resolve(!SystemAnimations);
        ReduceTransparency = settings.ReduceTransparency.Resolve(!SystemTransparencyEffects);
        Translucent = layout.Appearance.Material == ChromeMaterial.LiquidGlass && !ReduceTransparency && !HighContrast && MicaSupported;
        ProminentFocusRings = settings.ProminentFocusRings;
        AlwaysShowTabCloseButtons = settings.AlwaysShowTabCloseButtons;
        Metrics = new ShellMetrics(settings.TextSize.Scale(), layout.Appearance.Density);
        IsDark = dark;
        AccentColor = ResolveAccent(layout.Appearance.Accent);
        if (HighContrast) ApplyContrastColors();
        else ApplyColors(dark);
    }

    private Color ResolveAccent(string hex)
    {
        if (hex == HexColor.System)
        {
            try { return ui.GetColorValue(IsDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1); }
            catch { return C(0x00, 0x78, 0xD4); }
        }
        return ColorFromHex(hex) ?? C(0x00, 0x78, 0xD4);
    }

    private void ApplyColors(bool dark)
    {
        var accent = AccentColor;
        Accent.Color = accent;
        AccentText.Color = dark ? Mix(accent, Colors.White, Luma(accent) < 0.45 ? 0.35 : 0.1) : Mix(accent, Colors.Black, Luma(accent) > 0.6 ? 0.35 : 0);
        OnAccent.Color = Luma(accent) > 0.62 ? Colors.Black : Colors.White;
        AccentSubtle.Color = WithAlpha(accent, dark ? 0.24 : 0.14);
        PrimaryText.Color = dark ? Colors.White : C(0x1B, 0x1B, 0x1B);
        SecondaryText.Color = dark ? C(0xFF, 0xFF, 0xFF, 0xC5) : C(0x00, 0x00, 0x00, 0x9E);
        DisabledText.Color = dark ? C(0xFF, 0xFF, 0xFF, 0x5D) : C(0x00, 0x00, 0x00, 0x5C);
        var surface = Layout.Appearance.ToolbarSurface;
        ControlFill.Color = surface
            ? (dark ? C(0x28, 0x2A, 0x2D) : C(0xEE, 0xF1, 0xF4))
            : (dark ? C(0xFF, 0xFF, 0xFF, 0x12) : C(0xFF, 0xFF, 0xFF, 0xC8));
        ControlStroke.Color = dark ? C(0xFF, 0xFF, 0xFF, 0x0F) : C(0x00, 0x00, 0x00, surface ? 0x00 : 0x10);
        Hover.Color = dark ? C(0xFF, 0xFF, 0xFF, 0x14) : C(0x00, 0x00, 0x00, 0x0B);
        Pressed.Color = dark ? C(0xFF, 0xFF, 0xFF, 0x0B) : C(0x00, 0x00, 0x00, 0x07);
        Divider.Color = dark ? C(0xFF, 0xFF, 0xFF, 0x17) : C(0x00, 0x00, 0x00, 0x17);
        SelectedTab.Color = dark ? C(0xFF, 0xFF, 0xFF, 0x1C) : C(0xFF, 0xFF, 0xFF, 0xE6);
        SidebarSelection.Color = dark ? C(0xFF, 0xFF, 0xFF, 0x21) : C(0xFF, 0xFF, 0xFF, 0xD1);
        Frame.Color = dark ? C(0x20, 0x21, 0x24) : C(0xDE, 0xE1, 0xE6);
        Surface.Color = dark ? C(0x3C, 0x3D, 0x40) : Colors.White;
        OverlayPanel.Color = dark ? C(0x2B, 0x2B, 0x2C) : C(0xFA, 0xFA, 0xFB);
        PageBackground.Color = dark ? C(0x1F, 0x1F, 0x1F) : C(0xFA, 0xFA, 0xFA);
        SettingsSidebar.Color = dark ? C(0x1A, 0x1A, 0x1A) : C(0xF3, 0xF3, 0xF3);
        Card.Color = dark ? C(0x2B, 0x2B, 0x2B) : Colors.White;
        CardStroke.Color = dark ? C(0x00, 0x00, 0x00, 0x33) : C(0x00, 0x00, 0x00, 0x0F);
        WindowSolid.Color = dark ? C(0x20, 0x20, 0x20) : C(0xF3, 0xF3, 0xF3);
    }

    private void ApplyContrastColors()
    {
        Color Sys(UIElementType type, Color fallback)
        {
            try { return ui.UIElementColor(type); } catch { return fallback; }
        }
        var window = Sys(UIElementType.Window, Colors.Black);
        var text = Sys(UIElementType.WindowText, Colors.White);
        var highlight = Sys(UIElementType.Highlight, Colors.Cyan);
        var highlightText = Sys(UIElementType.HighlightText, Colors.Black);
        var gray = Sys(UIElementType.GrayText, Colors.Gray);
        var face = Sys(UIElementType.ButtonFace, window);
        AccentColor = highlight;
        Accent.Color = highlight;
        AccentText.Color = Sys(UIElementType.Hotlight, highlight);
        OnAccent.Color = highlightText;
        AccentSubtle.Color = window;
        PrimaryText.Color = text;
        SecondaryText.Color = text;
        DisabledText.Color = gray;
        foreach (var b in new[] { ControlFill, Frame, Surface, OverlayPanel, PageBackground, SettingsSidebar, Card, WindowSolid, SidebarSelection, SelectedTab })
        {
            b.Color = window;
        }
        SelectedTab.Color = face;
        ControlStroke.Color = text;
        CardStroke.Color = text;
        Divider.Color = text;
        Hover.Color = highlight;
        Pressed.Color = highlight;
    }

    /// <summary>Fluent resource keys Mosaic's accent overrides (accent buttons, toggles, sliders, selection).</summary>
    public void ApplyResourceOverrides(ResourceDictionary resources)
    {
        string[] accentKeys =
        [
            "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
            "SystemControlHighlightAccentBrush", "SystemControlForegroundAccentBrush", "SystemControlHighlightAltAccentBrush",
            "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
            "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
            "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
            "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
            "RadioButtonOuterEllipseCheckedFill", "RadioButtonOuterEllipseCheckedFillPointerOver", "RadioButtonOuterEllipseCheckedFillPressed",
            "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver",
            "TextControlBorderBrushFocused", "ListViewItemSelectionIndicatorBrush", "NavigationViewSelectionIndicatorForeground",
        ];
        string[] onAccentKeys = ["AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "ToggleSwitchKnobFillOn"];
        foreach (var key in accentKeys)
        {
            if (HighContrast) resources.Remove(key);
            else resources[key] = Accent;
        }
        foreach (var key in onAccentKeys)
        {
            if (HighContrast) resources.Remove(key);
            else resources[key] = OnAccent;
        }
    }

    public static Color? ColorFromHex(string? hex)
    {
        if (HexColor.Components(hex) is not { } c) return null;
        return Color.FromArgb(B(c.Alpha), B(c.Red), B(c.Green), B(c.Blue));
    }

    private static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    public static Color C(int r, int g, int b, int a = 0xFF) => Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb(B(alpha), c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>The engine's colour (the dot on tabs and the engine switch).</summary>
    public static Color EngineTint(EngineKind engine) => engine == EngineKind.Gecko ? C(0xFF, 0x73, 0x1A) : C(0x33, 0x85, 0xF5);
}
