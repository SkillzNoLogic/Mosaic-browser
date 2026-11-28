using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.App.Features.System;

/// <summary>
/// Fluent icons for every area: <c>Icons.Make("back")</c>. Owned by the system area.
/// </summary>
/// <remarks>
/// <para>Names: every SF Symbol name the macOS code uses (so ports read the same as the Swift:
/// <c>Icons.Make("puzzlepiece.extension")</c>) plus short semantic names (<c>back</c>,
/// <c>reload</c>, <c>newtab</c>, <c>private</c>, <c>downloads</c>, …). The table and the
/// reasoning for each glyph live in <see cref="IconGlyphs"/> (Mosaic.Core, unit-tested).</para>
/// <para>Fonts: Segoe Fluent Icons when it is installed (Windows 11, or installed by hand on
/// Windows 10), otherwise Segoe MDL2 Assets with MDL2 substitutes for the Fluent-only glyphs.
/// <c>MOSAIC_ICON_FONT=mdl2</c> forces the Windows 10 path (CI uses it to check fallbacks).
/// Detection is a file/registry check on first use, well under a millisecond.</para>
/// <para>Need a name that is missing? Ask the system area (or call <see cref="Add(string, int, int)"/>
/// from your own mounts); do not hard-code glyphs.</para>
/// </remarks>
public static class Icons
{
    /// <summary>True when Segoe Fluent Icons is used; false on the Segoe MDL2 Assets fallback.</summary>
    public static readonly bool UsesFluent = IconGlyphs.ChooseFluent(
        FluentFontInstalled(), Environment.GetEnvironmentVariable("MOSAIC_ICON_FONT"));

    /// <summary>The icon font's family name in use.</summary>
    public static string FontName => UsesFluent ? IconGlyphs.FluentFontName : IconGlyphs.Mdl2FontName;

    /// <summary>The icon font in use (Segoe Fluent Icons, or Segoe MDL2 Assets on Windows 10).</summary>
    public static readonly FontFamily Font = new(UsesFluent
        ? $"{IconGlyphs.FluentFontName}, {IconGlyphs.Mdl2FontName}"
        : IconGlyphs.Mdl2FontName);

    /// <summary>The glyph for <paramref name="name"/>, or a neutral placeholder ("unknown").</summary>
    public static string Glyph(string name) => IconGlyphs.Glyph(name, UsesFluent);

    /// <summary>Whether <paramref name="name"/> is mapped.</summary>
    public static bool Has(string name) => IconGlyphs.Contains(name);

    /// <summary>Registers or replaces a glyph used by both fonts (system area; others ask for additions).</summary>
    public static void Add(string name, string glyph)
    {
        var codePoint = char.ConvertToUtf32(glyph, 0);
        IconGlyphs.Set(name, new IconGlyphs.Entry(codePoint, codePoint));
    }

    /// <summary>Registers or replaces a glyph with a different Segoe MDL2 Assets substitute.</summary>
    public static void Add(string name, int fluentCodePoint, int mdl2CodePoint) =>
        IconGlyphs.Set(name, new IconGlyphs.Entry(fluentCodePoint, mdl2CodePoint));

    /// <summary>An icon element (buttons, <c>MenuFlyoutItem.Icon</c>, <c>AppBarButton.Icon</c>).</summary>
    public static FontIcon Make(string name, double size = 16) =>
        new() { Glyph = Glyph(name), FontFamily = Font, FontSize = size };

    /// <summary>An icon source (<c>TabViewItem.IconSource</c>, <c>NavigationViewItem</c>, <c>InfoBar</c>, …).</summary>
    public static FontIconSource MakeSource(string name, double size = 16) =>
        new() { Glyph = Glyph(name), FontFamily = Font, FontSize = size };

    private static bool FluentFontInstalled()
    {
        try
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (File.Exists(Path.Combine(fonts, IconGlyphs.FluentFontFile))) return true;
            var perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts", IconGlyphs.FluentFontFile);
            if (File.Exists(perUser)) return true;
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key?.GetValueNames().Any(n => n.StartsWith(IconGlyphs.FluentFontName, StringComparison.OrdinalIgnoreCase)) == true)
                    return true;
            }
        }
        catch (Exception)
        {
            // Unreadable registry or folders: fall back to MDL2, which every Windows 10/11 has.
        }
        return false;
    }
}
