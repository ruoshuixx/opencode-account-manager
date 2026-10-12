using AccountManager.Core.OpenCode;

namespace AccountManager.Core.Actions;

/// <summary>Shared form/core validation. Error messages never include keys or callback contents.</summary>
public static class ApiProviderValidation
{
    public static string Name(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length is 0 or > 120 || text.Any(char.IsControl))
        {
            throw new OpenCodeException("供应商名称不能为空、不能含控制字符，且不得超过 120 个字符。");
        }
        return text;
    }

    public static string Endpoint(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Any(char.IsWhiteSpace) || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new OpenCodeException("接口地址必须是无内嵌凭据、查询参数和片段的 HTTPS 地址。");
        }
        return text;
    }

    public static List<string> ParseModels(string? text)
    {
        var models = (text ?? string.Empty).Split(new[] { '\r', '\n', ',', '，' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();
        if (models.Count is 0 or > 256)
        {
            throw new OpenCodeException("请填写 1 到 256 个模型 ID，每行一个或用逗号分隔。");
        }
        if (models.Any(m => m.Length > 512 || m.Any(char.IsControl)))
        {
            throw new OpenCodeException("模型 ID 不能含控制字符，且不得超过 512 个字符。");
        }
        return models;
    }

    public static string Key(string? key, bool required)
    {
        var text = (key ?? string.Empty).Trim();
        if ((required && text.Length == 0) || text.Length > 8192 || text.Any(char.IsControl))
        {
            throw new OpenCodeException(required
                ? "请填写有效的 API Key（不能含控制字符）。"
                : "API Key 不能含控制字符；留空表示保留现有密钥。");
        }
        return text;
    }
}
