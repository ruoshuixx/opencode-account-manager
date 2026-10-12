using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>
/// Regression guard for the shared options' TypeInfoResolver. <c>JsonArray.Add(string)</c> wraps
/// its value in a <c>JsonValueCustomized&lt;string&gt;</c> node; writing such a node calls the
/// parameterless <c>JsonSerializerOptions.MakeReadOnly()</c>, which throws unless the options
/// specifies a TypeInfoResolver ("JsonSerializerOptions instance must specify a TypeInfoResolver
/// setting before being marked as read-only"). Exporting an API-key account hit exactly this.
/// </summary>
public class CoreJsonTests
{
    private static JsonObject CatalogDocument()
    {
        string model = "gpt-5";
        var catalog = new JsonArray();
        catalog.Add(model); // deliberately the customizing overload, as the export used to do

        Assert.Equal("JsonValueCustomized`1", catalog[0]!.GetType().Name);

        return new JsonObject { ["api_model_catalog"] = catalog };
    }

    [Fact]
    public void IndentedWritesCustomizedNodes()
    {
        var json = CatalogDocument().ToJsonString(CoreJson.Indented);

        Assert.Contains("gpt-5", json);
    }

    [Fact]
    public void CompactWritesCustomizedNodes()
    {
        var json = CatalogDocument().ToJsonString(CoreJson.Compact);

        Assert.Contains("gpt-5", json);
    }
}
