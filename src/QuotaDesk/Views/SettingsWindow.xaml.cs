using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using QuotaDesk.Models;
using QuotaDesk.Providers.Glm;
using KeyStatus = QuotaDesk.Providers.Glm.GlmKeyStatus; // XAML 的 GlmKeyStatus(TextBlock) 遮蔽了枚举名
using QuotaDesk.Services;
using QuotaDesk.ViewModels;

namespace QuotaDesk.Views;

/// <summary>设置窗口：编辑本地偏好与数据来源。保存后立即生效并持久化。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private readonly DispatcherTimer _refreshTimer;
    private readonly TaskbarWidgetController? _taskbarWidget;
    private readonly ScreenshotHotkey? _screenshot;
    private int _shortcutModifiers;
    private int _shortcutKey;

    public SettingsWindow(AppSettings settings, MainViewModel vm, MainWindow main, DispatcherTimer refreshTimer,
        TaskbarWidgetController? taskbarWidget = null, ScreenshotHotkey? screenshot = null)
    {
        InitializeComponent();
        _settings = settings;
        _vm = vm;
        _main = main;
        _refreshTimer = refreshTimer;
        _taskbarWidget = taskbarWidget;
        _screenshot = screenshot;

        _shortcutModifiers = settings.ScreenshotModifiers;
        _shortcutKey = settings.ScreenshotKey;
        ScreenshotEnabledCheck.IsChecked = settings.ScreenshotEnabled;
        ScreenshotShortcutBox.Text = ScreenshotHotkey.IsValid(_shortcutModifiers, _shortcutKey)
            ? ScreenshotHotkey.Format(_shortcutModifiers, _shortcutKey) : "请设置快捷键";

        TopmostCheck.IsChecked = _settings.Topmost;
        var theme = _settings.Theme is ThemeManager.LightTheme or ThemeManager.DarkTheme
            ? _settings.Theme : ThemeManager.SystemTheme;
        ThemeSystemCheck.IsChecked = theme == ThemeManager.SystemTheme;
        ThemeLightCheck.IsChecked = theme == ThemeManager.LightTheme;
        ThemeDarkCheck.IsChecked = theme == ThemeManager.DarkTheme;
        LockCheck.IsChecked = _settings.LockPosition;
        RemainingCheck.IsChecked = _settings.ShowRemaining;
        TaskbarWidgetCheck.IsChecked = _settings.TaskbarWidgetEnabled;
        CodexRealCheck.IsChecked = _settings.CodexSource == "real";
        GlmRealCheck.IsChecked = _settings.GlmSource == "real";
        LowQuotaNotifyCheck.IsChecked = _settings.LowQuotaNotify;
        AutoStartCheck.IsChecked = AutoStartHelper.IsEnabled();
        WarnBox.Text = _settings.WarnRemainingPercent.ToString("0.#");
        CriticalBox.Text = _settings.CriticalRemainingPercent.ToString("0.#");
        RefreshBox.Text = _settings.RefreshSeconds.ToString();
        RefreshGlmKeyStatus();
    }

    private void RefreshGlmKeyStatus()
    {
        // 区分未配置与无法解密/读取：后者原文件已保留，用户可重存覆盖或删除后重配。
        GlmKeyStatus.Text = GlmKeyStore.TryLoad(_vm.SettingsDir).Status switch
        {
            KeyStatus.Ok => "已配置密钥（DPAPI 加密存储，仅本机当前用户可解密）",
            KeyStatus.Unreadable => "已有密钥文件但无法解密（可能来自其他 Windows 用户或已损坏）。原文件已保留：重新保存将覆盖，或点「删除密钥」后重配。",
            _ => "未配置密钥；真实数据不可用",
        };
    }

    private void GlmKeySave_Click(object sender, RoutedEventArgs e)
    {
        var key = GlmKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            GlmKeyStatus.Text = "请先粘贴密钥再保存。";
            return;
        }
        GlmKeyStore.Save(_vm.SettingsDir, key);
        GlmKeyBox.Clear();
        RefreshGlmKeyStatus();
        // key 更换后旧账号缓存随之隔离；来源为真实时立即刷新。
        if (_settings.GlmSource == "real") _vm.ReconfigureProviders();
    }

    private void GlmKeyDelete_Click(object sender, RoutedEventArgs e)
    {
        GlmKeyStore.Delete(_vm.SettingsDir);
        GlmKeyBox.Clear();
        RefreshGlmKeyStatus();
        if (_settings.GlmSource == "real") _vm.ReconfigureProviders();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var screenshotEnabled = ScreenshotEnabledCheck.IsChecked == true;
        if (screenshotEnabled && !ScreenshotHotkey.IsValid(_shortcutModifiers, _shortcutKey))
        {
            ScreenshotShortcutHint.Text = "请设置有效的截图快捷键。";
            return;
        }
        if (_screenshot is not null && !_screenshot.Configure(screenshotEnabled, _shortcutModifiers, _shortcutKey, out var shortcutError))
        {
            ScreenshotShortcutHint.Text = shortcutError;
            ScreenshotShortcutBox.Focus();
            return;
        }
        _settings.ScreenshotEnabled = screenshotEnabled;
        _settings.ScreenshotModifiers = _shortcutModifiers;
        _settings.ScreenshotKey = _shortcutKey;
        _settings.Topmost = TopmostCheck.IsChecked == true;
        _settings.Theme = ThemeLightCheck.IsChecked == true ? ThemeManager.LightTheme
            : ThemeDarkCheck.IsChecked == true ? ThemeManager.DarkTheme
            : ThemeManager.SystemTheme;
        ThemeManager.ApplyPreference(_settings.Theme);
        _settings.LockPosition = LockCheck.IsChecked == true;
        _settings.ShowRemaining = RemainingCheck.IsChecked == true;
        _taskbarWidget?.SetEnabled(TaskbarWidgetCheck.IsChecked == true);

        var codexReal = CodexRealCheck.IsChecked == true;
        var glmReal = GlmRealCheck.IsChecked == true;
        var sourceChanged = (_settings.CodexSource == "real") != codexReal
                            || (_settings.GlmSource == "real") != glmReal;
        _settings.CodexSource = codexReal ? "real" : "mock";
        _settings.GlmSource = glmReal ? "real" : "mock";
        _settings.LowQuotaNotify = LowQuotaNotifyCheck.IsChecked == true;
        _vm.Notifier.Enabled = _settings.LowQuotaNotify;
        _settings.AutoStart = AutoStartCheck.IsChecked == true;
        try
        {
            if (AutoStartHelper.IsEnabled() != _settings.AutoStart)
                AutoStartHelper.Set(_settings.AutoStart);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"设置开机自启失败：{ex.Message}", "QuotaDesk",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            AutoStartCheck.IsChecked = AutoStartHelper.IsEnabled();
        }

        if (double.TryParse(WarnBox.Text, out var warn))
            _settings.WarnRemainingPercent = Math.Clamp(warn, 0, 100);
        if (double.TryParse(CriticalBox.Text, out var critical))
            _settings.CriticalRemainingPercent = Math.Clamp(critical, 0, 100);
        if (_settings.CriticalRemainingPercent > _settings.WarnRemainingPercent)
            _settings.CriticalRemainingPercent = _settings.WarnRemainingPercent;
        if (int.TryParse(RefreshBox.Text, out var refresh))
            _settings.RefreshSeconds = Math.Clamp(refresh, 30, 3600);

        _main.SetTopmost(_settings.Topmost);
        _refreshTimer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        _vm.OnSettingsChanged();
        _main.SaveSettings();
        if (sourceChanged) _vm.ReconfigureProviders();
        Close();
    }

    private void ScreenshotShortcut_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        var flags = System.Windows.Input.Keyboard.Modifiers;
        var modifiers = (flags.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? 1 : 0)
                      | (flags.HasFlag(System.Windows.Input.ModifierKeys.Control) ? 2 : 0)
                      | (flags.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? 4 : 0);
        e.Handled = true;
        if (flags.HasFlag(System.Windows.Input.ModifierKeys.Windows) || !ScreenshotHotkey.IsValid(modifiers, vk))
        {
            ScreenshotShortcutHint.Text = "请按 Ctrl、Alt 或 Shift 加字母、数字或 F1–F12。";
            return;
        }
        _shortcutModifiers = modifiers;
        _shortcutKey = vk;
        ScreenshotShortcutBox.Text = ScreenshotHotkey.Format(modifiers, vk);
        ScreenshotShortcutHint.Text = "点击保存后生效。";
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // The close button owns its input; only the header background drags.
        for (var element = e.OriginalSource as DependencyObject; element is not null;)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase) return;
            element = element is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
