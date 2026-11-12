using System.Windows;
using QuotaDesk.Models;
using QuotaDesk.Providers;
using QuotaDesk.ViewModels;
using QuotaDesk.Views;
using WinForms = System.Windows.Forms;

namespace QuotaDesk.Services;

/// <summary>
/// 托盘：左键显示/隐藏；右键菜单含刷新、模式、显示比例、置顶、锁定、演示场景、设置、退出。
/// 退出是唯一真正结束进程的入口。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly MainWindow _window;
    private readonly string _settingsPath;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private WinForms.ToolStripMenuItem _refreshItem = null!;
    private WinForms.ToolStripMenuItem _modeCompact = null!;
    private WinForms.ToolStripMenuItem _modeDetailed = null!;
    private WinForms.ToolStripMenuItem _modeMini = null!;
    private WinForms.ToolStripMenuItem _ratioRemaining = null!;
    private WinForms.ToolStripMenuItem _ratioUsed = null!;
    private WinForms.ToolStripMenuItem _topmostItem = null!;
    private WinForms.ToolStripMenuItem _lockItem = null!;
    private WinForms.ToolStripMenuItem _widgetItem = null!;
    private readonly TaskbarWidgetController? _widget;

    public TrayService(MainViewModel vm, AppSettings settings, MainWindow window,
        string settingsPath, Action openSettings, Action exit, TaskbarWidgetController? taskbarWidget = null)
    {
        _vm = vm;
        _settings = settings;
        _window = window;
        _settingsPath = settingsPath;
        _openSettings = openSettings;
        _exit = exit;
        _widget = taskbarWidget;

        _menu = new WinForms.ContextMenuStrip();
        BuildMenu();
        _menu.Opening += (_, _) => RefreshChecks();

        _icon = new WinForms.NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "QuotaDesk（演示数据）",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ToggleWindow();
        };

        _window.HideToTrayRequested += (_, _) => ShowCloseHintOnce();
    }

    private void BuildMenu()
    {
        var items = _menu.Items;
        items.Add(_refreshItem = new WinForms.ToolStripMenuItem("刷新", null,
            (_, _) => _ = RefreshAsync()));

        var modeMenu = new WinForms.ToolStripMenuItem("显示模式");
        modeMenu.DropDownItems.Add(_modeCompact = new WinForms.ToolStripMenuItem("紧凑模式", null,
            (_, _) => SetMode("compact")));
        modeMenu.DropDownItems.Add(_modeDetailed = new WinForms.ToolStripMenuItem("详细模式", null,
            (_, _) => SetMode("detailed")));
        modeMenu.DropDownItems.Add(_modeMini = new WinForms.ToolStripMenuItem("微缩栏", null,
            (_, _) => SetMode("mini")));
        items.Add(modeMenu);

        var dockMenu = new WinForms.ToolStripMenuItem("微缩栏停靠位置");
        dockMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem("屏幕上方", null,
            (_, _) => SetDock("top")));
        dockMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem("屏幕下方", null,
            (_, _) => SetDock("bottom")));
        dockMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem("屏幕左侧", null,
            (_, _) => SetDock("left")));
        dockMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem("屏幕右侧", null,
            (_, _) => SetDock("right")));
        dockMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem("浮动（不吸附）", null,
            (_, _) => SetDock("none")));
        dockMenu.ToolTipText = "选择后自动切换到微缩栏模式";
        items.Add(dockMenu);

        var ratioMenu = new WinForms.ToolStripMenuItem("显示比例");
        ratioMenu.DropDownItems.Add(_ratioRemaining = new WinForms.ToolStripMenuItem("剩余", null,
            (_, _) => SetRatio(true)));
        ratioMenu.DropDownItems.Add(_ratioUsed = new WinForms.ToolStripMenuItem("已用", null,
            (_, _) => SetRatio(false)));
        items.Add(ratioMenu);

        items.Add(_topmostItem = new WinForms.ToolStripMenuItem("置顶", null, (_, _) => ToggleTopmost()));
        items.Add(_lockItem = new WinForms.ToolStripMenuItem("锁定位置", null, (_, _) => ToggleLock()));
        items.Add(_widgetItem = new WinForms.ToolStripMenuItem("任务栏挂件", null,
            (_, _) => _widget?.Toggle())
        {
            ToolTipText = "在任务栏通知区左侧内嵌显示额度",
        });

        items.Add(new WinForms.ToolStripSeparator());
        items.Add(new WinForms.ToolStripMenuItem("设置…", null, (_, _) => _openSettings()));
        items.Add(new WinForms.ToolStripSeparator());
        items.Add(new WinForms.ToolStripMenuItem("退出", null, (_, _) => _exit()));
    }

    private async Task RefreshAsync()
    {
        try { await _vm.RefreshAsync(manual: true); } catch { /* 界面已显示失败状态 */ }
    }

    private void ToggleWindow()
    {
        if (_window.IsVisible) { _window.Hide(); }
        else
        {
            _window.Show();
            _window.Activate();
            _window.EnsureOnScreen();
        }
    }

    private void SetMode(string mode)
    {
        _settings.UiMode = mode;
        _window.ApplyMode();
        _window.SaveSettings();
    }

    /// <summary>设置微缩栏停靠边；当前不在微缩栏模式时自动切换过去。</summary>
    private void SetDock(string edge)
    {
        _settings.DockEdge = edge;
        if (_settings.UiMode != "mini") _settings.UiMode = "mini";
        _window.ApplyMode();
        _window.SaveSettings();
    }

    private void SetRatio(bool remaining)
    {
        _settings.ShowRemaining = remaining;
        _vm.OnSettingsChanged();
        _window.SaveSettings();
    }

    private void ToggleTopmost()
    {
        _settings.Topmost = !_settings.Topmost;
        _window.SetTopmost(_settings.Topmost);
        _window.SaveSettings();
    }

    private void ToggleLock()
    {
        _settings.LockPosition = !_settings.LockPosition;
        _window.SaveSettings();
    }

    private void SetScenario(string providerId, MockScenario scenario)
    {
        if (_vm.Find(providerId) is { } p)
        {
            p.Scenario = scenario;
            _ = RefreshAsync();
        }
    }

    private void RefreshChecks()
    {
        _refreshItem.Enabled = !_vm.IsRefreshing;
        _modeCompact.Checked = _settings.UiMode == "compact";
        _modeDetailed.Checked = _settings.UiMode == "detailed";
        _modeMini.Checked = _settings.UiMode == "mini";
        _ratioRemaining.Checked = _settings.ShowRemaining;
        _ratioUsed.Checked = !_settings.ShowRemaining;
        _topmostItem.Checked = _settings.Topmost;
        _lockItem.Checked = _settings.LockPosition;
        _widgetItem.Enabled = _widget is not null;
        _widgetItem.Checked = _settings.TaskbarWidgetEnabled;
    }

    /// <summary>首次隐藏到托盘时用气泡解释“关闭≠退出”。</summary>
    private void ShowCloseHintOnce()
    {
        if (_settings.CloseHintShown) return;
        _icon.BalloonTipTitle = "QuotaDesk 仍在运行";
        _icon.BalloonTipText = "窗口只是隐藏到托盘。托盘左键可再显示，右键菜单 → 退出 才会真正退出。";
        _icon.ShowBalloonTip(4000);
        _settings.CloseHintShown = true;
        _window.SaveSettings();
    }

    public void ShowStartupBalloon()
    {
        _icon.BalloonTipTitle = "QuotaDesk 已启动（演示数据）";
        _icon.BalloonTipText = "悬浮窗显示模拟额度。点击窗口切换紧凑/详细；关闭按钮只是隐藏，退出请用托盘右键菜单。";
        _icon.ShowBalloonTip(5000);
    }

    /// <summary>低额度通知等运行时提醒。</summary>
    public void ShowMessage(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(4000);
    }

    /// <summary>
    /// 测试钩子：直接走 NotifyIcon 右键同一代码路径（ContextMenuStrip.Show），
    /// 用于自动化复现托盘菜单问题。仅由 --selftest-menu 启动参数触发。
    /// </summary>
    public void DebugShowMenu()
    {
        var pos = new System.Drawing.Point(
            (int)(SystemParameters.WorkArea.Left + SystemParameters.WorkArea.Width / 2),
            (int)(SystemParameters.WorkArea.Top + SystemParameters.WorkArea.Height / 2));
        WinForms.Cursor.Position = pos;
        _menu.Show(pos);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
