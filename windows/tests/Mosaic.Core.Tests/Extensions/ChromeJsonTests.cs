using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Extensions;

namespace Mosaic.Core.Tests.Extensions;

/// <summary>The JSON dialect Chrome accepts in manifest.json and messages.json.</summary>
public class ChromeJsonTests
{
    private static JsonNode? Parse(string json) => ChromeJson.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void AcceptsCommentsTrailingCommasAndAByteOrderMark()
    {
        var node = Parse("""
            // A line comment before anything else.
            {
              "name": "X", /* a block comment */
              "list": [1, 2, 3,],   // trailing comma in an array
              "nested": { "a": true, },
            }
            // …and one after the end.
            """);
        Assert.Equal("""{"name":"X","list":[1,2,3],"nested":{"a":true}}""", node!.ToJsonString());

        byte[] withMark = [0xEF, 0xBB, 0xBF, .. "{\"a\":1}"u8];
        Assert.Equal("""{"a":1}""", ChromeJson.Parse(withMark)!.ToJsonString());
    }

    [Fact]
    public void KeepsKeyOrderAndTheLastOfARepeatedKey()
    {
        var node = Parse("""{ "z": 1, "a": 2, "m": 3, "a": "again", "nested": { "k": 1, "k": 2 } }""")!.AsObject();
        Assert.Equal(["z", "a", "m", "nested"], node.Select(property => property.Key));
        Assert.Equal("again", (string?)node["a"]);
        Assert.Equal(2, (int?)node["nested"]!["k"]);
    }

    [Fact]
    public void KeepsValuesAsWritten()
    {
        const string Json = """{"float":1.0,"exp":1e3,"big":12345678901234567890123,"neg":-0.5,"text":"é \" \\ \n 日本 😀","none":null,"no":false,"deep":[[[{"x":[]}]]]}""";
        var node = Parse(Json)!;
        Assert.Equal(JsonNode.Parse(Json)!.ToJsonString(), node.ToJsonString());
        Assert.Contains("\"float\":1.0,\"exp\":1e3,\"big\":12345678901234567890123", node.ToJsonString());
        Assert.Null(node["none"]);
        Assert.True(node.AsObject().ContainsKey("none"));
    }

    [Theory]
    [InlineData("[1, 2]", "[1,2]")]
    [InlineData("\"text\"", "\"text\"")]
    [InlineData("42", "42")]
    [InlineData("true", "true")]
    public void TheRootMayBeAnyValue(string json, string expected) => Assert.Equal(expected, Parse(json)!.ToJsonString());

    [Fact]
    public void TheRootMayBeNull() => Assert.Null(Parse(" null "));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("// only a comment")]
    [InlineData("{ this is not json")]
    [InlineData("{ \"a\": 1 } trailing")]
    [InlineData("{ \"a\": 1 } { \"b\": 2 }")]
    [InlineData("{ 'single': 'quotes' }")]
    [InlineData("{ unquoted: 1 }")]
    [InlineData("{ \"a\": 1,, }")]
    [InlineData("[1, 2")]
    [InlineData("{ \"a\": 0x10 }")]
    public void RejectsWhatChromeRejects(string json) => Assert.ThrowsAny<JsonException>(() => Parse(json));

    [Fact]
    public void RejectsTextThatIsNotUtf8()
    {
        Assert.ThrowsAny<JsonException>(() => ChromeJson.Parse([.. "{\"a\":\""u8, 0xFF, 0xFE, .. "\"}"u8]));
        Assert.ThrowsAny<JsonException>(() => ChromeJson.Parse([.. "{\""u8, 0xC3, 0x28, .. "\":1}"u8]));
    }

    [Fact]
    public void RejectsAbsurdNesting() =>
        Assert.ThrowsAny<JsonException>(() => Parse(new string('[', 5000) + new string(']', 5000)));

    [Fact]
    public void WritesIndentedTextWithoutEscapingReadableCharacters()
    {
        var text = ChromeJson.Serialize(new JsonObject { ["name"] = "Zürich 日本 <b> & 'q' + /", ["emoji"] = "😀", ["list"] = new JsonArray(1, 2) });
        Assert.Contains("\"name\": \"Zürich 日本 <b> & 'q' + /\"", text);
        Assert.Contains("\n", text);
        Assert.Equal("😀", (string?)Parse(text)!["emoji"]);
    }
}
