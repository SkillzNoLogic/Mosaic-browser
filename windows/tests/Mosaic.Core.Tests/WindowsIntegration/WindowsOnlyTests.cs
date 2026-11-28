using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Mosaic.Core.WindowsIntegration;
using Xunit.Abstractions;

namespace Mosaic.Core.Tests.WindowsIntegration;

/// <summary>A test that needs a real Windows (the CI runner); skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Needs Windows.";
    }
}

/// <summary>The real HKEY_CURRENT_USER (the app's own CurrentUserRegistry, repeated here because the app is not referenced).</summary>
internal sealed class TestCurrentUserRegistry : IRegistryStore
{
    public string? GetString(string key, string? name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var opened = Registry.CurrentUser.OpenSubKey(key);
        return opened?.GetValue(name ?? "")?.ToString();
    }

    public void SetString(string key, string? name, string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var opened = Registry.CurrentUser.CreateSubKey(key, writable: true);
        opened.SetValue(name ?? "", value, RegistryValueKind.String);
    }

    public void DeleteValue(string key, string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var opened = Registry.CurrentUser.OpenSubKey(key, writable: true);
        opened?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteTree(string key)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
    }

    public bool KeyExists(string key)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var opened = Registry.CurrentUser.OpenSubKey(key);
        return opened is not null;
    }
}

public class WindowsOnlyTests(ITestOutputHelper output)
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(uint flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);

    private const int ASSOCSTR_COMMAND = 1;
    private const int ASSOCSTR_PROGID = 20;
    private const uint ASSOCF_IS_PROTOCOL = 0x1000;

    private static string? Assoc(uint flags, int what, string assoc, string? extra)
    {
        uint length = 0;
        AssocQueryStringW(flags, what, assoc, extra, null, ref length);
        if (length == 0) return null;
        var buffer = new StringBuilder((int)length);
        return AssocQueryStringW(flags, what, assoc, extra, buffer, ref length) >= 0 ? buffer.ToString() : null;
    }

    /// <summary>
    /// Registers under a throwaway name in the runner's real HKCU, checks that the shell resolves
    /// Mosaic's ProgIDs to the open command, then removes everything again.
    /// </summary>
    [WindowsFact]
    public void RegistersAndUnregistersInTheRealRegistry()
    {
        var name = "MosaicTest" + Guid.NewGuid().ToString("N")[..8];
        var exe = @"C:\Program Files\Mosaic Test\Mosaic.exe";
        var registration = new DefaultBrowserRegistration(exe, name);
        var registry = new TestCurrentUserRegistry();
        try
        {
            registration.Register(registry);
            var status = registration.Status(registry);
            Assert.True(status.Registered);
            Assert.True(status.RegisteredForThisCopy);
            output.WriteLine($"http is handled by {status.Choices["http"] ?? "(no user choice)"}, https by {status.Choices["https"] ?? "(no user choice)"}");

            // The shell sees the ProgIDs and their open commands.
            Assert.Equal(registration.OpenCommand, Assoc(0, ASSOCSTR_COMMAND, registration.UrlProgId, "open"));
            Assert.Equal(registration.OpenCommand, Assoc(0, ASSOCSTR_COMMAND, registration.HtmlProgId, "open"));
            output.WriteLine($"Shell default for http: {Assoc(ASSOCF_IS_PROTOCOL, ASSOCSTR_PROGID, "http", null) ?? "(none)"}");
            output.WriteLine($"Shell default for .html: {Assoc(0, ASSOCSTR_PROGID, ".html", null) ?? "(none)"}");

            // Registered applications list it, pointing at the capabilities.
            Assert.Equal(registration.CapabilitiesKey, registry.GetString(DefaultBrowserRegistration.RegisteredApplicationsKey, name));
        }
        finally
        {
            registration.Unregister(registry);
        }
        Assert.False(registration.Status(registry).Registered);
        Assert.False(registry.KeyExists(registration.ClientKey));
        Assert.False(registry.KeyExists(@"Software\Classes\" + registration.UrlProgId));
        Assert.Null(registry.GetString(@"Software\Classes\.html\OpenWithProgids", registration.HtmlProgId));
    }

    /// <summary>Every mapped code point exists in the icon fonts installed on this Windows.</summary>
    [WindowsFact]
    public void EveryGlyphExistsInTheInstalledFonts()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var mdl2 = Path.Combine(fonts, IconGlyphs.Mdl2FontFile);
        var fluent = Path.Combine(fonts, IconGlyphs.FluentFontFile);
        output.WriteLine($"{IconGlyphs.Mdl2FontFile}: {File.Exists(mdl2)}, {IconGlyphs.FluentFontFile}: {File.Exists(fluent)}");
        Assert.True(File.Exists(mdl2) || File.Exists(fluent), "Neither icon font is installed.");

        if (File.Exists(mdl2))
        {
            var covered = TrueTypeCmap.CodePoints(mdl2);
            var missing = IconGlyphs.Names.Where(n => IconGlyphs.TryGet(n, out var e) && !covered.Contains(e.Mdl2)).ToList();
            Assert.True(missing.Count == 0, "Missing from Segoe MDL2 Assets: " + string.Join(", ", missing));
            Assert.Contains(IconGlyphs.Placeholder, covered);
        }
        if (File.Exists(fluent))
        {
            var covered = TrueTypeCmap.CodePoints(fluent);
            var missing = IconGlyphs.Names.Where(n => IconGlyphs.TryGet(n, out var e) && !covered.Contains(e.Fluent)).ToList();
            Assert.True(missing.Count == 0, "Missing from Segoe Fluent Icons: " + string.Join(", ", missing));
        }
    }
}

/// <summary>Reads the Unicode code points a TrueType/OpenType font maps (cmap formats 4 and 12).</summary>
internal static class TrueTypeCmap
{
    public static HashSet<int> CodePoints(string path)
    {
        var data = File.ReadAllBytes(path);
        int U16(int at) => (data[at] << 8) | data[at + 1];
        long U32(int at) => ((long)data[at] << 24) | ((long)data[at + 1] << 16) | ((long)data[at + 2] << 8) | data[at + 3];

        var tables = U16(4);
        var cmap = -1;
        for (var i = 0; i < tables; i++)
        {
            var record = 12 + i * 16;
            if (Encoding.ASCII.GetString(data, record, 4) == "cmap") cmap = (int)U32(record + 8);
        }
        if (cmap < 0) throw new InvalidDataException("No cmap table.");

        var result = new HashSet<int>();
        var subtables = U16(cmap + 2);
        for (var i = 0; i < subtables; i++)
        {
            var platform = U16(cmap + 4 + i * 8);
            var encoding = U16(cmap + 4 + i * 8 + 2);
            var offset = cmap + (int)U32(cmap + 4 + i * 8 + 4);
            if (platform != 3 || encoding is not (1 or 10 or 0)) continue;
            var format = U16(offset);
            if (format == 4)
            {
                var segments = U16(offset + 6) / 2;
                var ends = offset + 14;
                var starts = ends + segments * 2 + 2;
                var deltas = starts + segments * 2;
                var ranges = deltas + segments * 2;
                for (var s = 0; s < segments; s++)
                {
                    int end = U16(ends + s * 2), start = U16(starts + s * 2), delta = U16(deltas + s * 2), rangeOffset = U16(ranges + s * 2);
                    for (var c = start; c <= end && c != 0xFFFF; c++)
                    {
                        int glyph;
                        if (rangeOffset == 0) glyph = (c + delta) & 0xFFFF;
                        else
                        {
                            var at = ranges + s * 2 + rangeOffset + (c - start) * 2;
                            glyph = U16(at);
                            if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                        }
                        if (glyph != 0) result.Add(c);
                    }
                }
            }
            else if (format == 12)
            {
                var groups = U32(offset + 12);
                for (var g = 0; g < groups; g++)
                {
                    var at = offset + 16 + (int)g * 12;
                    for (var c = U32(at); c <= U32(at + 4); c++) result.Add((int)c);
                }
            }
        }
        return result;
    }
}
