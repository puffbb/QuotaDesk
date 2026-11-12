using System.Diagnostics;
using QuotaDesk.Models;
using QuotaDesk.ViewModels;
using QuotaDesk.Views;

namespace QuotaDesk.Services;

/// <summary>
/// 任务栏挂件的生命周期：按设置开关创建/关闭窗口；隐藏挂件=写入设置并关闭（不是退出应用）。
/// </summary>
public sealed class TaskbarWidgetController : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly MainWindow _main;
    private readonly string _settingsPath;
    private readonly Action _openSettings;
    private TaskbarWidgetWindow? _window;

    public TaskbarWidgetController(MainViewModel vm, AppSettings settings, MainWindow main,
        string settingsPath, Action openSettings)
    {
        _vm = vm;
        _settings = settings;
        _main = main;
        _settingsPath = settingsPath;
        _openSettings = openSettings;
    }

    public bool IsEnabled => _settings.TaskbarWidgetEnabled;

    /// <summary>按当前设置应用开关状态（启动与切换共用）。</summary>
    public void ApplyEnabled()
    {
        if (_settings.TaskbarWidgetEnabled) Show();
        else Close();
    }

    /// <summary>切换开关（托盘菜单用），并持久化。</summary>
    public void Toggle()
    {
        _settings.TaskbarWidgetEnabled = !_settings.TaskbarWidgetEnabled;
        ApplyEnabled();
        Save();
    }

    /// <summary>设置窗口用的显式开关；值不变时不做动作。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_settings.TaskbarWidgetEnabled == enabled) return;
        Toggle();
    }

    private void Show()
    {
        try
        {
            if (_window is null)
            {
                _window = new TaskbarWidgetWindow(_vm, _settings, _main, _openSettings,
                    hideSelf: () => SetEnabled(false), _settingsPath);
                // 首次定位前先放到屏幕外，避免在 (0,0) 闪现一帧。
                _window.Left = -10000;
            }
            _window.Show();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private void Close()
    {
        _window?.Close();
        _window = null;
    }

    private void Save()
    {
        try { _settings.Save(_settingsPath); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    public void Dispose() => Close();
}
