using QuotaDesk.Services;
using Xunit;

namespace QuotaDesk.Tests;

/// <summary>
/// 任务栏挂件定位计算（TaskbarWidgetLayout，纯 DIP 数学）。
/// 覆盖：自动吸附、手动偏移、分辨率切换后的边界收敛、负坐标、顶靠任务栏、不支持的布局。
/// </summary>
public class TaskbarWidgetLayoutTests
{
    private const double W = 120, H = 36;

    // ---------- 方向支持 ----------

    [Theory]
    [InlineData(0, 1040, 1920, 1080, true)]   // 底部横条
    [InlineData(0, 0, 1920, 40, true)]        // 顶部横条
    [InlineData(-8, -8, -8 + 48, 1040, false)] // 竖直左边栏（宽 < 高）
    public void Orientation_rules(int l, int t, int r, int b, bool supported)
    {
        Assert.Equal(supported, TaskbarWidgetLayout.IsSupportedOrientation(l, t, r, b));
    }

    [Fact]
    public void Degenerate_rect_is_unsupported()
    {
        Assert.False(TaskbarWidgetLayout.IsSupportedOrientation(0, 0, 0, 0));
        Assert.False(TaskbarWidgetLayout.IsSupportedOrientation(0, 0, 100, 0));
    }

    // ---------- 自动吸附（以"^"按钮左缘为目标） ----------

    [Fact]
    public void Auto_snap_places_right_edge_before_chevron()
    {
        // 任务栏 0..1920，"^"按钮左缘 1700（DIP）
        var desired = TaskbarWidgetLayout.ComputeDesiredRightDip(0, 1920, W,
            savedOffsetRightDip: null, chevronLeftDip: 1700, fallbackOffsetDip: 310);
        Assert.Equal(1700 - TaskbarWidgetLayout.ChevronGap, desired);
        var left = TaskbarWidgetLayout.ComputeLeftDip(0, 1920, W, desired);
        Assert.Equal(1700 - TaskbarWidgetLayout.ChevronGap - W, left);
    }

    [Fact]
    public void Chevron_left_of_tray_start_clamps_inside()
    {
        // 异常数据：chevron 比任务栏左缘还靠左 → 结果仍须落在任务栏内。
        var desired = TaskbarWidgetLayout.ComputeDesiredRightDip(0, 1920, W,
            null, chevronLeftDip: 2, fallbackOffsetDip: 310);
        var left = TaskbarWidgetLayout.ComputeLeftDip(0, 1920, W, desired);
        Assert.Equal(TaskbarWidgetLayout.EdgeGap, left); // 收敛到左缘
    }

    // ---------- 手动偏移与分辨率切换 ----------

    [Fact]
    public void Saved_offset_is_respected_within_tray()
    {
        // 用户把挂件拖到距右缘 200 DIP 处
        var desired = TaskbarWidgetLayout.ComputeDesiredRightDip(0, 1920, W,
            savedOffsetRightDip: 200, chevronLeftDip: null, fallbackOffsetDip: 310);
        Assert.Equal(1920 - 200, desired);
    }

    [Fact]
    public void Saved_offset_is_clamped_after_resolution_shrink()
    {
        // 3840 宽屏上保存了 600 的偏移；切到 1920 后若照搬：左缘 = 1920-600-120 = 1200，仍在界内——原样尊重。
        var desired = TaskbarWidgetLayout.ComputeDesiredRightDip(0, 1920, W,
            savedOffsetRightDip: 600, null, 310);
        var left = TaskbarWidgetLayout.ComputeLeftDip(0, 1920, W, desired);
        Assert.Equal(1920 - 600 - W, left);

        // 切到 800 宽：700 的偏移已放不下 → 收敛到任务栏内，挂件完整可见（不被静默清除，仅本次显示收敛）。
        desired = TaskbarWidgetLayout.ComputeDesiredRightDip(0, 800, W,
            savedOffsetRightDip: 700, null, 310);
        left = TaskbarWidgetLayout.ComputeLeftDip(0, 800, W, desired);
        Assert.InRange(left, TaskbarWidgetLayout.EdgeGap, 800 - TaskbarWidgetLayout.EdgeGap - W);
        Assert.Equal(800 - TaskbarWidgetLayout.SanitizeOffsetDip(700, 800, W) - W, left);
    }

    [Fact]
    public void Offset_beyond_tray_width_clamps_to_right_edge()
    {
        // 偏移超过任务栏宽（多屏任务栏矩形变化）：不产生界外坐标，收敛到最靠右的合法位置。
        var desired = TaskbarWidgetLayout.ComputeDesiredRightDip(-1920, 0, W,
            savedOffsetRightDip: 5000, null, 310);
        var left = TaskbarWidgetLayout.ComputeLeftDip(-1920, 0, W, desired);
        Assert.InRange(left, -1920 + TaskbarWidgetLayout.EdgeGap, 0 - TaskbarWidgetLayout.EdgeGap - W);
        Assert.Equal(-1920 + 2 * TaskbarWidgetLayout.EdgeGap, left); // 收敛到最靠右的合法位置（偏移被截到任务栏宽内）
    }

    [Fact]
    public void Negative_coordinates_are_supported()
    {
        // 副屏在主屏左侧（x 为负）。
        var left = TaskbarWidgetLayout.ComputeLeftDip(-3840, -1920, W, -2000);
        Assert.Equal(-2000 - W, left);
    }

    [Fact]
    public void Widget_wider_than_tray_pins_to_left_edge()
    {
        var left = TaskbarWidgetLayout.ComputeLeftDip(0, 100, W, 90);
        Assert.Equal(TaskbarWidgetLayout.EdgeGap, left); // 放不下：贴左缘，不出界、不翻转为负宽
    }

    // ---------- 顶边 ----------

    [Fact]
    public void Bottom_docked_taskbar_places_widget_above()
    {
        // 屏幕 0..1080，任务栏占 1040..1080
        var top = TaskbarWidgetLayout.ComputeTopDip(1040, 1080, 0, 1080, H);
        Assert.Equal(1080 - TaskbarWidgetLayout.EdgeGap - H, top);
    }

    [Fact]
    public void Top_docked_taskbar_places_widget_below()
    {
        var top = TaskbarWidgetLayout.ComputeTopDip(0, 40, 0, 1080, H);
        Assert.Equal(TaskbarWidgetLayout.EdgeGap, top);
    }

    [Fact]
    public void Undockable_rect_returns_null_instead_of_guessing()
    {
        // 矩形横跨屏幕中部（异常状态）：不猜位置。
        var top = TaskbarWidgetLayout.ComputeTopDip(300, 500, 0, 1080, H);
        Assert.Null(top);
    }

    // ---------- 偏移收敛不改变保存值 ----------

    [Fact]
    public void Sanitize_is_pure_and_does_not_amplify()
    {
        Assert.Equal(0, TaskbarWidgetLayout.SanitizeOffsetDip(-50, 1920, W));
        Assert.Equal(1920 - W - 2 * TaskbarWidgetLayout.EdgeGap,
            TaskbarWidgetLayout.SanitizeOffsetDip(9999, 1920, W));
        Assert.Equal(200, TaskbarWidgetLayout.SanitizeOffsetDip(200, 1920, W));
    }

    // ---------- 停靠方式（详情卡展开方向） ----------

    [Theory]
    [InlineData(1040, 1080, 0, 1080, TaskbarDockMode.Bottom)]  // 底部任务栏
    [InlineData(0, 40, 0, 1080, TaskbarDockMode.Top)]          // 顶部任务栏
    [InlineData(300, 348, 0, 1080, TaskbarDockMode.Unknown)]   // 浮动/异常矩形：不猜
    public void Dock_mode_follows_taskbar_edge(double trayTop, double trayBottom,
        double screenTop, double screenBottom, TaskbarDockMode mode)
    {
        Assert.Equal(mode, TaskbarWidgetLayout.ComputeDockMode(trayTop, trayBottom, screenTop, screenBottom));
    }

    [Fact]
    public void Dock_mode_tolerates_small_slack()
    {
        // 与 ComputeTopDip 相同的 8 DIP 容差：任务栏与屏幕边缘差几个像素仍判定为停靠。
        Assert.Equal(TaskbarDockMode.Bottom, TaskbarWidgetLayout.ComputeDockMode(1044, 1080, 0, 1080));
        Assert.Equal(TaskbarDockMode.Top, TaskbarWidgetLayout.ComputeDockMode(0, 40, 3, 1080));
    }

    // ---------- 悬停详情卡高度预算 ----------

    [Fact]
    public void Popup_height_subtracts_screen_gap()
    {
        // 卡片底缘距屏幕上缘 900 DIP → 可用 900 - 8 = 892。
        Assert.Equal(892, TaskbarWidgetLayout.ComputeMaxPopupHeightDip(900));
    }

    [Fact]
    public void Popup_height_has_floor_for_scroll()
    {
        // 极小屏幕：可用高度不足时不裁到不可用，保底可滚动可视区。
        Assert.Equal(TaskbarWidgetLayout.MinPopupHeightDip, TaskbarWidgetLayout.ComputeMaxPopupHeightDip(50));
        Assert.True(TaskbarWidgetLayout.ComputeMaxPopupHeightDip(0) >= TaskbarWidgetLayout.MinPopupHeightDip);
    }

    [Fact]
    public void Window_height_for_popup_adds_widget_band()
    {
        Assert.Equal(TaskbarWidgetLayout.WidgetBandDip + 220,
            TaskbarWidgetLayout.ComputeWindowHeightForPopupDip(220));
        // 负值防御：不产生比常驻高度更小的窗口。
        Assert.Equal(TaskbarWidgetLayout.WidgetBandDip, TaskbarWidgetLayout.ComputeWindowHeightForPopupDip(-5));
    }

    [Fact]
    public void Small_screen_popup_uses_scroll_and_steady_window()
    {
        // 小屏：卡片底缘距屏幕上缘只剩 162 → 卡片限高 154（滚动承担），窗口需要 196。
        // EnsureWindowFitsPopup 只长不缩：196 < 常驻 204 → 窗口保持 204，卡片仍完整可见（有滚动）。
        var avail = TaskbarWidgetLayout.ComputeMaxPopupHeightDip(162);
        Assert.Equal(154, avail);
        Assert.Equal(TaskbarWidgetLayout.WidgetBandDip + 154,
            TaskbarWidgetLayout.ComputeWindowHeightForPopupDip(avail));
    }
}
