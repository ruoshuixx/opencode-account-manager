using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccountManager.Core.Json;

/// <summary>
/// Tolerant JSON/JSONC reader. Strips <c>//</c> and <c>/* */</c> comments outside strings
/// and removes trailing commas, then parses with <see cref="JsonNode"/>.
/// Mirrors the PowerShell <c>Read-Jsonc</c> behaviour so the same files round-trip.
/// </summary>
public static class Jsonc
{
    public static JsonNode? Parse(string text)
    {
        var cleaned = StripComments(text);
        cleaned = StripTrailingCommas(cleaned);
        try
        {
            return JsonNode.Parse(cleaned);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("全局配置不能安全解析；已停止操作，未覆盖该文件。", ex);
        }
    }

    private static string StripComments(string text)
    {
        var b = new StringBuilder(text.Length);
        var quoted = false;
        var escape = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                b.Append(c);
                if (escape) { escape = false; }
                else if (c == '\\') { escape = true; }
                else if (c == '"') { quoted = false; }
                continue;
            }

            if (c == '"') { quoted = true; b.Append(c); continue; }

            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i += 2;
                    while (i < text.Length && text[i] != '\n') { i++; }
                    b.Append('\n');
                    continue;
                }

                if (text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) { throw new InvalidDataException("配置文件存在未结束的注释。"); }
                    b.Append(' ');
                    i = end + 1;
                    continue;
                }
            }

            b.Append(c);
        }

        return b.ToString();
    }

    private static string StripTrailingCommas(string text)
    {
        var b = new StringBuilder(text.Length);
        var quoted = false;
        var escape = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!quoted && c == ',')
            {
                var next = i + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next])) { next++; }
                if (next < text.Length && (text[next] == '}' || text[next] == ']')) { continue; }
            }

            b.Append(c);
            if (quoted)
            {
                if (escape) { escape = false; }
                else if (c == '\\') { escape = true; }
                else if (c == '"') { quoted = false; }
            }
            else if (c == '"') { quoted = true; }
        }

        return b.ToString();
    }
}
