using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Json;

namespace AccountManager.Core.Quota;

/// <summary>
/// Codex "banked rate-limit reset" credits.
/// <list type="bullet">
///   <item><c>GET /wham/rate-limit-reset-credits</c> lists credits (id / status / granted / expires).</item>
///   <item><c>POST /wham/rate-limit-reset-credits/consume</c> redeems one; the credit is gone on HTTP 200.</item>
/// </list>
/// Undocumented endpoints: never echoes the token or body, fixed error messages, bounded response size.
/// The HTTP boundary (<see cref="SendAsync"/>) is replaceable in tests.
/// </summary>
public class ResetCreditService
{
    public const string ListUri = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    public const string ConsumeUri = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume";
    public const int TimeoutSeconds = 18;
    public const int MaxBytes = 1048576;

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        return new HttpClient(handler);
    }

    public virtual async Task<ResetCreditList> GetListAsync(string accessToken, string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) { throw new InvalidOperationException("缺少访问令牌。"); }
        var json = await SendAsync(HttpMethod.Get, ListUri, accessToken, accountId, body: null, cancellationToken).ConfigureAwait(false);
        return ParseList(json);
    }

    public virtual async Task<ResetConsumeResult> ConsumeAsync(string accessToken, string accountId, string creditId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) { throw new InvalidOperationException("缺少访问令牌。"); }
        if (string.IsNullOrWhiteSpace(creditId)) { throw new InvalidOperationException("缺少要使用的重置券 ID。"); }

        var body = new JsonObject
        {
            ["credit_id"] = creditId,
            ["redeem_request_id"] = Guid.NewGuid().ToString(),
        };
        var json = await SendAsync(HttpMethod.Post, ConsumeUri, accessToken, accountId, body, cancellationToken).ConfigureAwait(false);
        return ParseConsume(json);
    }

    /// <summary>Replaceable HTTP boundary. Returns the decoded JSON body.</summary>
    protected virtual async Task<JsonNode> SendAsync(HttpMethod method, string url, string accessToken, string accountId, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        if (!string.IsNullOrWhiteSpace(accountId)) { request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId); }
        request.Headers.TryAddWithoutValidation("Origin", "https://chatgpt.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://chatgpt.com/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(QuotaService.ResolveError(0));
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(QuotaService.ResolveError(0));
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                throw new InvalidOperationException("请求被重定向，出于安全考虑已中止。");
            }

            if (status is < 200 or >= 300)
            {
                throw new InvalidOperationException(QuotaService.ResolveError(status));
            }

            var text = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) { throw new InvalidOperationException("响应为空。"); }
            try
            {
                return JsonNode.Parse(text) ?? throw new InvalidOperationException("响应解析失败：接口可能已变更。");
            }
            catch (System.Text.Json.JsonException)
            {
                throw new InvalidOperationException("响应解析失败：接口可能已变更。");
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxBytes) { throw new InvalidOperationException("响应过大，已中止解析。"); }
            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    // ---- pure parsing -------------------------------------------------------

    public static ResetCreditList ParseList(JsonNode? json)
    {
        if (json is null) { throw new InvalidOperationException("重置券响应为空。"); }

        var credits = new List<ResetCredit>();
        if (JsonMerge.Get(json, "credits") is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is null) { continue; }
                credits.Add(new ResetCredit
                {
                    Id = JsonMerge.GetString(node, "id") ?? string.Empty,
                    ResetType = JsonMerge.GetString(node, "reset_type") ?? string.Empty,
                    Status = JsonMerge.GetString(node, "status") ?? string.Empty,
                    GrantedAt = QuotaService.ConvertResetTime(JsonMerge.Get(node, "granted_at")),
                    ExpiresAt = QuotaService.ConvertResetTime(JsonMerge.Get(node, "expires_at")),
                    RedeemedAt = QuotaService.ConvertResetTime(JsonMerge.Get(node, "redeemed_at")),
                    Title = JsonMerge.GetString(node, "title") ?? string.Empty,
                });
            }
        }

        var count = JsonNum.ToNumber(JsonMerge.Get(json, "available_count"));
        return new ResetCreditList
        {
            Credits = credits,
            AvailableCount = count is not null ? (int)count.Value : credits.Count(c => c.IsAvailable),
        };
    }

    public static ResetConsumeResult ParseConsume(JsonNode? json)
    {
        if (json is null) { throw new InvalidOperationException("重置响应为空。"); }

        var result = new ResetConsumeResult
        {
            Code = JsonMerge.GetString(json, "code") ?? string.Empty,
            WindowsReset = (int)(JsonNum.ToNumber(JsonMerge.Get(json, "windows_reset")) ?? 0),
        };

        if (JsonMerge.Get(json, "credit") is { } credit)
        {
            result.CreditId = JsonMerge.GetString(credit, "id") ?? string.Empty;
            result.RedeemedAt = QuotaService.ConvertResetTime(JsonMerge.Get(credit, "redeemed_at"));
        }

        return result;
    }

    /// <summary>Short human countdown to a credit's expiry, e.g. "2d 9h" / "3h12m".</summary>
    public static string FormatExpiry(DateTimeOffset? expires, DateTimeOffset now)
    {
        if (expires is null) { return string.Empty; }
        var span = expires.Value.ToUniversalTime() - now.ToUniversalTime();
        if (span.TotalSeconds <= 0) { return "已过期"; }
        if (span.TotalDays >= 1) { return $"{(int)span.TotalDays}d{span.Hours}h"; }
        if (span.TotalHours >= 1) { return $"{(int)span.TotalHours}h{span.Minutes}m"; }
        return $"{(int)Math.Max(1, span.TotalMinutes)}m";
    }
}
