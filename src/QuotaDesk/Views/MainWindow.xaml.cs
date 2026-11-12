using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using QuotaDesk.Models;
using QuotaDesk.Services;
using QuotaDesk.ViewModels;
using Application = System.Windows.Application;

namespace QuotaDesk.Views;

/// <summary>
/// 悬浮窗主界面：精简、详细与侧边栏三态；点击内容展开或收起，拖动内容移动；
/// 位置、模式等偏好持久化；恢复位置时确保落在可见屏幕内。
/// </summary>
public partial class MainWindow : Window
{
    private const double CompactWidth = 248;
    private const double DetailedWidth = 280;
    private const double SidebarWidth = 66;
    private string _returnMode = "compact";

    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly DispatcherTimer _secondTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _screenTimer;
    private readonly string? _selfTestPath;
    private readonly int? _exitAfterSeconds;
    private bool _loaded;

    public MainWindow(MainViewModel vm, AppSettings settings, string settingsPath, string[] args)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        _settingsPath = settingsPath;
        _selfTestPath = ArgsValue(args, "--selftest");
        _exitAfterSeconds = int.TryParse(ArgsValue(args, "--exit-after"), out var s) ? s : null;

        DataContext = _vm;
        Topmost = _settings.Topmost;
        ApplyMode(save: false);

        _secondTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _secondTimer.Tick += async (_, _) => await SafeTickAsync();
        _secondTimer.Start();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(30, _settings.RefreshSeconds)) };
        _refreshTimer.Tick += async (_, _) => await RefreshSafeAsync(manual: false);
        _refreshTimer.Start();

        _screenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _screenTimer.Tick += (_, _) => EnsureOnScreen();
        _screenTimer.Start();

        Loaded += async (_, _) =>
        {
            _loaded = true;
            await RefreshSafeAsync(manual: false);
            if (_exitAfterSeconds is { } secs)
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
                t.Tick += (_, _) => Application.Current.Shutdown();
                t.Start();
            }
        };

        LocationChanged += (_, _) => { if (_loaded) ScheduleSavePosition(); };
    }

    private static string? ArgsValue(string[] args, string name)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(name + "=", StringComparison.Ordinal)) return a[(name.Length + 1)..];
        }
        return null;
    }

    /// <summary>设置窗口调整刷新间隔时使用。</summary>
    public System.Windows.Threading.DispatcherTimer RefreshTimer => _refreshTimer;

    private async Task SafeTickAsync()
    {
        try { await _vm.TickSecondAsync(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async Task RefreshSafeAsync(bool manual)
    {
        try { await _vm.RefreshAsync(manual); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    /// <summary>紧凑/详细/微缩三态切换。微缩栏按 DockEdge 吸附屏幕边缘。</summary>
    public void ApplyMode(bool save = true)
    {
        CompactPanel.Visibility = _settings.UiMode == "compact" ? Visibility.Visible : Visibility.Collapsed;
        MiniPanel.Visibility = _settings.UiMode == "mini" ? Visibility.Visible : Visibility.Collapsed;
        DetailedPanel.Visibility = _settings.UiMode == "detailed" ? Visibility.Visible : Visibility.Collapsed;
        Width = _settings.UiMode switch
        {
            "detailed" => DetailedWidth,
            "mini" => SidebarWidth,
            _ => CompactWidth,
        };
        Root.CornerRadius = _settings.UiMode == "detailed" ? new CornerRadius(18) : new CornerRadius(13);
        if (_settings.UiMode == "mini" && _settings.DockEdge == "right") Root.CornerRadius = new CornerRadius(13, 4, 4, 13);
        if (_settings.UiMode == "mini" && _settings.DockEdge == "left") Root.CornerRadius = new CornerRadius(4, 13, 13, 4);
        Glow.CornerRadius = Root.CornerRadius;
        if (_loaded) { UpdateLayout(); EnsureOnScreen(); }
        if (save) SaveSettings();
        if (_settings.UiMode == "mini") ApplyDock();
    }

    /// <summary>微缩栏按停靠边贴边定位（浮动则不动）。</summary>
    public void ApplyDock()
    {
        if (_settings.UiMode != "mini") return;
        var wa = SystemParameters.WorkArea;
        UpdateLayout();
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : 68;
        switch (_settings.DockEdge)
        {
            case "top":
                Left = wa.Left + (wa.Width - w) / 2;
                Top = wa.Top;
                break;
            case "bottom":
                Left = wa.Left + (wa.Width - w) / 2;
                Top = wa.Bottom - h;
                break;
            case "left":
                Left = wa.Left;
                Top = wa.Top + (wa.Height - h) / 2;
                break;
            case "right":
                Left = wa.Right - w;
                Top = wa.Top + (wa.Height - h) / 2;
                break;
        }
    }

    /// <summary>微缩栏拖放结束后，靠近屏幕边缘 24 DIP 内自动吸附。</summary>
    private void SnapToEdgeIfNeeded()
    {
        if (_settings.UiMode != "mini") return;
        var wa = SystemParameters.WorkArea;
        const double threshold = 24;
        string edge =
            Top - wa.Top < threshold ? "top" :
            wa.Bottom - (Top + ActualHeight) < threshold ? "bottom" :
            Left - wa.Left < threshold ? "left" :
            wa.Right - (Left + ActualWidth) < threshold ? "right" : "none";
        if (_settings.DockEdge != edge)
        {
            _settings.DockEdge = edge;
            ApplyDock();
        }
    }

    public void SetTopmost(bool value)
    {
        Topmost = value;
    }

    public void SaveSettings()
    {
        _settings.WindowX = Left;
        _settings.WindowY = Top;
        try { _settings.Save(_settingsPath); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>拖动过程中不写盘，停止 500ms 后保存一次。</summary>
    private void ScheduleSavePosition()
    {
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>点击=切换模式；移动=拖动。锁定时仅允许点击切换。</summary>
    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Template 内的文字/图标也属于按钮，不能误触发拖动或模式切换。
        for (var element = e.OriginalSource as DependencyObject; element is not null;)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase) return;
            element = element is Visual || element is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        e.Handled = true;
        var beforeLeft = Left;
        var beforeTop = Top;

        if (!_settings.LockPosition)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
        }

        var moved = Math.Abs(Left - beforeLeft) > 1.5 || Math.Abs(Top - beforeTop) > 1.5;
        if (moved)
        {
            EnsureOnScreen();
            SnapToEdgeIfNeeded();
            SaveSettings();
        }
        else
        {
            if (_settings.UiMode == "detailed") CollapseButton_Click(sender, e);
            else ExpandButton_Click(sender, e);
        }
    }

    private void RatioButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowRemaining = !_settings.ShowRemaining;
        _vm.OnSettingsChanged();
        SaveSettings();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    public event EventHandler? HideToTrayRequested;

    /// <summary>关闭按钮=隐藏到托盘；首次解释该行为。真正退出走托盘菜单。</summary>
    public void HideToTray()
    {
        Hide();
        HideToTrayRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshSafeAsync(manual: true);
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.UiMode != "detailed") _returnMode = _settings.UiMode;
        _settings.UiMode = "detailed";
        ApplyMode();
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.UiMode = _returnMode;
        ApplyMode();
    }

    public event EventHandler? SettingsRequested;
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>恢复或检查窗口位置：必须落在虚拟屏幕内（多屏、副屏断开均可恢复）。</summary>
    public void EnsureOnScreen()
    {
        // 停靠中的微缩栏不参与自由位置修正，直接重新吸附。
        if (_settings.UiMode == "mini" && _settings.DockEdge != "none")
        {
            ApplyDock();
            return;
        }
        var vsLeft = SystemParameters.VirtualScreenLeft;
        var vsTop = SystemParameters.VirtualScreenTop;
        var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

        var w = ActualWidth <= 0 ? Width : ActualWidth;
        var h = ActualHeight <= 0 ? Height : ActualHeight;

        double left = Left, top = Top;
        if (double.IsNaN(left) || double.IsNaN(top) ||
            left + w < vsLeft + 10 || left > vsRight - 10 ||
            top + h < vsTop + 10 || top > vsBottom - 10)
        {
            // 完全不可见：放回主工作区右上角。
            left = SystemParameters.WorkArea.Right - w - 24;
            top = SystemParameters.WorkArea.Top + 24;
        }
        else
        {
            left = Math.Min(Math.Max(left, vsLeft), vsRight - w);
            top = Math.Min(Math.Max(top, vsTop), vsBottom - h);
        }

        if (left != Left || top != Top)
        {
            Left = left;
            Top = top;
            SaveSettings();
        }
    }

    /// <summary>恢复保存的位置（在显示前调用）。</summary>
    public void RestorePosition()
    {
        if (_settings.WindowX is { } x && _settings.WindowY is { } y)
        {
            Left = x;
            Top = y;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Top + 24;
        }
    }

    /// <summary>自检：输出 DPI、窗口尺寸、四行额度文本，供自动化验证；不写任何敏感数据。</summary>
    public async Task WriteSelfTestAsync()
    {
        if (_selfTestPath is null) return;
        // 等首刷完成（真实来源经代理最多等 15 秒）。
        for (var i = 0; i < 150 && _vm.Providers.Any(p => p.Source is null); i++)
            await Task.Delay(100);

        var dpi = VisualTreeHelper.GetDpi(this);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var lines = new List<string>
        {
            $"hwnd={hwnd.ToInt64()}",
            $"dpiScale={dpi.DpiScaleX:F2}",
            $"window={ActualWidth:F1}x{ActualHeight:F1}",
            $"windowLeft={Left:F0} windowTop={Top:F0}",
            $"virtualScreen={SystemParameters.VirtualScreenWidth:F0}x{SystemParameters.VirtualScreenHeight:F0}",
            $"mode={_settings.UiMode}",
            $"theme={_settings.Theme}",
            $"themeDecision={QuotaDesk.Services.ThemeManager.LastDecision ?? "not-applied"}",
            $"themeBg={(TryFindResource("BgRoot") as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "missing"}",
            $"themeWidgetText={(TryFindResource("WidgetText") as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "missing"}",
            $"ratio={(_settings.ShowRemaining ? "remaining" : "used")}",
            $"topmost={Topmost}",
        };
        foreach (var p in _vm.Providers)
        {
            var rowText = string.Join(" | ", p.Rows.Select(r =>
                $"{r.Label}={r.PercentText}({r.Severity}){(r.StatusText is null ? "" : $"[{r.StatusText}]")}" +
                (r.RightText is null ? "" : $"<{r.RightText}>")));
            lines.Add($"provider={p.ProviderId} source={p.Source} scenario={p.Scenario} status=\"{p.StatusText}\" error=\"{p.ErrorText}\" rows: {rowText}");
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_selfTestPath)!);
            await File.WriteAllLinesAsync(_selfTestPath, lines);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
