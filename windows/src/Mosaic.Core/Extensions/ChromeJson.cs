using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Extensions;

/// <summary>
/// Reads JSON the way Chrome reads an extension's <c>manifest.json</c> and
/// <c>_locales/*/messages.json</c>, and writes it back out for the engines.
/// </summary>
/// <remarks>
/// Chrome is more forgiving than the JSON standard with these files, and published extensions
/// rely on it: a UTF-8 byte-order mark, <c>//</c> and <c>/* */</c> comments and trailing commas
/// are all accepted, and when a key is repeated the last value wins. (The macOS version reads
/// them as JSON5, which is laxer still; nothing Chrome itself would load needs more than this.)
/// </remarks>
internal static class ChromeJson
{
    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Indented, and without the default escaping of non-ASCII and HTML-sensitive characters, so
    /// names in any script stay readable in the files written to disk.
    /// </summary>
    private static readonly JsonSerializerOptions WriterOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Parses a JSON document, keeping the key order of its objects.</summary>
    /// <returns>The root value; null for the JSON literal <c>null</c>.</returns>
    /// <exception cref="JsonException">The text is not JSON even by Chrome's standards.</exception>
    public static JsonNode? Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.StartsWith(ByteOrderMark)) utf8 = utf8[ByteOrderMark.Length..];

        var reader = new Utf8JsonReader(utf8, ReaderOptions);
        try
        {
            reader.Read();
            var root = ReadValue(ref reader);
            reader.Read(); // Throws if anything but comments and whitespace follows the root value.
            return root;
        }
        catch (InvalidOperationException error)
        {
            // A string that is not valid UTF-8.
            throw new JsonException(error.Message, error);
        }
    }

    /// <summary>Serialises a value for a file on disk: indented UTF-8 text.</summary>
    public static string Serialize(JsonNode node) => node.ToJsonString(WriterOptions);

    /// <summary>Converts the value the reader is positioned on, and everything inside it.</summary>
    private static JsonNode? ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var properties = new JsonObject();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString()!;
                    reader.Read();
                    properties[name] = ReadValue(ref reader); // Assignment, so a repeated key takes the later value.
                }
                return properties;
            case JsonTokenType.StartArray:
                var elements = new JsonArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    elements.Add(ReadValue(ref reader));
                }
                return elements;
            case JsonTokenType.String:
                return JsonValue.Create(reader.GetString());
            case JsonTokenType.True:
            case JsonTokenType.False:
                return JsonValue.Create(reader.GetBoolean());
            case JsonTokenType.Null:
                return null;
            default:
                // A number, kept as written so that it round-trips digit for digit.
                return JsonValue.Create(JsonElement.ParseValue(ref reader));
        }
    }
}
