using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;
using QuotaDesk.Models;
using QuotaDesk.Providers;
using QuotaDesk.Services;
using QuotaDesk.ViewModels;
using QuotaDesk.Views;

namespace QuotaDesk;

public partial class App : Application
{
    private TrayService? _tray;
    private TaskbarWidgetController? _taskbarWidget;
    private ScreenshotHotkey? _screenshot;
    private static string? _errorLogPath;
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：重复启动时提示后退出，避免多窗口同时写设置。
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\QuotaDesk.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("QuotaDesk 已在运行（可查看系统托盘）。", "QuotaDesk",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        GC.KeepAlive(_singleInstanceMutex);

        base.OnStartup(e);

        var argsList = e.Args ?? [];

        // 设置目录：--settings-dir 参数 > QUOTADESK_SETTINGS_DIR > %APPDATA%\QuotaDesk
        var settingsDir = ArgsValue(argsList, "--settings-dir")
                          ?? Environment.GetEnvironmentVariable("QUOTADESK_SETTINGS_DIR")
                          ?? AppSettings.DefaultDirectory;
        var settingsPath = Path.Combine(settingsDir, "settings.json");
        _errorLogPath = Path.Combine(settingsDir, "error.log");

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            MessageBox.Show($"发生未处理错误：{args.Exception.Message}", "QuotaDesk",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // WinForms 控件（托盘）与后台线程的异常同样落日志。
        WinForms.Application.SetUnhandledExceptionMode(WinForms.UnhandledExceptionMode.CatchException);
        WinForms.Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogError(ex);
        };

        var settings = AppSettings.Load(settingsPath);

        // 主题在创建任何窗口前应用：画刷原地改写后所有 StaticResource 引用取到的即是目标色。
        ThemeManager.Init(settings.Theme);

        ApplyStartupArgs(argsList, settings);

        var vm = new MainViewModel(settings, settingsDir: settingsDir);
        foreach (var (provider, scenarioName) in StartupScenarios)
        {
            if (vm.Find(provider.Trim()) is { } pvm && MockScenarioNames.TryParse(scenarioName, out var sc))
                pvm.Scenario = sc;
        }
        StartupScenarios.Clear();
        var window = new MainWindow(vm, settings, settingsPath, argsList);
        _screenshot = new ScreenshotHotkey();

        void OpenSettings()
        {
            var dlg = new SettingsWindow(settings, vm, window, window.RefreshTimer, _taskbarWidget, _screenshot);
            dlg.Show();
            dlg.Activate();
        }
        _taskbarWidget = new TaskbarWidgetController(vm, settings, window, settingsPath, openSettings: OpenSettings);
        window.SettingsRequested += (_, _) => OpenSettings();
        _tray = new TrayService(vm, settings, window, settingsPath,
            openSettings: OpenSettings,
            exit: () =>
            {
                _taskbarWidget?.Dispose();
                _tray?.Dispose();
                Shutdown();
            },
            taskbarWidget: _taskbarWidget);

        window.HideToTrayRequested += (_, _) => { /* 气泡提示由 TrayService 处理 */ };
        vm.NotificationRaised += (_, message) => _tray.ShowMessage(message.Title, message.Text);

        window.RestorePosition();
        window.Show();
        window.EnsureOnScreen();

        // 任务栏挂件按设置启动（窗口置顶显示在通知区左侧）。
        _taskbarWidget.ApplyEnabled();

        if (!settings.CloseHintShown) _tray.ShowStartupBalloon();

        if (!_screenshot.Configure(settings.ScreenshotEnabled, settings.ScreenshotModifiers, settings.ScreenshotKey, out var hotkeyError))
            _tray.ShowMessage("截图快捷键不可用", hotkeyError ?? "请在设置中更换快捷键。");

        _ = window.WriteSelfTestAsync();

        // 测试钩子：--selftest-menu 在启动 2 秒后以与托盘右键相同的方式打开菜单，
        // 异常会进入 error.log（自动化复现托盘菜单问题用）。
        if (argsList.Any(a => a == "--selftest-menu"))
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { _tray.DebugShowMenu(); }
                catch (Exception ex) { LogError(ex); }
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _screenshot?.Dispose();
        _screenshot = null;
        base.OnExit(e);
    }

    /// <summary>错误日志：只记异常类型/消息/堆栈，不记录任何凭据。</summary>
    public static void LogError(Exception ex)
    {
        try
        {
            if (_errorLogPath is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_errorLogPath)!);
            File.AppendAllText(_errorLogPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch
        {
            // 日志失败不影响运行。
        }
    }

    private static void ApplyStartupArgs(string[] args, AppSettings settings)
    {
        if (ArgsValue(args, "--mode") is { } mode
            && mode is "detailed" or "compact" or "mini")
        {
            settings.UiMode = mode;
        }
        if (ArgsValue(args, "--show") is { } show)
            settings.ShowRemaining = !show.Equals("used", StringComparison.OrdinalIgnoreCase);
        if (ArgsValue(args, "--pos") is { } pos)
        {
            var parts = pos.Split(',');
            if (parts.Length == 2
                && double.TryParse(parts[0], out var x) && double.TryParse(parts[1], out var y))
            {
                settings.WindowX = x;
                settings.WindowY = y;
            }
        }
        // --scenario=provider:scenario 可重复；--source=provider:real|mock 覆盖数据来源（会随设置持久化）
        foreach (var a in args)
        {
            if (!a.StartsWith("--scenario=", StringComparison.Ordinal)) continue;
            var pair = a["--scenario=".Length..].Split(':', 2);
            if (pair.Length != 2) continue;
            StartupScenarios.Add((pair[0].Trim(), pair[1].Trim()));
        }
        foreach (var a in args)
        {
            if (!a.StartsWith("--source=", StringComparison.Ordinal)) continue;
            var pair = a["--source=".Length..].Split(':', 2);
            if (pair.Length != 2) continue;
            var (provider, source) = (pair[0].Trim().ToLowerInvariant(), pair[1].Trim().ToLowerInvariant());
            if (source is not ("real" or "mock")) continue;
            switch (provider)
            {
                case "glm": settings.GlmSource = source; break;
                case "codex": settings.CodexSource = source; break;
            }
        }
    }

    public static List<(string Provider, string Scenario)> StartupScenarios { get; } = [];

    private static string? ArgsValue(string[] args, string name)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(name + "=", StringComparison.Ordinal)) return a[(name.Length + 1)..];
        }
        return null;
    }
}
