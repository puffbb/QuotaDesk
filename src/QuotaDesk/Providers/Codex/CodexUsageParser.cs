using System.Text.Json;
using QuotaDesk.Models;

namespace QuotaDesk.Providers.Codex;

/// <summary>
/// Codex 客户端用量接口（GET https://chatgpt.com/backend-api/wham/usage）200 响应 → 统一快照。
/// 此接口并非公开承诺，响应结构及字段可能变化：
/// 窗口按时长识别（18000s=5h、604800s=周），缺失字段保持缺失，已用百分比可超 100 原样保留。
/// </summary>
public static class CodexUsageParser
{
    public const string Source = "codex-wham";

    private const double FiveHourSeconds = 5 * 3600;
    private const double WeeklySeconds = 7 * 24 * 3600;

    public static ProviderSnapshot Parse(string json, DateTimeOffset now, string? accountKey = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var plan = root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String
            ? pt.GetString()
            : null;

        var windows = new List<QuotaWindow>();

        if (root.TryGetProperty("rate_limit", out var group) && group.ValueKind == JsonValueKind.Object)
            windows.AddRange(ParseGroup(group, scope: null));

        if (root.TryGetProperty("additional_rate_limits", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in extra.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = item.TryGetProperty("limit_name", out var ln) && ln.ValueKind == JsonValueKind.String
                    ? ln.GetString()
                    : null;
                if (item.TryGetProperty("rate_limit", out var sub) && sub.ValueKind == JsonValueKind.Object)
                    windows.AddRange(ParseGroup(sub, scope: name));
            }
        }

        return new ProviderSnapshot
        {
            ProviderId = "codex",
            Source = Source,
            Plan = plan,
            Windows = windows,
            ObservedAt = now,
            LastAttemptAt = now,
            State = ProviderState.Live,
            AccountKey = accountKey,
        };
    }

    private static IEnumerable<QuotaWindow> ParseGroup(JsonElement group, string? scope)
    {
        // limit_reached 是组级标志（Codex 标记整组而非单个窗口），保守应用到组内所有窗口。
        var groupReached = group.TryGetProperty("limit_reached", out var lr) && lr.ValueKind == JsonValueKind.True;

        foreach (var slot in new[] { "primary_window", "secondary_window" })
        {
            if (!group.TryGetProperty(slot, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            var window = ParseWindow(w, scope, groupReached);
            if (window is not null) yield return window;
        }
    }

    private static QuotaWindow? ParseWindow(JsonElement w, string? scope, bool groupReached)
    {
        double? used = w.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number
            ? up.GetDouble()
            : null;

        double? seconds = w.TryGetProperty("limit_window_seconds", out var lws) && lws.ValueKind == JsonValueKind.Number
            ? lws.GetDouble()
            : null;

        DateTimeOffset? reset = null;
        if (w.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number
            && ra.TryGetInt64(out var epoch))
        {
            reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return new QuotaWindow
        {
            Id = $"codex:{scope ?? "main"}:{seconds?.ToString("0") ?? "?"}",
            Kind = KindOf(seconds),
            Scope = scope,
            UsedPercent = used,
            LimitReached = groupReached ? true : null,
            WindowSeconds = seconds,
            ResetsAt = reset,
            StartState = StartState.Unknown,
        };
    }

    private static WindowKind KindOf(double? windowSeconds) => windowSeconds switch
    {
        FiveHourSeconds => WindowKind.FiveHour,
        WeeklySeconds => WindowKind.Weekly,
        _ => WindowKind.Other,
    };
}
