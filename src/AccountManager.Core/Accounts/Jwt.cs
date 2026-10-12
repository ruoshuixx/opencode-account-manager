using System.Text;
using System.Text.Json.Nodes;

namespace AccountManager.Core.Accounts;

/// <summary>Decodes a JWT payload without validating the signature (display/metadata only).</summary>
public static class Jwt
{
    public static JsonNode? ReadPayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) { return null; }
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) { return null; }
            var part = parts[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(part));
            return JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }
}
