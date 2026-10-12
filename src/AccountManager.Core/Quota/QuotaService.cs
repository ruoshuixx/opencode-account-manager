using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using AccountManager.Core.Json;

namespace AccountManager.Core.Quota;

/// <summary>
/// Codex quota lookup: a single read-only HTTPS GET against a fixed URL, no redirects,
/// bounded response size, and fixed safe error messages (never echoing the token or body).
/// The HTTP boundary (<see cref="SendAsync"/>) can be replaced in tests.
/// </summary>
public class QuotaService
{
    public const string UsageUri = "https://chatgpt.com/backend-api/wham/usage";
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

    public virtual async Task<QuotaUsage> GetQuotaAsync(string accessToken, string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) { throw new InvalidOperationException("缺少访问令牌。"); }
        var json = await SendAsync(accessToken, accountId, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Replaceable HTTP boundary. Returns the decoded JSON body.</summary>
    protected virtual async Task<JsonNode> SendAsync(string accessToken, string accountId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        if (!string.IsNullOrWhiteSpace(accountId)) { request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId); }
        request.Headers.TryAddWithoutValidation("Origin", "https://chatgpt.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://chatgpt.com/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(ResolveError(0));
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(ResolveError(0));
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
                throw new InvalidOperationException(ResolveError(status));
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

    public static string ResolveError(int status) => status switch
    {
        401 => "令牌被拒绝：访问令牌可能已过期，请先在 OpenCode 中刷新后重试。",
        403 => "访问被拒绝：可能无权限或网络被拦截，并不代表令牌失效，请检查该账号的访问权限。",
        429 => "请求过于频繁（HTTP 429），请稍后重试。",
        >= 500 and <= 599 => "服务暂时不可用（HTTP " + status + "），请稍后重试。",
        > 0 => "请求失败（HTTP " + status + "）。",
        _ => "请求失败：网络不可达或超时。",
    };

    // ---- pure parsing -------------------------------------------------------

    private static readonly string[] PercentFields = { "used_percent", "usedPercent", "usage_percent", "usagePercent", "percent" };
    private static readonly string[][] UsedLimitPairs =
    {
        new[] { "used", "limit" }, new[] { "usage", "limit" }, new[] { "used", "total" },
        new[] { "usage", "total" }, new[] { "used", "allowed" }, new[] { "usage", "allowed" },
    };
    private static readonly string[] RemainingFields = { "remaining_percent", "remainingPercent" };
    private static readonly string[] ResetFields = { "reset_at", "resets_at", "resetAt", "reset_time" };
    private static readonly string[] ResetAfterFields = { "reset_after_seconds", "resetAfterSeconds" };
    private static readonly string[] DurationFields = { "limit_window_seconds", "window_seconds", "windowSeconds", "limit_window_sec" };
    private static readonly string[] PrimaryNames = { "primary_window", "primary", "five_hour_limit", "five_hour", "five_hour_window" };
    private static readonly string[] SecondaryNames = { "secondary_window", "secondary", "weekly_limit", "weekly", "weekly_window" };

    public static QuotaUsage Parse(JsonNode? json)
    {
        if (json is null) { throw new InvalidOperationException("限额响应为空。"); }
        var hasRateLimit = Field(json, "rate_limit") is not null || HasField(json, "rate_limits");
        if (!hasRateLimit) { throw new InvalidOperationException("限额响应缺少 rate_limit 字段（接口可能已变更）。"); }

        var rateLimit = Field(json, "rate_limit") ?? Field(json, "rate_limits");
        var plan = JsonMerge.GetString(json, "plan_type") ?? JsonMerge.GetString(json, "plan") ?? string.Empty;

        var result = new QuotaUsage
        {
            Plan = plan,
            PrimaryState = "Missing",
            SecondaryState = "Missing",
            Message = null,
            LimitReached = null,
            ResetCreditsAvailable = ReadResetCredits(json) ?? (rateLimit is not null ? ReadResetCredits(rateLimit) : null),
        };

        var explicitUnlimited = JsonNum.ToBool(Field(json, "unlimited"))
            || (rateLimit is not null && JsonNum.ToBool(Field(rateLimit, "unlimited")));

        if (explicitUnlimited)
        {
            result.PrimaryState = "Unlimited";
            result.SecondaryState = "Unlimited";
            result.Message = "套餐不限制用量。";
            result.LimitReached = false;
        }
        else if (rateLimit is null)
        {
            result.Message = "接口未返回窗口数据。";
        }
        else
        {
            var resolved = new List<(JsonNode Window, string? Slot, bool Confident, string? Rejected)>();
            foreach (var (window, nameSlot) in WindowCandidates(rateLimit))
            {
                var (slot, confident, rejected) = ResolveSlot(window, nameSlot);
                resolved.Add((window, slot, confident, rejected));
            }

            var placed = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            foreach (var item in resolved)
            {
                if (item.Confident && item.Slot is not null && !placed.ContainsKey(item.Slot)) { placed[item.Slot] = item.Window; }
            }

            foreach (var item in resolved)
            {
                if (!item.Confident && item.Slot is not null && !placed.ContainsKey(item.Slot)) { placed[item.Slot] = item.Window; }
            }

            var rejectedSlots = resolved.Where(r => r.Rejected is not null).Select(r => r.Rejected!).ToList();

            foreach (var slotName in new[] { "Primary", "Secondary" })
            {
                if (placed.TryGetValue(slotName, out var window))
                {
                    var read = ReadWindow(window);
                    if (slotName == "Primary")
                    {
                        result.PrimaryPercent = read.Percent;
                        result.PrimaryReset = read.Reset;
                        result.PrimaryState = read.State;
                    }
                    else
                    {
                        result.SecondaryPercent = read.Percent;
                        result.SecondaryReset = read.Reset;
                        result.SecondaryState = read.State;
                    }
                }
                else if (rejectedSlots.Contains(slotName))
                {
                    if (slotName == "Primary") { result.PrimaryState = "Invalid"; }
                    else { result.SecondaryState = "Invalid"; }
                }
            }

            var states = new[] { result.PrimaryState, result.SecondaryState };
            if (states.Contains("Invalid")) { result.Message = "部分窗口数据无法识别。"; }
            else if (states.Contains("Missing")) { result.Message = "部分窗口数据缺失。"; }
        }

        var hasExplicitLimit = false;
        foreach (var container in new[] { json, rateLimit })
        {
            if (container is not null && HasField(container, "limit_reached"))
            {
                result.LimitReached = JsonNum.ToBool(Field(container, "limit_reached"));
                hasExplicitLimit = true;
                break;
            }
        }

        if (!hasExplicitLimit && !explicitUnlimited)
        {
            var hasWindow = false;
            var reached = false;
            foreach (var state in new[] { result.PrimaryState, result.SecondaryState })
            {
                if (state != "Available") { continue; }
                hasWindow = true;
                var percent = state == result.PrimaryState ? result.PrimaryPercent : result.SecondaryPercent;
                if (percent is >= 100) { reached = true; }
            }

            if (hasWindow) { result.LimitReached = reached; }
        }

        return result;
    }

    private static readonly string[] ResetCreditsContainers = { "rate_limit_reset_credits", "reset_credits" };
    private static readonly string[] ResetCreditsCountFields = { "available_count", "available", "count", "remaining" };

    private static int? ReadResetCredits(JsonNode node)
    {
        foreach (var name in ResetCreditsContainers)
        {
            var container = Field(node, name);
            if (container is null) { continue; }
            foreach (var countName in ResetCreditsCountFields)
            {
                var value = JsonNum.ToNumber(Field(container, countName));
                if (value is not null && value >= 0) { return (int)value.Value; }
            }
        }

        return null;
    }

    private static IEnumerable<(JsonNode Window, string NameSlot)> WindowCandidates(JsonNode rateLimit)
    {
        foreach (var name in PrimaryNames)
        {
            var value = Field(rateLimit, name);
            if (value is not null) { yield return (value, "Primary"); }
        }

        foreach (var name in SecondaryNames)
        {
            var value = Field(rateLimit, name);
            if (value is not null) { yield return (value, "Secondary"); }
        }
    }

    private static (string? Slot, bool Confident, string? Rejected) ResolveSlot(JsonNode window, string nameSlot)
    {
        var durationField = DurationFields.FirstOrDefault(f => HasField(window, f));
        if (durationField is not null)
        {
            var seconds = JsonNum.ToNumber(Field(window, durationField));
            if (seconds == 18000) { return ("Primary", true, null); }
            if (seconds == 604800) { return ("Secondary", true, null); }
            return (null, true, nameSlot);
        }

        return (nameSlot, false, null);
    }

    private static (double? Percent, DateTimeOffset? Reset, string State) ReadWindow(JsonNode window)
    {
        var reset = WindowReset(window);
        if (JsonNum.ToBool(Field(window, "unlimited"))) { return (null, reset, "Unlimited"); }
        var percent = WindowPercent(window);
        return percent is not null ? (percent, reset, "Available") : (null, reset, "Invalid");
    }

    private static double? WindowPercent(JsonNode window)
    {
        foreach (var name in PercentFields)
        {
            if (HasField(window, name))
            {
                var value = JsonNum.ToNumber(Field(window, name));
                if (value is not null) { return value; }
            }
        }

        foreach (var pair in UsedLimitPairs)
        {
            var used = JsonNum.ToNumber(Field(window, pair[0]));
            var limit = JsonNum.ToNumber(Field(window, pair[1]));
            if (used is not null && limit is > 0) { return used / limit * 100; }
        }

        foreach (var name in RemainingFields)
        {
            if (HasField(window, name))
            {
                var remaining = JsonNum.ToNumber(Field(window, name));
                if (remaining is not null) { return 100 - remaining; }
            }
        }

        return null;
    }

    private static DateTimeOffset? WindowReset(JsonNode window)
    {
        foreach (var name in ResetFields)
        {
            if (HasField(window, name))
            {
                var reset = ConvertResetTime(Field(window, name));
                if (reset is not null) { return reset; }
            }
        }

        foreach (var name in ResetAfterFields)
        {
            if (HasField(window, name))
            {
                var seconds = JsonNum.ToNumber(Field(window, name));
                if (seconds is >= 0) { return DateTimeOffset.UtcNow.AddSeconds(seconds.Value); }
            }
        }

        return null;
    }

    public static DateTimeOffset? ConvertResetTime(JsonNode? node)
    {
        if (node is null) { return null; }
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            text = text.Trim();
            if (text.Length == 0) { return null; }
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+(\.\d+)?$"))
            {
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? FromEpoch(n) : null;
            }

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
        }

        var number = JsonNum.ToNumber(node);
        return number is null ? null : FromEpoch(number.Value);
    }

    private static DateTimeOffset? FromEpoch(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number)) { return null; }
        try
        {
            if (number >= 1e12) { return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(number)); }
            if (number >= 1e9) { return DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(number)); }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static JsonNode? Field(JsonNode? node, string name) => JsonMerge.Get(node, name);

    private static bool HasField(JsonNode? node, string name)
        => node is JsonObject obj && obj.ContainsKey(name);

    public static string FormatDuration(DateTimeOffset? reset)
    {
        if (reset is null) { return string.Empty; }
        var span = reset.Value.ToUniversalTime() - DateTimeOffset.UtcNow;
        if (span.TotalSeconds <= 0) { return "即将重置"; }
        if (span.TotalDays >= 1) { return $"{(int)span.Days}d{span.Hours}h"; }
        if (span.TotalHours >= 1) { return $"{(int)span.Hours}h{span.Minutes}m"; }
        return $"{(int)Math.Max(1, span.TotalMinutes)}m";
    }

    public static string? FormatAbsoluteTime(DateTimeOffset? reset)
        => reset?.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
