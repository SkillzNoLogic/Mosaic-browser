using System.Globalization;

// Colours for the Gradient Studio's colour chart, ported from macOS
// (Sources/MosaicCore/Canvas/ColorValue.swift, itself from Eminence's ColorChartPicker): sRGB
// components with conversions to and from HEX, RGB, HSB, HSL, CMYK, CIELAB and OKLCH, plus
// OKLab, which the free-form gradient blends in. Owner: canvas.

namespace Mosaic.Core.Canvas;

/// <summary>A colour as sRGB components in 0…1.</summary>
public readonly record struct ColorValue
{
    public double R { get; }
    public double G { get; }
    public double B { get; }

    public static readonly ColorValue Fallback = new(0.35, 0.55, 0.95);

    public ColorValue(double r, double g, double b)
    {
        R = Unit(r);
        G = Unit(g);
        B = Unit(b);
    }

    private static double Unit(double v) => double.IsFinite(v) ? Math.Min(Math.Max(v, 0), 1) : 0;

    // MARK: Hex

    /// <summary>#RGB, #RRGGBB and #RRGGBBAA (alpha ignored), with or without #; null otherwise.</summary>
    public static ColorValue? FromHex(string? hex)
    {
        if (hex is null) return null;
        var text = hex.Trim(' ', '\t').ToUpperInvariant();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length == 3) text = string.Concat(text.Select(c => $"{c}{c}"));
        if (text.Length == 8) text = text[..6];
        if (text.Length != 6 || !text.All(Uri.IsHexDigit)) return null;
        var value = uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new ColorValue(((value >> 16) & 0xFF) / 255.0, ((value >> 8) & 0xFF) / 255.0, (value & 0xFF) / 255.0);
    }

    private static int Byte(double v) => (int)Math.Round(v * 255, MidpointRounding.AwayFromZero);

    public string Hex => $"#{Byte(R):X2}{Byte(G):X2}{Byte(B):X2}";

    /// <summary>Relative luminance (0 black … 1 white).</summary>
    public double Luminance => 0.2126 * R + 0.7152 * G + 0.0722 * B;

    // MARK: HSB and HSL (hue in degrees, the rest in percent)

    public (double H, double S, double B) Hsb
    {
        get
        {
            double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), delta = max - min;
            return (Hue(max, delta), max == 0 ? 0 : delta / max * 100, max * 100);
        }
    }

    public static ColorValue FromHsb(double h, double s, double brightness)
    {
        double sv = s / 100, v = brightness / 100, c = v * sv;
        var (r, g, b) = HueComponents(h, c);
        var m = v - c;
        return new ColorValue(r + m, g + m, b + m);
    }

    public (double H, double S, double L) Hsl
    {
        get
        {
            double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), delta = max - min;
            var l = (max + min) / 2;
            var s = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * l - 1));
            return (Hue(max, delta), s * 100, l * 100);
        }
    }

    public static ColorValue FromHsl(double h, double s, double lightness)
    {
        double sv = s / 100, l = lightness / 100;
        var c = (1 - Math.Abs(2 * l - 1)) * sv;
        var (r, g, b) = HueComponents(h, c);
        var m = l - c / 2;
        return new ColorValue(r + m, g + m, b + m);
    }

    private double Hue(double max, double delta)
    {
        if (!(delta > 0)) return 0;
        double h;
        if (max == R) h = ((G - B) / delta) % 6;
        else if (max == G) h = (B - R) / delta + 2;
        else h = (R - G) / delta + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static (double, double, double) HueComponents(double h, double c)
    {
        var hp = ((h % 360) + 360) % 360 / 60;
        var x = c * (1 - Math.Abs(hp % 2 - 1));
        return hp switch
        {
            < 1 => (c, x, 0),
            < 2 => (x, c, 0),
            < 3 => (0, c, x),
            < 4 => (0, x, c),
            < 5 => (x, 0, c),
            _ => (c, 0, x),
        };
    }

    // MARK: CMYK (naive, percent)

    public (double C, double M, double Y, double K) Cmyk
    {
        get
        {
            var k = 1 - Math.Max(R, Math.Max(G, B));
            if (!(k < 1)) return (0, 0, 0, 100);
            return ((1 - R - k) / (1 - k) * 100, (1 - G - k) / (1 - k) * 100, (1 - B - k) / (1 - k) * 100, k * 100);
        }
    }

    public static ColorValue FromCmyk(double c, double m, double y, double k)
    {
        var kv = k / 100;
        return new ColorValue((1 - c / 100) * (1 - kv), (1 - m / 100) * (1 - kv), (1 - y / 100) * (1 - kv));
    }

    // MARK: CIELAB (D65) and OKLCH, through linear sRGB

    private static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    private static double Gamma(double v) => v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4) - 0.055;

    private static ColorValue FromLinear(double r, double g, double b) => new(Gamma(r), Gamma(g), Gamma(b));

    public (double L, double A, double B) Lab
    {
        get
        {
            double lr = Linear(R), lg = Linear(G), lb = Linear(B);
            var x = (0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb) / 0.95047;
            var y = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb;
            var z = (0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb) / 1.08883;
            static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
            double fx = F(x), fy = F(y), fz = F(z);
            return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
        }
    }

    public static ColorValue FromLab(double l, double a, double b)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double Inv(double t) => t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) / (24389.0 / 27);
        double x = Inv(fx) * 0.95047, y = Inv(fy), z = Inv(fz) * 1.08883;
        return FromLinear(3.2404542 * x - 1.5371385 * y - 0.4985314 * z,
                          -0.9692660 * x + 1.8760108 * y + 0.0415560 * z,
                          0.0556434 * x - 0.2040259 * y + 1.0572252 * z);
    }

    /// <summary>OKLCH: lightness in percent, chroma (0…~0.4), hue in degrees.</summary>
    public (double L, double C, double H) Oklch
    {
        get
        {
            var v = Oklab;
            var h = Math.Atan2(v.B, v.A) * 180 / Math.PI;
            if (h < 0) h += 360;
            return (v.L * 100, Math.Sqrt(v.A * v.A + v.B * v.B), h);
        }
    }

    public static ColorValue FromOklch(double l, double c, double h) =>
        FromOklab(l / 100, c * Math.Cos(h * Math.PI / 180), c * Math.Sin(h * Math.PI / 180));

    /// <summary>OKLab with L in 0…1 (the space the free-form gradient blends in).</summary>
    public (double L, double A, double B) Oklab
    {
        get
        {
            double lr = Linear(R), lg = Linear(G), lb = Linear(B);
            var l = Math.Cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
            var m = Math.Cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
            var s = Math.Cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);
            return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                    1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                    0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }
    }

    public static ColorValue FromOklab(double L, double A, double B)
    {
        var l = Math.Pow(L + 0.3963377774 * A + 0.2158037573 * B, 3);
        var m = Math.Pow(L - 0.1055613458 * A - 0.0638541728 * B, 3);
        var s = Math.Pow(L - 0.0894841775 * A - 1.2914855480 * B, 3);
        return FromLinear(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                          -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                          -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }
}

/// <summary>The encodings the colour chart can show and edit (raw values as on macOS).</summary>
public enum ColorEncoding { Hex, Rgb, Hsb, Hsl, Cmyk, Lab, Oklch }

public static class ColorEncodings
{
    public static string Raw(this ColorEncoding e) => e.ToString().ToUpperInvariant();

    public static ColorEncoding? Parse(string? raw) =>
        Enum.GetValues<ColorEncoding>().Cast<ColorEncoding?>().FirstOrDefault(e => e!.Value.Raw() == raw);

    public static IReadOnlyList<(string Label, double Min, double Max, int Digits)> Components(this ColorEncoding e) => e switch
    {
        ColorEncoding.Rgb => [("R", 0, 255, 0), ("G", 0, 255, 0), ("B", 0, 255, 0)],
        ColorEncoding.Hsb => [("H", 0, 360, 0), ("S", 0, 100, 0), ("B", 0, 100, 0)],
        ColorEncoding.Hsl => [("H", 0, 360, 0), ("S", 0, 100, 0), ("L", 0, 100, 0)],
        ColorEncoding.Cmyk => [("C", 0, 100, 0), ("M", 0, 100, 0), ("Y", 0, 100, 0), ("K", 0, 100, 0)],
        ColorEncoding.Lab => [("L", 0, 100, 1), ("a", -128, 127, 1), ("b", -128, 127, 1)],
        ColorEncoding.Oklch => [("L", 0, 100, 1), ("C", 0, 0.4, 3), ("H", 0, 360, 1)],
        _ => [],
    };

    public static double[] Values(this ColorEncoding e, ColorValue c)
    {
        switch (e)
        {
            case ColorEncoding.Rgb: return [c.R * 255, c.G * 255, c.B * 255];
            case ColorEncoding.Hsb: { var v = c.Hsb; return [v.H, v.S, v.B]; }
            case ColorEncoding.Hsl: { var v = c.Hsl; return [v.H, v.S, v.L]; }
            case ColorEncoding.Cmyk: { var v = c.Cmyk; return [v.C, v.M, v.Y, v.K]; }
            case ColorEncoding.Lab: { var v = c.Lab; return [v.L, v.A, v.B]; }
            case ColorEncoding.Oklch: { var v = c.Oklch; return [v.L, v.C, v.H]; }
            default: return [];
        }
    }

    /// <summary>The colour for component values (missing values count as 0).</summary>
    public static ColorValue Color(this ColorEncoding e, IReadOnlyList<double> values)
    {
        double V(int i) => i < values.Count ? values[i] : 0;
        return e switch
        {
            ColorEncoding.Rgb => new ColorValue(V(0) / 255, V(1) / 255, V(2) / 255),
            ColorEncoding.Hsb => ColorValue.FromHsb(V(0), V(1), V(2)),
            ColorEncoding.Hsl => ColorValue.FromHsl(V(0), V(1), V(2)),
            ColorEncoding.Cmyk => ColorValue.FromCmyk(V(0), V(1), V(2), V(3)),
            ColorEncoding.Lab => ColorValue.FromLab(V(0), V(1), V(2)),
            ColorEncoding.Oklch => ColorValue.FromOklch(V(0), V(1), V(2)),
            _ => ColorValue.Fallback,
        };
    }

    /// <summary>Typed text for each component, clamped; unreadable entries keep <paramref name="current"/>'s value.</summary>
    public static ColorValue Color(this ColorEncoding e, IReadOnlyList<string> fields, ColorValue current)
    {
        var comps = e.Components();
        var existing = e.Values(current);
        var numbers = new double[comps.Count];
        for (var i = 0; i < comps.Count; i++)
        {
            numbers[i] = existing[i];
            if (i >= fields.Count) continue;
            var text = new string(fields[i].Replace(',', '.').Where(ch => "0123456789.-".Contains(ch)).ToArray());
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n))
                numbers[i] = Math.Min(Math.Max(n, comps[i].Min), comps[i].Max);
        }
        return e.Color(numbers);
    }

    /// <summary>printf-style fixed formatting (round half to even on the binary value, as %.nf does).</summary>
    internal static string F(double x, int digits)
    {
        var rounded = Math.Round(x, digits, MidpointRounding.ToEven);
        return rounded.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    /// <summary>The colour written the way designers and CSS write it.</summary>
    public static string String(this ColorEncoding e, ColorValue c)
    {
        var v = e.Values(c);
        return e switch
        {
            ColorEncoding.Rgb => $"rgb({F(v[0], 0)}, {F(v[1], 0)}, {F(v[2], 0)})",
            ColorEncoding.Hsb => $"hsb({F(v[0], 0)}°, {F(v[1], 0)}%, {F(v[2], 0)}%)",
            ColorEncoding.Hsl => $"hsl({F(v[0], 0)} {F(v[1], 0)}% {F(v[2], 0)}%)",
            ColorEncoding.Cmyk => $"cmyk({F(v[0], 0)}%, {F(v[1], 0)}%, {F(v[2], 0)}%, {F(v[3], 0)}%)",
            ColorEncoding.Lab => $"lab({F(v[0], 1)}% {F(v[1], 1)} {F(v[2], 1)})",
            ColorEncoding.Oklch => $"oklch({F(v[0], 1)}% {F(v[1], 3)} {F(v[2], 1)})",
            _ => c.Hex,
        };
    }
}
