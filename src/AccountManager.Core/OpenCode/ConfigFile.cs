using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Json;

namespace AccountManager.Core.OpenCode;

/// <summary>A global config document plus the raw text it was read from.</summary>
public sealed class ConfigDocument
{
    public string Path { get; init; } = string.Empty;

    /// <summary>Raw text at read time; null when the file did not exist yet.</summary>
    public string? Raw { get; init; }

    public JsonObject Config { get; init; } = new();

    public ConfigDocument Clone() => new()
    {
        Path = Path,
        Raw = Raw,
        Config = (JsonObject)Config.DeepClone(),
    };
}

/// <summary>Reads/writes the global <c>opencode.json(c)</c> with a concurrency guard.</summary>
public sealed class ConfigFile : IConfigFile
{
    private static readonly JsonSerializerOptions WriteOptions = CoreJson.Indented;

    private readonly OpenCodeCli _cli;

    public ConfigFile(OpenCodeCli cli) => _cli = cli;

    public async Task<ConfigDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        var dir = await _cli.GetConfigDirectoryAsync(cancellationToken).ConfigureAwait(false);
        var json = Path.Combine(dir, "opencode.json");
        var jsonc = Path.Combine(dir, "opencode.jsonc");
        if (File.Exists(json) && File.Exists(jsonc))
        {
            throw new OpenCodeException("全局目录同时存在 opencode.json 和 opencode.jsonc。请先确认要修改哪个文件；本工具不会猜测。");
        }

        var path = File.Exists(jsonc) ? jsonc : json;
        string? raw = null;
        JsonObject config;
        if (File.Exists(path))
        {
            raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            config = Jsonc.Parse(raw) as JsonObject
                ?? throw new OpenCodeException("全局配置必须是 JSON 对象。");
        }
        else
        {
            config = new JsonObject { ["$schema"] = "https://opencode.ai/config.json" };
        }

        return new ConfigDocument { Path = path, Raw = raw, Config = config };
    }

    /// <summary>
    /// Serialise and atomically replace the config, refusing to overwrite edits made since
    /// <see cref="ConfigDocument.Raw"/> was captured.
    /// </summary>
    public async Task WriteAsync(ConfigDocument document, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(document.Path)!;
        Directory.CreateDirectory(dir);

        var now = File.Exists(document.Path) ? await File.ReadAllTextAsync(document.Path, cancellationToken).ConfigureAwait(false) : null;
        if (!string.Equals(now, document.Raw, StringComparison.Ordinal))
        {
            throw new OpenCodeException("全局配置已被其他程序修改，停止写入；请重新预览。");
        }

        var temp = document.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, document.Config.ToJsonString(WriteOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var beforeReplace = File.Exists(document.Path) ? await File.ReadAllTextAsync(document.Path, cancellationToken).ConfigureAwait(false) : null;
            if (!string.Equals(beforeReplace, document.Raw, StringComparison.Ordinal))
            {
                throw new OpenCodeException("全局配置在保存期间被其他程序修改，停止替换；请重新读取。");
            }

            if (File.Exists(document.Path)) { File.Replace(temp, document.Path, null); }
            else { File.Move(temp, document.Path); }
        }
        finally
        {
            if (File.Exists(temp)) { File.Delete(temp); }
        }
    }
}
