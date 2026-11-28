using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Shell;

/// <summary>
/// Writes JSON exactly as macOS's <c>JSONEncoder</c> does with
/// <c>[.prettyPrinted, .sortedKeys]</c> (or <c>[.sortedKeys]</c>), so files written on Windows
/// are byte-for-byte what the macOS app writes for the same value: keys sorted by ordinal
/// UTF-8 order, two-space indentation, <c>"key" : value</c>, empty containers as
/// <c>[\n\n]</c>, <c>/</c> escaped as <c>\/</c>, non-ASCII written raw, numbers in their
/// shortest round-trip form (<c>248</c>, <c>0.55</c>, <c>1e-05</c>, <c>1e+16</c>), no trailing newline.
/// </summary>
/// <remarks>Owner: shell. Checked against golden files produced by the macOS sources (tests).</remarks>
public static class SwiftJson
{
    public static string Write(JsonNode? node, bool pretty = true)
    {
        var builder = new StringBuilder(4096);
        WriteNode(builder, node, pretty, 0);
        return builder.ToString();
    }

    public static byte[] WriteUtf8(JsonNode? node, bool pretty = true) =>
        new UTF8Encoding(false).GetBytes(Write(node, pretty));

    private static void WriteNode(StringBuilder b, JsonNode? node, bool pretty, int depth)
    {
        switch (node)
        {
            case null:
                b.Append("null");
                break;
            case JsonObject obj:
                b.Append('{');
                if (pretty) b.Append('\n');
                var keys = obj.Select(p => p.Key).ToList();
                keys.Sort(CompareUtf8);
                for (var i = 0; i < keys.Count; i++)
                {
                    if (i > 0) b.Append(pretty ? ",\n" : ",");
                    if (pretty) Indent(b, depth + 1);
                    WriteString(b, keys[i]);
                    b.Append(pretty ? " : " : ":");
                    WriteNode(b, obj[keys[i]], pretty, depth + 1);
                }
                if (pretty)
                {
                    b.Append('\n');
                    Indent(b, depth);
                }
                b.Append('}');
                break;
            case JsonArray array:
                b.Append('[');
                if (pretty) b.Append('\n');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) b.Append(pretty ? ",\n" : ",");
                    if (pretty) Indent(b, depth + 1);
                    WriteNode(b, array[i], pretty, depth + 1);
                }
                if (pretty)
                {
                    b.Append('\n');
                    Indent(b, depth);
                }
                b.Append(']');
                break;
            case JsonValue value:
                WriteValue(b, value);
                break;
        }
    }

    private static void WriteValue(StringBuilder b, JsonValue value)
    {
        if (value.TryGetValue(out string? text)) { WriteString(b, text); return; }
        if (value.TryGetValue(out bool flag)) { b.Append(flag ? "true" : "false"); return; }
        if (value.TryGetValue(out int integer)) { b.Append(integer.ToString(CultureInfo.InvariantCulture)); return; }
        if (value.TryGetValue(out long longInteger)) { b.Append(longInteger.ToString(CultureInfo.InvariantCulture)); return; }
        if (value.TryGetValue(out double number)) { b.Append(FormatNumber(number)); return; }
        // A value parsed from text (JsonElement): re-read it as the kind it is.
        switch (value.GetValueKind())
        {
            case JsonValueKind.String: WriteString(b, value.GetValue<string>()); break;
            case JsonValueKind.True: b.Append("true"); break;
            case JsonValueKind.False: b.Append("false"); break;
            case JsonValueKind.Number:
                var element = value.GetValue<JsonElement>();
                if (element.TryGetInt64(out var whole)) b.Append(whole.ToString(CultureInfo.InvariantCulture));
                else b.Append(FormatNumber(element.GetDouble()));
                break;
            default: b.Append("null"); break;
        }
    }

    /// <summary>A double as Swift's JSONEncoder writes it.</summary>
    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("JSON cannot hold NaN or infinity.", nameof(value));
        if (value == 0) return double.IsNegative(value) ? "-0" : "0";
        var round = value.ToString("R", CultureInfo.InvariantCulture);
        // Split the shortest round-trip form into sign, digits and a decimal exponent.
        var negative = round.StartsWith('-');
        if (negative) round = round[1..];
        var exponent = 0;
        var e = round.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(round[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            round = round[..e];
        }
        var dot = round.IndexOf('.');
        var intPart = dot >= 0 ? round[..dot] : round;
        var fracPart = dot >= 0 ? round[(dot + 1)..] : "";
        var digits = (intPart + fracPart).TrimStart('0');
        var leadingZeros = (intPart + fracPart).Length - digits.Length;
        // Position of the decimal point relative to the start of `digits`.
        var pointPos = intPart.Length - leadingZeros + exponent;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        var exp10 = pointPos - 1;  // scientific exponent

        var result = new StringBuilder();
        if (negative) result.Append('-');
        if (exp10 < -4 || exp10 >= 16)
        {
            result.Append(digits[0]);
            if (digits.Length > 1) result.Append('.').Append(digits, 1, digits.Length - 1);
            result.Append('e').Append(exp10 < 0 ? '-' : '+');
            result.Append(Math.Abs(exp10).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (pointPos <= 0)
        {
            result.Append("0.").Append('0', -pointPos).Append(digits);
        }
        else if (pointPos >= digits.Length)
        {
            result.Append(digits).Append('0', pointPos - digits.Length);
        }
        else
        {
            result.Append(digits, 0, pointPos).Append('.').Append(digits, pointPos, digits.Length - pointPos);
        }
        return result.ToString();
    }

    private static void WriteString(StringBuilder b, string text)
    {
        b.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '/': b.Append("\\/"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                case '\b': b.Append("\\b"); break;
                case '\f': b.Append("\\f"); break;
                default:
                    if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else b.Append(c);
                    break;
            }
        }
        b.Append('"');
    }

    private static void Indent(StringBuilder b, int depth) => b.Append(' ', depth * 2);

    /// <summary>Ordinal comparison of the strings' UTF-8 bytes (Swift's sortedKeys order).</summary>
    public static int CompareUtf8(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var a = Encoding.UTF8.GetBytes(x);
        var c = Encoding.UTF8.GetBytes(y);
        return a.AsSpan().SequenceCompareTo(c);
    }
}
