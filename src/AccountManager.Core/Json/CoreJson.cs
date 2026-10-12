using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AccountManager.Core.Json;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> with an explicit reflection
/// <see cref="JsonSerializerOptions.TypeInfoResolver"/>.
/// <para>
/// System.Text.Json nodes built with helpers such as <c>JsonArray.Add(string)</c> wrap their value
/// in <c>JsonValueCustomized&lt;T&gt;</c>. Writing one calls the parameterless
/// <see cref="JsonSerializerOptions.MakeReadOnly()"/>, which throws unless a resolver is set:
/// "JsonSerializerOptions instance must specify a TypeInfoResolver setting before being marked as
/// read-only". Setting the resolver here keeps <c>ToJsonString(options)</c> working for every node
/// shape (primitive, parsed, or customized).
/// </para>
/// </summary>
public static class CoreJson
{
    /// <summary>Write-indented options (user-visible files, sidecar, config).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    /// <summary>Compact options (API request bodies).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
}
