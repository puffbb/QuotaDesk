namespace QuotaDesk.Services;

/// <summary>任务栏的停靠方式（决定悬停详情卡朝哪一侧展开）。</summary>
public enum TaskbarDockMode
{
    /// <summary>任务栏在屏幕底部：挂件贴窗口下缘，卡片向上展开。</summary>
    Bottom,
    /// <summary>任务栏在屏幕顶部：挂件贴窗口上缘，卡片向下展开。</summary>
    Top,
    /// <summary>无法判定（浮动/异常矩形）：调用方不展示卡片。</summary>
    Unknown,
}

/// <summary>
/// 任务栏挂件定位的纯计算（不依赖 WPF/Win32，可单元测试）。
/// 输入一律为 DIP（调用方已把物理像素乘 scale 换算）。
/// 语义：挂件贴任务栏内缘、右缘对齐目标位置（用户偏移或"^"按钮左侧），
/// 且必须完整落在任务栏矩形内——分辨率/DPI 变化后旧偏移会在这里被收敛，而不是消失。
/// </summary>
public static class TaskbarWidgetLayout
{
    /// <summary>挂件与任务栏边缘的最小间隙（DIP）。</summary>
    public const double EdgeGap = 2.0;
    /// <summary>自动吸附时挂件右缘与"^"按钮左缘的间隙（DIP）。</summary>
    public const double ChevronGap = 4.0;

    /// <summary>该任务栏矩形是否可支持：仅水平横条（宽 ≥ 高）。竖直左右边栏不放置（挂件隐藏，宁缺勿错位）。</summary>
    public static bool IsSupportedOrientation(int trayLeft, int trayTop, int trayRight, int trayBottom)
    {
        var w = (long)trayRight - trayLeft;
        var h = (long)trayBottom - trayTop;
        return w > 0 && h > 0 && w >= h;
    }

    /// <summary>
    /// 挂件左缘 X（DIP）。desiredRightDip 为期望右缘；结果 clamp 到
    /// [trayLeftDip+EdgeGap, trayRightDip−EdgeGap−width]，任务栏放不下时贴左缘。
    /// </summary>
    public static double ComputeLeftDip(double trayLeftDip, double trayRightDip, double widthDip,
        double desiredRightDip)
    {
        var minLeft = trayLeftDip + EdgeGap;
        var maxLeft = Math.Max(minLeft, trayRightDip - EdgeGap - widthDip);
        return Math.Min(Math.Max(desiredRightDip - widthDip, minLeft), maxLeft);
    }

    /// <summary>
    /// 挂件右缘的目标位置：用户拖过 → 任务栏右缘 − 记忆偏移（先收敛到当前布局有效范围）；
    /// 未拖过 → "^"按钮左缘 − 间隙；找不到"^" → 右缘 − fallbackOffsetDip。
    /// </summary>
    public static double ComputeDesiredRightDip(double trayLeftDip, double trayRightDip,
        double widthDip, double? savedOffsetRightDip, double? chevronLeftDip, double fallbackOffsetDip)
    {
        if (savedOffsetRightDip is { } offset)
        {
            var sanitized = SanitizeOffsetDip(offset, trayRightDip - trayLeftDip, widthDip);
            return trayRightDip - sanitized;
        }
        if (chevronLeftDip is { } chevron) return chevron - ChevronGap;
        return trayRightDip - fallbackOffsetDip;
    }

    /// <summary>
    /// 把用户记忆的右缘偏移收敛到当前布局的有效范围 [0, trayWidth−width−2×EdgeGap]。
    /// 只影响本次计算，不改写保存的值（设置不被静默清除，恢复原分辨率后原偏移依然生效）。
    /// </summary>
    public static double SanitizeOffsetDip(double offsetDip, double trayWidthDip, double widthDip)
    {
        var max = Math.Max(0, trayWidthDip - widthDip - 2 * EdgeGap);
        return Math.Clamp(offsetDip, 0, max);
    }

    /// <summary>
    /// 挂件顶边 Y（DIP）。底靠任务栏：贴矩形下缘上方；顶靠任务栏（矩形位于屏幕顶部）：贴矩形上缘下方。
    /// 返回 null = 无法可靠判断（如竖直边栏、矩形横跨屏幕中部），调用方应隐藏而不是猜。
    /// </summary>
    public static double? ComputeTopDip(double trayTopDip, double trayBottomDip,
        double screenTopDip, double screenBottomDip, double heightDip)
    {
        const double slack = 8.0; // 判定"任务栏贴屏幕上/下缘"的容差（DIP）
        var atBottom = Math.Abs(trayBottomDip - screenBottomDip) <= slack;
        var atTop = Math.Abs(trayTopDip - screenTopDip) <= slack;
        if (atBottom) return trayBottomDip - EdgeGap - heightDip;
        if (atTop) return trayTopDip + EdgeGap;
        return null;
    }

    /// <summary>
    /// 任务栏停靠方式（与 ComputeTopDip 同一容差判定）：详情卡朝屏幕内侧展开的方向依据。
    /// 底靠 → Bottom；顶靠 → Top；其余（浮动/异常）→ Unknown，调用方不展示卡片。
    /// </summary>
    public static TaskbarDockMode ComputeDockMode(double trayTopDip, double trayBottomDip,
        double screenTopDip, double screenBottomDip)
    {
        const double slack = 8.0;
        var atBottom = Math.Abs(trayBottomDip - screenBottomDip) <= slack;
        var atTop = Math.Abs(trayTopDip - screenTopDip) <= slack;
        if (atBottom) return TaskbarDockMode.Bottom;
        if (atTop) return TaskbarDockMode.Top;
        return TaskbarDockMode.Unknown;
    }

    // ---------- 悬停详情卡的高度预算 ----------

    /// <summary>挂件本体占用的窗口下段高度（DIP）：挂件 36 + 边距 2，卡片底缘留 4 间隙。</summary>
    public const double WidgetBandDip = 42.0;
    /// <summary>卡片外缘与屏幕边缘（放不下时）之间的安全间隙（DIP）。</summary>
    public const double PopupScreenGapDip = 8.0;
    /// <summary>卡片可用高度下限：再小也给滚动条留出可用的可视区（DIP）。</summary>
    public const double MinPopupHeightDip = 140.0;

    /// <summary>
    /// 卡片的最大可用高度：从卡片靠屏幕外那一侧的边缘到屏幕边缘的距离，扣掉安全间隙，
    /// 再保底 MinPopupHeightDip（空间不足时由卡片内部滚动承担，而不是把内容裁掉）。
    /// bottomDock=true（底靠任务栏）传入「卡片底缘到屏幕上缘的距离」；顶靠传「屏幕下缘到卡片顶缘的距离」。
    /// </summary>
    public static double ComputeMaxPopupHeightDip(double popupEdgeToScreenDip)
        => Math.Max(MinPopupHeightDip, popupEdgeToScreenDip - PopupScreenGapDip);

    /// <summary>容纳一张卡片所需的窗口总高（DIP）：挂件下段 + 卡片实际渲染高度。</summary>
    public static double ComputeWindowHeightForPopupDip(double popupHeightDip)
        => WidgetBandDip + Math.Max(0, popupHeightDip);
}
