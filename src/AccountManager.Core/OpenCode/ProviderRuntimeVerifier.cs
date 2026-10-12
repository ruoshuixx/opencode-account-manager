using System.Text.Json.Nodes;
using AccountManager.Core.Json;

namespace AccountManager.Core.OpenCode;

/// <summary>Read-only runtime boundary; unlike the disk projection, this is what OpenCode sees.</summary>
public interface IProviderRuntimeReader
{
    Task<JsonNode?> GetProviderAsync(string providerId, CancellationToken ct = default);
}

public enum ProviderRuntimeState { Applied, Pending, Unavailable }

public sealed record ProviderRuntimeVerification(ProviderRuntimeState State)
{
    public string Message => State switch
    {
        ProviderRuntimeState.Applied => ProviderRuntimeVerifier.AppliedMessage,
        ProviderRuntimeState.Pending => ProviderRuntimeVerifier.PendingMessage,
        _ => ProviderRuntimeVerifier.UnavailableMessage,
    };
}

public sealed class ProviderRuntimeVerifier(IProviderRuntimeReader reader)
{
    public const string AppliedMessage = "OpenCode 后台已识别所选协议，无需重启；正在进行的请求不会中途切换协议。";
    public const string PendingMessage = "配置文件已保存，但尚未确认 OpenCode 后台采用新协议；请稍后重读并检查项目覆盖，不要重复添加或更换 Key。";
    public const string UnavailableMessage = "配置文件已保存，但无法读取 OpenCode 后台协议；不代表保存失败，也不会自动重启或重试写入。";

    /// <summary>No reload, restart, credential mutation or model request. Bounded watcher settling.</summary>
    public async Task<ProviderRuntimeVerification> VerifyAsync(string providerId, string expectedPackage, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var readable = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await reader.GetProviderAsync(providerId, deadline.Token).ConfigureAwait(false);
                var provider = JsonMerge.Get(response, "data") ?? response;
                if (!string.Equals(JsonMerge.GetString(provider, "id"), providerId, StringComparison.Ordinal))
                {
                    return new(ProviderRuntimeState.Unavailable);
                }
                var package = JsonMerge.GetString(provider, "package");
                readable |= !string.IsNullOrWhiteSpace(package);
                if (string.Equals(package, expectedPackage, StringComparison.Ordinal)) { return new(ProviderRuntimeState.Applied); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return new(ProviderRuntimeState.Unavailable); } // Never expose raw output or retry a failed request.
            if (attempt < 2)
            {
                try { await Task.Delay(150, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { return new(ProviderRuntimeState.Unavailable); }
            }
        }
        return new(readable ? ProviderRuntimeState.Pending : ProviderRuntimeState.Unavailable);
    }
}
