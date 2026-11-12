using System.Text.Json;
using QuotaDesk.Models;

namespace QuotaDesk.Providers.Glm;

/// <summary>智谱额度接口失败分类。展示文本由 DisplayMapper 处理，这里只给机器码。</summary>
public static class GlmErrorCodes
{
    public const string Unauthorized = "Unauthorized";
    public const string RateLimited = "RateLimited";
    public const string NoCodingPlan = "NoCodingPlan";
    public const string ServerError = "ServerError";
}

/// <summary>
/// 智谱国内 Coding Plan 额度接口（GET https://open.bigmodel.cn/api/monitor/usage/quota/limit）
/// 响应 → 统一快照。
///
/// 响应信封 {success, code, msg, data}；
/// limits[] 的 5h 窗口 unit=3/number=5、周窗口 unit=6/number=1；
/// percentage = 已用比例（currentValue/usage）；
/// 真实窗口类型为 CREDIT_LIMIT（lite 套餐），TOKENS_LIMIT 保留兼容；
/// 套餐名走 data.level（真实响应无 planName/plan 字段）。
/// </summary>
public static class GlmUsageParser
{
    public const string Source = "glm-monitor";

    public const long FiveHourMinutes = 300;     // unit=3(小时) number=5
    public const long WeeklyMinutes = 10080;     // unit=6(周) number=1
    public const long MonthlyMinutes = 43200;    // 30 天，按 Other 展示

    public static ProviderSnapshot Parse(string json, DateTimeOffset now, string? accountKey = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci)
            ? ci
            : 0;
        var msg = root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        if (!success || code != 200)
        {
            return new ProviderSnapshot
            {
                ProviderId = "glm",
                Source = Source,
                Windows = [],
                ObservedAt = now,
                LastAttemptAt = now,
                State = ProviderState.Unavailable,
                ErrorCode = Classify(code, msg),
                AccountKey = accountKey,
            };
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return Fail(now, GlmErrorCodes.ServerError);
        }

        var plan = new[] { "planName", "plan", "planType", "packageName", "level" }
            .Select(key => data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var windows = new List<QuotaWindow>();
        if (data.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                var window = ParseLimit(limit);
                if (window is not null) windows.Add(window);
            }
        }

        return new ProviderSnapshot
        {
            ProviderId = "glm",
            Source = Source,
            Plan = plan,
            Windows = windows,
            ObservedAt = now,
            LastAttemptAt = now,
            State = ProviderState.Live,
            AccountKey = accountKey,
        };
    }

    private static QuotaWindow? ParseLimit(JsonElement limit)
    {
        if (limit.ValueKind != JsonValueKind.Object) return null;

        // 只认携带配额的类型；MCP 的 TIME_LIMIT 等无百分比窗口不硬造额度。
        var type = limit.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var hasQuota = type is "TOKENS_LIMIT" or "CREDIT_LIMIT";
        if (!hasQuota) return null;

        double? used = limit.TryGetProperty("percentage", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble()
            : null;

        long? minutes = Minutes(limit);
        DateTimeOffset? reset = null;
        if (limit.TryGetProperty("nextResetTime", out var nrt) && nrt.ValueKind == JsonValueKind.Number
            && nrt.TryGetInt64(out var epochMs) && epochMs > 0)
        {
            reset = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
        }

        return new QuotaWindow
        {
            Id = $"glm:{type}:{minutes?.ToString() ?? "?"}",
            Kind = KindOf(minutes),
            Scope = type,
            UsedPercent = used,
            ResetsAt = reset,
            StartState = StartState.Unknown,
        };
    }

    /// <summary>unit 1=天(1440分) 3=小时(60分) 5=分钟(1) 6=周(10080分)，依据 Pulse 源码。</summary>
    private static long? Minutes(JsonElement limit)
    {
        var unit = limit.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.Number && u.TryGetInt32(out var ui) ? ui : (int?)null;
        var number = limit.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var ni) ? ni : (long?)null;
        if (unit is null || number is null) return null;
        return unit switch
        {
            1 => number * 1440,
            3 => number * 60,
            5 => number,
            6 => number * 10080,
            _ => null,
        };
    }

    private static WindowKind KindOf(long? minutes) => minutes switch
    {
        FiveHourMinutes => WindowKind.FiveHour,
        WeeklyMinutes => WindowKind.Weekly,
        _ => WindowKind.Other,
    };

    private static ProviderSnapshot Fail(DateTimeOffset now, string errorCode) => new()
    {
        ProviderId = "glm",
        Source = Source,
        Windows = [],
        ObservedAt = now,
        LastAttemptAt = now,
        State = ProviderState.Unavailable,
        ErrorCode = errorCode,
    };

    /// <summary>信封错误分类。依据：Pulse problem(_:) —— 先查 coding plan 字样，再查认证关键词，
    /// 信封码 401/403、429、1000–1099（认证族，含区域不匹配的 1000 身份验证失败）。</summary>
    public static string Classify(int code, string? msg)
    {
        if (!string.IsNullOrEmpty(msg))
        {
            if (msg.Contains("coding plan", StringComparison.OrdinalIgnoreCase)) return GlmErrorCodes.NoCodingPlan;
            string[] authHints = ["身份验证", "令牌", "token", "auth"];
            if (authHints.Any(h => msg.Contains(h, StringComparison.OrdinalIgnoreCase))) return GlmErrorCodes.Unauthorized;
        }
        return code switch
        {
            401 or 403 => GlmErrorCodes.Unauthorized,
            429 => GlmErrorCodes.RateLimited,
            >= 1000 and <= 1099 => GlmErrorCodes.Unauthorized,
            _ => GlmErrorCodes.ServerError,
        };
    }
}
