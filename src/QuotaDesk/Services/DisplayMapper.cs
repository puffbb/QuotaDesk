using QuotaDesk.Models;

namespace QuotaDesk.Services;

public enum Severity
{
    Normal,
    /// <summary>剩余量低于警告阈值（默认 20%）。</summary>
    Warn,
    /// <summary>剩余量低于严重阈值（默认 10%）或已超额/达上限。</summary>
    Critical,
}

/// <summary>一行额度（一个窗口）的最终展示数据。纯数据，不依赖 WPF，便于测试。</summary>
public sealed record RowDisplay
{
    public required string Label { get; init; }        // "5h" / "周"
    public required string PercentText { get; init; }  // "72%" / "—" / "137%"
    public required double Fraction { get; init; }     // 进度条填充比例 0..1
    public required Severity Severity { get; init; }
    public string? RightText { get; init; }            // 倒计时 / 重置时间 / 等待更新
    public string? StatusText { get; init; }           // 未报告 / 未开始 / 旧数据·14:02 / 已超额 …
    public string? CompactSuffix { get; init; }        // 紧凑模式的短标记（旧 / 超额 / 等待）
    public string? ToolTip { get; init; }
    public bool HasData { get; init; } = true;
}

public sealed record ProviderDisplay
{
    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public string? UpdateText { get; init; }   // “更新于 14:32”，按平台独立
    public string? ErrorText { get; init; }    // “获取失败：网络错误” / “需要重新登录” / “检查密钥”
    public required IReadOnlyList<RowDisplay> Rows { get; init; }
    public required bool IsDemo { get; init; }
}

/// <summary>
/// 快照 → 展示数据的映射。规则来自 PRODUCT.md / DATA.md：
/// 展示值 clamp(100-used, 0, 100)；原始已用百分比保留用于超额判断；
/// 缺失数值不当 0；服务未返回的窗口显示“未报告”；明确未开始才显示“未开始”；
/// 重置已过标“等待更新”，不得擅自显示为恢复。
/// </summary>
public static class DisplayMapper
{
    public static double RemainingPercent(double usedPercent) => Math.Clamp(100 - usedPercent, 0, 100);

    public static Severity SeverityOf(double remainingPercent, AppSettings settings)
    {
        if (remainingPercent <= settings.CriticalRemainingPercent) return Severity.Critical;
        if (remainingPercent <= settings.WarnRemainingPercent) return Severity.Warn;
        return Severity.Normal;
    }

    public static ProviderDisplay Map(ProviderSnapshot snapshot, AppSettings settings, DateTimeOffset now)
    {
        string displayName = snapshot.ProviderId switch
        {
            "glm" => "GLM",
            "codex" => "Codex",
            var id => id,
        };

        var errorText = MapError(snapshot);
        var showOld = snapshot.State == ProviderState.Stale;
        var stalePrefix = showOld ? $"旧数据·{TimeText.HHmm(snapshot.ObservedAt)} " : "";

        var rows = new List<RowDisplay>();
        var ordered = snapshot.Windows
            .OrderBy(w => w.Kind switch { WindowKind.FiveHour => 0, WindowKind.Weekly => 1, _ => 2 })
            .ToList();

        foreach (var kind in new[] { WindowKind.FiveHour, WindowKind.Weekly })
        {
            var w = ordered.FirstOrDefault(x => x.Kind == kind);
            rows.Add(MapRow(kind, w, settings, now, stalePrefix, snapshot.State));
        }

        // 同类多窗口（不同 scope）在详细模式追加展示，紧凑模式只取前两个。
        foreach (var w in ordered.Where(x => x.Kind == WindowKind.Other))
            rows.Add(MapRow(WindowKind.Other, w, settings, now, stalePrefix, snapshot.State));

        string? updateText = snapshot.State == ProviderState.Unavailable && snapshot.Windows.Count == 0
            ? null
            : $"更新于 {TimeText.HHmm(snapshot.ObservedAt)}";
        if (showOld) updateText = $"更新于 {TimeText.HHmm(snapshot.ObservedAt)}（旧）";

        return new ProviderDisplay
        {
            ProviderId = snapshot.ProviderId,
            DisplayName = displayName,
            UpdateText = updateText,
            ErrorText = errorText,
            Rows = rows,
            IsDemo = snapshot.Source == "mock",
        };
    }

    private static RowDisplay MapRow(WindowKind kind, QuotaWindow? w, AppSettings settings,
        DateTimeOffset now, string stalePrefix, ProviderState state)
    {
        var label = kind switch { WindowKind.FiveHour => "5h", WindowKind.Weekly => "周", _ => "其他" };

        if (w is null)
        {
            // 服务没有返回该窗口：不能补造一个额度。整体请求失败时按“暂无数据”展示，
            // 服务在线但缺该窗口才显示“未报告”。
            var absentStatus = state == ProviderState.Unavailable ? "暂无数据" : "未报告";
            return new RowDisplay
            {
                Label = label,
                PercentText = "—",
                Fraction = 0,
                Severity = Severity.Normal,
                RightText = null,
                StatusText = absentStatus,
                CompactSuffix = null,
                ToolTip = state == ProviderState.Unavailable ? "请求失败，暂无数据" : "服务本次未报告该窗口",
                HasData = false,
            };
        }

        if (w.StartState == StartState.NotStarted)
        {
            return new RowDisplay
            {
                Label = label,
                PercentText = "—",
                Fraction = 0,
                Severity = Severity.Normal,
                RightText = null,
                StatusText = "未开始",
                CompactSuffix = null,
                ToolTip = "服务明确表示该窗口尚未开始",
                HasData = false,
            };
        }

        if (w.UsedPercent is null)
        {
            return new RowDisplay
            {
                Label = label,
                PercentText = "—",
                Fraction = 0,
                Severity = Severity.Normal,
                RightText = null,
                StatusText = "暂无数据",
                CompactSuffix = stalePrefix.TrimEnd(),
                ToolTip = "服务未返回该窗口的数值",
                HasData = false,
            };
        }

        double used = w.UsedPercent.Value;
        double remaining = RemainingPercent(used);
        var severity = SeverityOf(remaining, settings);
        bool exceeded = used > 100 || w.LimitReached == true;

        string percentText = settings.ShowRemaining
            ? $"{Math.Round(remaining)}%"
            : $"{Math.Round(used)}%";
        // 进度条表达当前显示口径，但超额时已用方向也封顶 100%。
        double fraction = settings.ShowRemaining ? remaining / 100.0 : Math.Min(used, 100) / 100.0;

        string? right = null;
        if (w.ResetsAt is { } reset)
        {
            if (reset <= now)
            {
                right = "等待更新";
            }
            else if (kind == WindowKind.FiveHour)
            {
                right = TimeText.Countdown(now, reset);
            }
            else
            {
                right = TimeText.ResetDateTime(now, reset);
            }
        }

        string? status = exceeded ? (used > 100 ? "已超额" : "已达上限") : null;
        if (state == ProviderState.Stale) status = string.IsNullOrEmpty(status) ? "旧数据" : $"{status} · 旧数据";
        string? suffix = exceeded ? "超额" : resetPassed(w, now) ? "等待" : state == ProviderState.Stale ? "旧" : null;

        var tooltip = w.ResetsAt is { } r
            ? $"重置时间：{r.LocalDateTime:yyyy-MM-dd HH:mm}（本地）"
            : null;
        if (used > 100) tooltip = $"已用 {Math.Round(used)}%，超出限额。{tooltip}";

        return new RowDisplay
        {
            Label = label,
            PercentText = percentText,
            Fraction = fraction,
            Severity = severity,
            RightText = right,
            StatusText = status,
            CompactSuffix = suffix,
            ToolTip = tooltip,
            HasData = true,
        };
    }

    private static bool resetPassed(QuotaWindow w, DateTimeOffset now) => w.ResetsAt is { } r && r <= now;

    /// <summary>错误码 → 展示文本。认证失败按平台给出不同指引；缺失凭据给配置提示。</summary>
    public static string? MapError(ProviderSnapshot snapshot)
    {
        if (snapshot.State != ProviderState.Unavailable) return null;
        return snapshot.ErrorCode switch
        {
            "Unauthorized" => snapshot.ProviderId switch
            {
                "codex" => "需要重新登录",
                "glm" => "检查密钥",
                _ => "认证失败",
            },
            "NoCredentials" => snapshot.ProviderId switch
            {
                "codex" => "未检测到 Codex 登录",
                "glm" => "未配置密钥",
                _ => "未配置凭据",
            },
            "AuthFileUnreadable" => "登录文件无法读取",
            "KeyFileUnreadable" => snapshot.ProviderId == "glm"
                ? "密钥文件无法解密，请在设置中重新保存或删除"
                : "凭据文件无法读取",
            "RateLimited" => "请求过于频繁，稍后自动重试",
            "NoCodingPlan" => "未订阅 Coding Plan",
            "Timeout" => "请求超时",
            "NetworkError" => "网络错误",
            "BadResponse" => "响应格式异常，已保留旧数据",
            "InternalError" => "内部错误，等待下次刷新",
            null => "获取失败",
            var code => $"获取失败（{code}）",
        };
    }
}
