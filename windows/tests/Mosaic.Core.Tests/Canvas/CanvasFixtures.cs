using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Tests.Canvas;

/// <summary>
/// Files written by the macOS app's own MosaicCore (Fixtures/generator/main.swift, built
/// against Sources/MosaicCore): layout exports, a configuration library and a table of
/// expected results (colours, gradients, every edit on several layouts, imports).
/// </summary>
internal static class CanvasFixtures
{
    private static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    public static string Directory => Path.Combine(Here(), "Fixtures");

    public static string Path_(string name) => Path.Combine(Directory, name);

    public static string Text(string name) => File.ReadAllText(Path_(name));

    private static readonly Lazy<JsonObject> expectations = new(() =>
    {
        using var file = File.OpenRead(Path_("macos-expectations.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonNode.Parse(gzip)!.AsObject();
    });

    public static JsonObject Expectations => expectations.Value;

    /// <summary>JSON equality with numbers compared as numbers (240 == 240.0) and key order ignored.</summary>
    public static bool SameJson(JsonNode? a, JsonNode? b, out string where, string path = "$")
    {
        where = path;
        switch (a, b)
        {
            case (null, null):
                return true;
            case (JsonObject oa, JsonObject ob):
                if (oa.Count != ob.Count)
                {
                    where = $"{path}: keys {string.Join(",", oa.Select(p => p.Key).Order())} vs {string.Join(",", ob.Select(p => p.Key).Order())}";
                    return false;
                }
                foreach (var (key, value) in oa)
                {
                    if (!ob.ContainsKey(key)) { where = $"{path}.{key} missing"; return false; }
                    if (!SameJson(value, ob[key], out where, $"{path}.{key}")) return false;
                }
                return true;
            case (JsonArray xa, JsonArray xb):
                if (xa.Count != xb.Count) { where = $"{path}: {xa.Count} vs {xb.Count} items"; return false; }
                for (var i = 0; i < xa.Count; i++)
                {
                    if (!SameJson(xa[i], xb[i], out where, $"{path}[{i}]")) return false;
                }
                return true;
            case (JsonValue va, JsonValue vb):
                if (va.GetValueKind() == JsonValueKind.Number && vb.GetValueKind() == JsonValueKind.Number)
                {
                    var same = Math.Abs(Mosaic.Core.Canvas.Interim.LayoutJson.Number(va)!.Value - Mosaic.Core.Canvas.Interim.LayoutJson.Number(vb)!.Value) < 1e-12;
                    if (!same) where = $"{path}: {va} vs {vb}";
                    return same;
                }
                var equal = va.ToJsonString() == vb.ToJsonString();
                if (!equal) where = $"{path}: {va.ToJsonString()} vs {vb.ToJsonString()}";
                return equal;
            default:
                where = $"{path}: {a?.ToJsonString()} vs {b?.ToJsonString()}";
                return false;
        }
    }

    public static void AssertSameJson(JsonNode? expected, JsonNode? actual, string context = "")
    {
        Assert.True(SameJson(expected, actual, out var where), $"{context} differs at {where}");
    }
}
