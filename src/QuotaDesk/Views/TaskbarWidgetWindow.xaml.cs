using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuotaDesk.Controls;
using QuotaDesk.Models;
using QuotaDesk.Services;
using QuotaDesk.ViewModels;

namespace QuotaDesk.Views;

/// <summary>
/// 任务栏额度挂件（T2 双行）：一个无激活、置顶的透明窗口覆盖在任务栏通知区左侧空位上
/// （TrafficMonitor 同思路；Windows 没有在任务栏内放第三方控件的官方 API）。
/// 职责：贴住"显示隐藏的图标"左侧、悬停浮出详情卡、左键打开主窗口、右键菜单、
/// 任务栏范围内横向拖动并记忆位置、全屏/自动隐藏任务栏时让位。
/// 悬停详情卡：使用独立弹层，不改变挂件窗口尺寸（顶靠任务栏向下展开、底靠向上展开），
/// 放不下时卡片内部滚动，嵌套 Tooltip 已移除（卡内状态行/行状态/错误均已内联展示）。
/// 显示环境变化（分辨率/DPI/任务栏重建）：监听 WM_DISPLAYCHANGE、WM_DPICHANGED、
/// WM_SETTINGCHANGE 与 TaskbarCreated 广播，作废坐标缓存并去抖重定位；3 秒轮询只是兜底。
/// </summary>
public partial class TaskbarWidgetWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>"^"按钮找不到时的重试间隔（秒）：避免每次轮询都做 UIA 查询。</summary>
    private const double ChevronRetrySeconds = 30;

    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly MainWindow _main;
    private readonly Action _openSettings;
    private readonly Action _hideSelf;
    private readonly string _settingsPath;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _popShowTimer;
    private readonly DispatcherTimer _popHideTimer;
    private readonly DispatcherTimer _layoutResetTimer;
    private bool _layoutResetPending;
    private bool _disposed;

    /// <summary>1/255 透明底：肉眼不可见，但让分层窗口的逐像素命中测试覆盖整个挂件区域。</summary>
    private static readonly Brush HitTestBackground = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

    private double _scale = 1.0;          // 设备像素 → DIP（按任务栏所在显示器的 DPI 计算，96/dpi）
    private double? _chevronLeftDip;      // "^"按钮左缘（DIP）；显示布局变化或任务栏变化时作废
    private IntPtr _chevronHwnd;          // 缓存所属的任务栏窗口（Explorer 重建后 hwnd 会变）
    private RECT _chevronTrayRect;        // 缓存所属的任务栏矩形
    private DateTimeOffset _chevronLastAttempt = DateTimeOffset.MinValue; // 失败重试节流
    private HwndSource? _source;
    private int _taskbarCreatedMsg = -1;
    private bool _dragging;
    private Point _dragStart;
    private double _dragStartLeft;
    private TaskbarDockMode _dockMode = TaskbarDockMode.Bottom; // 详情卡展开方向（随任务栏停靠方式）
    private RECT? _lastTrayRect;                                // 最近一次轮询的任务栏矩形（收起卡片时复位窗口用）

    public TaskbarWidgetWindow(MainViewModel vm, AppSettings settings, MainWindow main,
        Action openSettings, Action hideSelf, string settingsPath)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        _main = main;
        _openSettings = openSettings;
        _hideSelf = hideSelf;
        _settingsPath = settingsPath;
        DataContext = vm;

        HookProviders();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pollTimer.Tick += (_, _) => Poll();

        _popShowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _popShowTimer.Tick += (_, _) => { _popShowTimer.Stop(); if (Widget.IsMouseOver) ShowPopup(); };
        _popHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _popHideTimer.Tick += (_, _) =>
        {
            _popHideTimer.Stop();
            if (!IsCursorInsideHoverArea()) CollapsePopup();
        };

        // 显示环境变化后的去抖重定位：150ms 内的连续消息合并为一次定位。
        _layoutResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _layoutResetTimer.Tick += (_, _) =>
        {
            _layoutResetTimer.Stop();
            _layoutResetPending = false;
            Poll();
        };

        Loaded += OnLoaded;
        Closed += OnWidgetClosed;

        // 「恢复自动吸附」只在有手动偏移时有意义。
        if (Widget.ContextMenu is { } menu) menu.Opened += (_, _) =>
            ResetAlignItem.IsEnabled = _settings.TaskbarOffsetRight is not null;
    }

    // ---------- 数据行更新 ----------

    private void HookProviders()
    {
        _vm.Providers.CollectionChanged += OnProvidersChanged;
        HookRows();
    }

    private void OnProvidersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        HookRows();
    }

    private void HookRows()
    {
        foreach (var p in _vm.Providers)
            foreach (var row in p.Rows)
            {
                row.PropertyChanged -= RowChanged;
                row.PropertyChanged += RowChanged;
            }
        UpdateLines();
    }

    private void RowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateLines();

    private void UpdateLines()
    {
        SetLine(GlmText, _vm.Find("glm"));
        SetLine(CodexText, _vm.Find("codex"));
    }

    private void SetLine(TextBlock target, ProviderViewModel? p)
    {
        if (p is null || p.Rows.Count < 2) return;
        target.Inlines.Clear();
        target.Inlines.Add(new Run(TaskbarText.CompactPercent(p.Rows[0].PercentText))
        {
            Foreground = BrushOf(p.Rows[0].Severity),
        });
        target.Inlines.Add(new Run("/") { Foreground = ThemeBrush.Find("WidgetDim"), FontWeight = FontWeights.Normal });
        target.Inlines.Add(new Run(TaskbarText.CompactPercent(p.Rows[1].PercentText))
        {
            Foreground = BrushOf(p.Rows[1].Severity),
        });
        // 紧凑状态标记：旧数据/失败/模拟。不依赖颜色，深浅色主题都可读。
        var marker = TaskbarText.StateMarker(p.IsDemo, p.IsStale,
            p.Rows.Any(r => r.HasData), p.IsAwaitingFirstData);
        if (marker is not null)
        {
            target.Inlines.Add(new Run(" " + marker)
            {
                Foreground = ThemeBrush.Find("WidgetDim"),
                FontSize = 9.5,
            });
        }
        // 悬停提示：来源、最近成功时间与失败原因（错误码 → 可理解文本，不含凭据）。
        // 详情卡已显示来源和错误，不再弹出重复的系统提示框。
        target.ToolTip = null;
    }

    private Brush BrushOf(Severity severity) => severity switch
    {
        Severity.Warn => ThemeBrush.Find("WidgetWarn"),
        Severity.Critical => ThemeBrush.Find("WidgetCritical"),
        _ => ThemeBrush.Find("WidgetText"),
    };

    // ---------- 悬停详情卡 ----------

    private void Widget_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _popHideTimer.Stop();
        // 悬停底色随任务栏深浅切换（藏蓝/白），单例画刷改色即生效。
        Widget.Background = ThemeBrush.Find("WidgetHoverBg");
        if (Popup.IsOpen) return;
        _popShowTimer.Stop();
        _popShowTimer.Start();
    }

    private void Widget_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        Widget.Background = HitTestBackground;
        _popShowTimer.Stop();
        if (!Popup.IsOpen) return;
        _popHideTimer.Stop();
        _popHideTimer.Start();
    }

    /// <summary>挂件、弹层与两者之间的窄间隙均属于悬停区域。</summary>
    private bool IsCursorInsideHoverArea()
    {
        if (!GetCursorPos(out var point)) return false;
        try
        {
            var widget = ScreenBounds(Widget);
            Rect? card = Popup.IsOpen && CardSurface.IsVisible ? ScreenBounds(CardSurface) : null;
            return HoverHitTest.Contains(widget, card, new Point(point.X, point.Y));
        }
        catch (InvalidOperationException) { return false; }
    }

    private static Rect ScreenBounds(FrameworkElement element)
    {
        var a = element.PointToScreen(new Point(0, 0));
        var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(a, b);
    }

    private void ShowPopup()
    {
        if (Popup.IsOpen || !IsVisible) return;
        if (GetTrayRect() is { } tray)
        {
            _lastTrayRect = tray;
            UpdateDockMode(tray);
            ApplyDockMode();
        }
        if (_dockMode == TaskbarDockMode.Unknown) return;
        CardSurface.MaxHeight = ComputeAvailablePopupHeight();
        Popup.HorizontalOffset = Widget.ActualWidth - CardSurface.Width;
        Popup.IsOpen = true;
    }

    private void CollapsePopup()
    {
        _popHideTimer.Stop();
        Popup.IsOpen = false;
    }

    /// <summary>
    /// 卡片可用最大高度（DIP）：按停靠方向从卡片外缘量到所在显示器的屏幕边缘。
    /// 取不到显示器矩形时退回保守值（小屏也可用，超长内容走卡片内滚动）。
    /// </summary>
    private double ComputeAvailablePopupHeight()
    {
        var tray = _lastTrayRect;
        var trayHwnd = GetTrayHwnd();
        var monitor = trayHwnd != IntPtr.Zero ? GetMonitorRect(trayHwnd) : null;
        if (tray is { } t && monitor is { } m)
        {
            if (_dockMode == TaskbarDockMode.Bottom)
            {
                var windowBottom = t.Bottom * _scale - TaskbarWidgetLayout.EdgeGap;
                var popupBottom = windowBottom - TaskbarWidgetLayout.WidgetBandDip;
                return TaskbarWidgetLayout.ComputeMaxPopupHeightDip(popupBottom - m.Top * _scale);
            }
            var windowTop = t.Top * _scale + TaskbarWidgetLayout.EdgeGap;
            var popupTop = windowTop + TaskbarWidgetLayout.WidgetBandDip;
            return TaskbarWidgetLayout.ComputeMaxPopupHeightDip(m.Bottom * _scale - popupTop);
        }
        return TaskbarWidgetLayout.ComputeMaxPopupHeightDip(480);
    }

    /// <summary>按任务栏矩形刷新停靠方式判定；取不到显示器矩形时保持原判定（Reposition 的兜底按底靠处理）。</summary>
    private void UpdateDockMode(RECT tray)
    {
        var trayHwnd = GetTrayHwnd();
        var monitor = trayHwnd != IntPtr.Zero ? GetMonitorRect(trayHwnd) : null;
        if (monitor is { } m)
        {
            _dockMode = TaskbarWidgetLayout.ComputeDockMode(
                tray.Top * _scale, tray.Bottom * _scale, m.Top * _scale, m.Bottom * _scale);
        }
    }

    /// <summary>停靠方式变化时翻转窗口内部布局：挂件贴靠侧、卡片朝屏幕内侧、右键菜单弹出方向。</summary>
    private void ApplyDockMode()
    {
        var topDock = _dockMode == TaskbarDockMode.Top;
        var widgetAlign = topDock ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        var widgetMargin = topDock ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 0, 2);
        if (Widget.VerticalAlignment != widgetAlign)
        {
            Widget.VerticalAlignment = widgetAlign;
            Widget.Margin = widgetMargin;
        }
        Popup.Placement = topDock ? System.Windows.Controls.Primitives.PlacementMode.Bottom
                                  : System.Windows.Controls.Primitives.PlacementMode.Top;
        if (Widget.ContextMenu is { } menu)
            menu.Placement = topDock ? System.Windows.Controls.Primitives.PlacementMode.Bottom
                                     : System.Windows.Controls.Primitives.PlacementMode.Top;
    }

    private void Popup_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _popHideTimer.Stop();
    }

    private void Popup_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _popHideTimer.Stop();
        _popHideTimer.Start();
    }

    // ---------- 定位与状态轮询 ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            _source = source;
            if (source.CompositionTarget is { } target)
                _scale = target.TransformFromDevice.M11;
            source.AddHook(WndProc);
        }
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated"); // Explorer/任务栏重建广播
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Poll();
        _pollTimer.Start();

        // UI 自动化验证钩子（默认关闭）：设置环境变量 QUOTADESK_TEST_POPUP=1 后，
        // 挂件出现 1.5 秒即程序化弹出详情卡。本会话环境无法把注入的鼠标移动送达 WPF 窗口
        // （云桌面输入管线限制，真实鼠标不受影响），自动化验证借助此钩子复现悬停效果。
        if (Environment.GetEnvironmentVariable("QUOTADESK_TEST_POPUP") == "1")
        {
            var hook = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            hook.Tick += (_, _) =>
            {
                hook.Stop();
                ShowPopup();
            };
            hook.Start();
        }
    }

    /// <summary>显示环境变化消息：分辨率切换、DPI/缩放变化、系统设置变化、任务栏重建。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED || msg == WM_SETTINGCHANGE
            || (_taskbarCreatedMsg > 0 && msg == _taskbarCreatedMsg))
        {
            OnDisplayEnvironmentChanged();
        }
        return IntPtr.Zero;
    }

    /// <summary>作废坐标缓存并去抖重定位。高频消息被合并；不写日志避免噪音。</summary>
    private void OnDisplayEnvironmentChanged()
    {
        CollapsePopup();
        _chevronLeftDip = null;
        _chevronLastAttempt = DateTimeOffset.MinValue;
        if (_layoutResetPending) return;
        _layoutResetPending = true;
        _layoutResetTimer.Stop();
        _layoutResetTimer.Start();
    }

    /// <summary>3 秒兜底轮询：漏掉的事件、任务栏移动/分辨率变化 → 跟随；全屏或自动隐藏任务栏 → 让位。</summary>
    private void Poll()
    {
        if (_disposed) return;
        try
        {
            var tray = GetTrayRect();
            if (tray is null
                || !TaskbarWidgetLayout.IsSupportedOrientation(tray.Value.Left, tray.Value.Top, tray.Value.Right, tray.Value.Bottom)
                || IsTaskbarAutoHidden() || IsFullscreenBlocked())
            {
                CollapsePopup();
                if (IsVisible) Hide();
                return;
            }
            UpdateScale();
            TrackChevronCache(tray.Value);
            if (!IsVisible) Show();
            Reposition(tray.Value);
            // 兜底：卡片仍开着但光标已不在本窗口（鼠标跳变/远程会话重连错过 MouseLeave）→ 收起。
            if (Popup.IsOpen && !IsCursorInsideHoverArea())
                CollapsePopup();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    /// <summary>
    /// 设备像素 → DIP 的换算比例按"任务栏所在显示器"的 DPI 计算（而非挂件窗口自身——
    /// 多屏不同缩放或挂件恰好不在任务栏屏上时，窗口自身的变换可能对不上任务栏）。
    /// </summary>
    private void UpdateScale()
    {
        var hwnd = GetTrayHwnd();
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    _scale = 96.0 / dpi;
                    return;
                }
            }
            catch
            {
                // API 不可用（旧系统）→ 回退到窗口自身的变换。
            }
        }
        if (PresentationSource.FromVisual(this) is HwndSource source
            && source.CompositionTarget is { } target)
        {
            _scale = target.TransformFromDevice.M11;
        }
    }

    /// <summary>
    /// "^"按钮坐标缓存的失效与节流：任务栏窗口、矩形或缩放比例变了立即失效；
    /// 没找到时最多每 30 秒重试一次（UIA 查询有成本，任务栏隐藏图标按钮在部分布局里根本不存在）。
    /// </summary>
    private void TrackChevronCache(RECT tray)
    {
        var hwnd = GetTrayHwnd();
        if (_chevronLeftDip is { } &&
            (_chevronHwnd != hwnd || !_chevronTrayRect.Equals(tray) || _chevronCachedScale != _scale))
        {
            _chevronLeftDip = null; // 布局已变，旧坐标作废
        }
        if (_chevronLeftDip is null && (DateTimeOffset.Now - _chevronLastAttempt).TotalSeconds >= ChevronRetrySeconds)
        {
            _chevronLastAttempt = DateTimeOffset.Now;
            var found = FindChevronLeftDip();
            if (found is { } left)
            {
                _chevronLeftDip = left;
                _chevronHwnd = hwnd;
                _chevronTrayRect = tray;
                _chevronCachedScale = _scale;
            }
        }
    }

    private double _chevronCachedScale;

    private void Reposition(RECT tray)
    {
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        var trayLeftDip = tray.Left * _scale;
        var trayRightDip = tray.Right * _scale;
        _lastTrayRect = tray;

        // 顶边：底靠任务栏贴上缘；顶靠任务栏贴下缘；无法可靠判断（竖直边栏等）→ 不猜，隐藏。
        double? top;
        var trayHwnd = GetTrayHwnd();
        var monitor = trayHwnd != IntPtr.Zero ? GetMonitorRect(trayHwnd) : null;
        if (monitor is { } m)
        {
            var dock = TaskbarWidgetLayout.ComputeDockMode(
                tray.Top * _scale, tray.Bottom * _scale, m.Top * _scale, m.Bottom * _scale);
            if (dock != _dockMode)
            {
                _dockMode = dock;
                ApplyDockMode();
            }
            top = TaskbarWidgetLayout.ComputeTopDip(tray.Top * _scale, tray.Bottom * _scale,
                m.Top * _scale, m.Bottom * _scale, h);
        }
        else
        {
            top = tray.Bottom * _scale - TaskbarWidgetLayout.EdgeGap - h; // 取不到显示器矩形：按底靠处理
        }
        if (top is null)
        {
            CollapsePopup();
            if (IsVisible) Hide();
            return;
        }

        var desiredRight = TaskbarWidgetLayout.ComputeDesiredRightDip(trayLeftDip, trayRightDip, w,
            _settings.TaskbarOffsetRight, _chevronLeftDip, FallbackOffsetDip(tray));
        var left = TaskbarWidgetLayout.ComputeLeftDip(trayLeftDip, trayRightDip, w, desiredRight);

        if (Math.Abs(Left - left) > 0.5) Left = left;
        if (Math.Abs(Top - top.Value) > 0.5) Top = top.Value;
        if (new WindowInteropHelper(this).Handle is { } hwnd && hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    private double FallbackOffsetDip(RECT tray)
    {
        // 找不到"^"按钮时的兜底：按典型通知区宽度估算，用户仍可拖动微调。
        var widthDip = (tray.Right - tray.Left) * _scale;
        return Math.Min(310, widthDip * 0.45);
    }

    /// <summary>用 UIA 找任务栏上的"显示隐藏的图标"按钮（中英文系统都试）。调用频率由 TrackChevronCache 节流。</summary>
    private double? FindChevronLeftDip()
    {
        try
        {
            var tray = GetTrayHwnd();
            if (tray == IntPtr.Zero) return null;
            var root = AutomationElement.FromHandle(tray);
            foreach (var name in new[] { "显示隐藏的图标", "Show Hidden Icons", "显示隐藏图标" })
            {
                var button = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, name));
                if (button is null) continue;
                var rect = button.Current.BoundingRectangle;
                if (rect.IsEmpty) continue;
                return rect.X * _scale;
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return null;
    }

    private static IntPtr GetTrayHwnd() => FindWindow("Shell_TrayWnd", null);

    private static RECT? GetTrayRect()
    {
        var hwnd = GetTrayHwnd();
        if (hwnd == IntPtr.Zero) return null;
        return GetWindowRect(hwnd, out var rect) ? rect : null;
    }

    private static bool IsFullscreenBlocked()
    {
        return SHQueryUserNotificationState(out var state) == 0
            && state is QUERY_USER_NOTIFICATION_STATE.QUNS_NOT_PRESENT
                or QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
                or QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_APP
                or QUERY_USER_NOTIFICATION_STATE.QUNS_APP
                or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
    }

    private static bool IsTaskbarAutoHidden()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        return (SHAppBarMessage(4 /* ABM_GETSTATE */, ref data).ToInt64() & 0x1 /* ABS_AUTOHIDE */) != 0;
    }

    // ---------- 生命周期清理 ----------

    /// <summary>关闭时释放全部事件订阅与计时器，避免关闭/重开后旧窗口仍收到行更新。</summary>
    private void OnWidgetClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        CollapsePopup();
        _pollTimer.Stop();
        _popShowTimer.Stop();
        _popHideTimer.Stop();
        _layoutResetTimer.Stop();
        if (_source is { } source)
        {
            source.RemoveHook(WndProc);
            _source = null;
        }
        _vm.Providers.CollectionChanged -= OnProvidersChanged;
        foreach (var p in _vm.Providers)
            foreach (var row in p.Rows)
                row.PropertyChanged -= RowChanged;
    }

    // ---------- 拖动与点击 ----------

    private void Widget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _popShowTimer.Stop();
        CollapsePopup();
        _dragStart = e.GetPosition(this);
        _dragStartLeft = Left;
        _dragging = false;
        Widget.CaptureMouse();
        e.Handled = true;
    }

    private void Widget_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !Widget.IsMouseCaptured) return;
        var pos = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < 3) return;
            _dragging = true;
        }
        var tray = GetTrayRect();
        if (tray is null) return; // 任务栏暂不可得：不移动，等待恢复
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var left = _dragStartLeft + (pos.X - _dragStart.X);
        // 拖动即时约束在当前任务栏内，避免把窗口拖丢。
        Left = TaskbarWidgetLayout.ComputeLeftDip(tray.Value.Left * _scale, tray.Value.Right * _scale, w,
            left + w);
    }

    private void Widget_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!Widget.IsMouseCaptured) return;
        Widget.ReleaseMouseCapture();
        var moved = _dragging;
        _dragging = false;
        if (moved)
        {
            SaveOffset();
        }
        else
        {
            OpenMain();
        }
        e.Handled = true;
    }

    /// <summary>拖动结束后把位置记为"到任务栏右缘的偏移"，此后不再自动吸附"^"左侧。</summary>
    private void SaveOffset()
    {
        var tray = GetTrayRect();
        if (tray is null || ActualWidth <= 0) return;
        _settings.TaskbarOffsetRight = Math.Max(0, tray.Value.Right * _scale - (Left + ActualWidth));
        _chevronLeftDip = null;
        _chevronLastAttempt = DateTimeOffset.MinValue;
        SaveSettings();
        Reposition(tray.Value);
    }

    /// <summary>右键菜单「恢复自动吸附」：清除手动偏移，回到"^"左侧自动定位（用户显式操作，不静默清设置）。</summary>
    private void ResetAlign_Click(object sender, RoutedEventArgs e)
    {
        _settings.TaskbarOffsetRight = null;
        _chevronLeftDip = null;
        _chevronLastAttempt = DateTimeOffset.MinValue;
        SaveSettings();
        var tray = GetTrayRect();
        if (tray is { } rect) Reposition(rect);
    }

    private void SaveSettings()
    {
        try { _settings.Save(_settingsPath); }
        catch { /* 写盘失败不影响显示，下次修改再试 */ }
    }

    // ---------- 菜单与动作 ----------

    private void OpenMain()
    {
        _main.Show();
        _main.Activate();
        _main.EnsureOnScreen();
    }

    private async void RefreshMenu_Click(object sender, RoutedEventArgs e)
    {
        try { await _vm.RefreshAsync(manual: true); }
        catch { /* 界面已显示失败状态 */ }
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => OpenMain();

    private void SettingsMenu_Click(object sender, RoutedEventArgs e) => _openSettings();

    private void HideWidget_Click(object sender, RoutedEventArgs e) => _hideSelf();

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;

        public bool Equals(RECT other) => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private enum QUERY_USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_APP = 3,
        QUNS_ACCEPTS_NOTIFICATIONS = 4,
        QUNS_QUIET_TIME = 5,
        QUNS_APP = 6,
        QUNS_PRESENTATION_MODE = 7,
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private static RECT? GetMonitorRect(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == IntPtr.Zero) return null;
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? info.rcMonitor : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pqunsState);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
}
