using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;

namespace AccountManager.App.Services;

/// <summary>The deliberately small, no-services entry used to inspect a candidate bundle.</summary>
internal static class UpdateProbe
{
    internal const string Flag = "--update-probe";
    private const string ProductName = "OpenCodeAccountManager";

    internal static bool TryHandle(string[] args, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        exitCode = 1;
        if (!args.Any(argument => argument.StartsWith("--update-probe", StringComparison.Ordinal))) { return false; }

        if (args.Length != 1 || !string.Equals(args[0], Flag, StringComparison.Ordinal))
        {
            return true;
        }

        var platform = GetPlatform();
        if (platform is null) { return true; }

        try
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new ProbeDocument(
                ProductName,
                AppUpdateInfo.Version.ToString(3),
                platform,
                1)));
            exitCode = 0;
        }
        catch
        {
            exitCode = 1;
        }

        return true;
    }

    internal static bool IsExpectedOutput(string? output, Version expectedVersion, string expectedPlatform)
    {
        if (string.IsNullOrEmpty(output) || output.Length > 2048) { return false; }
        try
        {
            var jsonLine = output;
            if (jsonLine.EndsWith("\r\n", StringComparison.Ordinal)) { jsonLine = jsonLine[..^2]; }
            else if (jsonLine.EndsWith('\n')) { jsonLine = jsonLine[..^1]; }
            if (jsonLine.Contains('\r') || jsonLine.Contains('\n')) { return false; }
            using var document = JsonDocument.Parse(jsonLine, new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4) { return false; }
            if (!root.TryGetProperty("product", out var product) || product.GetString() != ProductName) { return false; }
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String) { return false; }
            if (!TryParseThreePartVersion(version.GetString(), out var actual) || actual != Normalize(expectedVersion)) { return false; }
            if (!root.TryGetProperty("platform", out var platform) || platform.GetString() != expectedPlatform) { return false; }
            return root.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == JsonValueKind.Number && protocol.GetInt32() == 1;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryParseThreePartVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrEmpty(value)) { return false; }
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(c => c is < '0' or > '9'))) { return false; }
        if (!Version.TryParse(value, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0) { return false; }
        if (!string.Equals(parsed.ToString(3), value, StringComparison.Ordinal)) { return false; }
        version = parsed;
        return true;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static string? GetPlatform()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) { return null; }
        if (OperatingSystem.IsLinux()) { return "linux-x64"; }
        if (OperatingSystem.IsWindows()) { return "win-x64"; }
        return null;
    }

    private sealed record ProbeDocument(
        [property: JsonPropertyName("product")] string Product,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("protocol")] int Protocol);
}
