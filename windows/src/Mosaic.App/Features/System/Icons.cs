using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mosaic.App.Features.System;

/// <summary>
/// Fluent icons for every area: <c>Icons.Make("back")</c>. Names are Mosaic's own
/// (mostly the SF Symbol names the macOS code uses, so ports read the same). Owned by the
/// system area, which fills in the table and the Windows 10 fallback; other areas only call it.
/// </summary>
public static class Icons
{
    /// <summary>Segoe Fluent Icons (Windows 11), falling back to Segoe MDL2 Assets (Windows 10).</summary>
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly Dictionary<string, string> glyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["back"] = "", ["chevron.left"] = "",
        ["forward"] = "", ["chevron.right"] = "",
        ["reload"] = "", ["arrow.clockwise"] = "",
        ["stop"] = "", ["xmark"] = "",
        ["add"] = "", ["plus"] = "",
        ["settings"] = "", ["gearshape"] = "",
        ["search"] = "", ["magnifyingglass"] = "",
        ["home"] = "", ["house"] = "",
        ["download"] = "", ["arrow.down.circle"] = "",
        ["star"] = "", ["star.fill"] = "",
        ["shield"] = "", ["lock"] = "", ["lock.fill"] = "",
        ["sidebar.left"] = "", ["puzzlepiece"] = "",
        ["more"] = "", ["ellipsis"] = "",
    };

    /// <summary>The glyph for <paramref name="name"/>, or a neutral placeholder.</summary>
    public static string Glyph(string name) => glyphs.TryGetValue(name, out var glyph) ? glyph : "";

    /// <summary>Registers or replaces glyphs (system area; others ask for additions).</summary>
    public static void Add(string name, string glyph) => glyphs[name] = glyph;

    public static FontIcon Make(string name, double size = 16) =>
        new() { Glyph = Glyph(name), FontFamily = Font, FontSize = size };
}
