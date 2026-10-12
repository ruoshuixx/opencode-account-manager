using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using Xunit;

namespace AccountManager.Core.Tests;

public class JsonTests
{
    [Fact]
    public void JsoncStripsCommentsAndTrailingCommas()
    {
        var node = Jsonc.Parse("""
        {
          // line comment
          "a": 1,
          /* block */ "b": [1, 2, 3,],
        }
        """);
        Assert.Equal(1, (int)node!["a"]!);
        Assert.Equal(3, ((JsonArray)node["b"]!).Count);
    }

    [Fact]
    public void JsoncKeepsSlashesInsideStrings()
    {
        var node = Jsonc.Parse("""{"url":"https://example.com//x"}""");
        Assert.Equal("https://example.com//x", (string)node!["url"]!);
    }

    [Fact]
    public void JsonNumRejectsBoolsAndBlankStrings()
    {
        Assert.Null(JsonNum.ToNumber(JsonValue.Create(true)));
        Assert.Null(JsonNum.ToNumber(JsonValue.Create("")));
        Assert.Null(JsonNum.ToNumber(JsonValue.Create("abc")));
        Assert.Equal(1.5, JsonNum.ToNumber(JsonValue.Create("1.5")));
        Assert.Equal(7, JsonNum.ToLong(JsonValue.Create(7)));
    }

    [Fact]
    public void JsonMergeObjectsDeepAndOverlayWins()
    {
        var baseNode = JsonNode.Parse("""{"settings":{"a":1,"b":2},"x":1}""")!;
        var overlay = JsonNode.Parse("""{"settings":{"b":9,"c":3}}""")!;
        var merged = (JsonObject)JsonMerge.Merge(baseNode, overlay)!;

        Assert.Equal(1, (int)merged["settings"]!["a"]!);
        Assert.Equal(9, (int)merged["settings"]!["b"]!);
        Assert.Equal(3, (int)merged["settings"]!["c"]!);
        Assert.Equal(1, (int)merged["x"]!);
        // inputs untouched
        Assert.Equal(2, (int)baseNode["settings"]!["b"]!);
    }
}
