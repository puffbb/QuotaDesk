namespace QuotaDesk.Services;

/// <summary>任务栏挂件的紧凑文本拼接。纯字符串逻辑，便于单元测试。</summary>
public static class TaskbarText
{
    /// <summary>"82%" → "82"；"—"、空串、解析不出数字时回退 "—"。</summary>
    public static string CompactPercent(string? percentText)
    {
        if (string.IsNullOrWhiteSpace(percentText)) return "—";
        var trimmed = percentText.Trim().TrimEnd('%');
        return trimmed.Length == 0 ? "—" : trimmed;
    }

    /// <summary>5h 与周两个窗口拼成 "82/37"；任一窗口无数据用 "—" 占位。</summary>
    public static string TwoWindows(string? fiveHour, string? week)
        => $"{CompactPercent(fiveHour)}/{CompactPercent(week)}";

    /// <summary>
    /// 任务栏数字行末的紧凑状态标记（不依赖颜色，深浅色主题都可读）：
    /// "旧" = 展示的是旧数据；"错" = 请求失败且无可显示数据；"模" = 模拟来源；null = 正常实时数据。
    /// 优先级：旧 &gt; 错 &gt; 模。
    /// </summary>
    public static string? StateMarker(bool isMock, bool isStale, bool hasData, bool isAwaitingFirstData)
    {
        if (isStale && hasData) return "旧";
        if (!hasData) return isAwaitingFirstData ? null : "错";
        if (isMock) return "模";
        return null;
    }

    /// <summary>任务栏数字行的悬停提示：平台、来源、最近成功时间与可理解的失败原因（多行）。</summary>
    public static string WidgetLineToolTip(string displayName, string? sourceLabel,
        string? updateText, string? errorText)
    {
        var head = string.IsNullOrEmpty(sourceLabel) ? displayName : $"{displayName} · {sourceLabel}";
        return string.Join("\n", new[] { head, updateText, errorText }.Where(s => !string.IsNullOrEmpty(s)));
    }
}
