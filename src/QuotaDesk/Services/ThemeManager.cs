using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace QuotaDesk.Services;

/// <summary>
/// 主题管理：三档偏好（system / light / dark）。
/// WPF 会把放进 ResourceDictionary 的 Freezable 立即冻结，因此换肤走两层：
/// 1) 字典层：XAML 用 DynamicResource 引用，切换主题时整条替换为新画刷，界面即时更新；
/// 2) 单例层：每个 key 另持一个永不入字典的可变画刷，供转换器（SeverityToBrush 等）与
///    代码捕获使用——转换结果持有该实例，改 Color 即传播，避免换肤后旧色残留到下次数据刷新。
/// 任务栏挂件的文字颜色独立跟随系统任务栏深浅（挂件浮在任务栏上，与窗口主题无关）。
/// </summary>
public static class ThemeManager
{
    public const string SystemTheme = "system";
    public const string LightTheme = "light";
    public const string DarkTheme = "dark";

    private static string _preference = SystemTheme;
    private static bool _initialized;

    // 单例层：key → 可变画刷（含 Widget* 键）。
    private static readonly Dictionary<string, SolidColorBrush> _live = new();
    private static LinearGradientBrush? _liveGlow;

    public static string Preference => _preference;

    /// <summary>自检诊断（selftest 输出用）。</summary>
    public static string? LastDecision { get; private set; }

    public static void Init(string preference)
    {
        EnsureResources();
        ApplyPreference(preference);
        if (_initialized) return;
        _initialized = true;
        // 个性化（深/浅色）变更会以 General 类别的广播送达，收到后重读注册表。
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>切换偏好并立即生效（设置窗口保存时调用）。</summary>
    public static void ApplyPreference(string preference)
    {
        _preference = preference is LightTheme or DarkTheme ? preference : SystemTheme;
        RunOnUi(ApplyAll);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        RunOnUi(ApplyAll);
    }

    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
    }

    private static void ApplyAll()
    {
        var appsLight = SystemAppsLight();
        var taskbarLight = SystemTaskbarLight();
        var appDark = _preference == DarkTheme || (_preference == SystemTheme && !appsLight);
        ApplyPalette(AppPalette, dark: appDark);
        ApplyPalette(WidgetPalette, dark: !taskbarLight);
        ApplyGlow(dark: appDark);
        if (_live.TryGetValue("BgRoot", out var bg))
            LastDecision = $"pref={_preference} appsLight={appsLight} taskbarLight={taskbarLight} bgNow={bg.Color}";
    }

    /// <summary>AppsUseLightTheme：窗口/应用内容应使用的系统深浅色。</summary>
    private static bool SystemAppsLight() => RegLight("AppsUseLightTheme");

    /// <summary>SystemUsesLightTheme：任务栏/开始菜单的深浅色，挂件以此为准。</summary>
    private static bool SystemTaskbarLight() => RegLight("SystemUsesLightTheme");

    private static bool RegLight(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue(valueName) is int v) return v != 0;
        }
        catch
        {
            // 读不到（旧系统/注册表受限）按浅色处理，避免浅底浅字。
        }
        return true;
    }

    // ---------- 调色板（深色 = 极光夜幕，浅色 = 极光晨光） ----------

    private static readonly (string Key, string Dark, string Light)[] AppPalette =
    [
        ("BgRoot",       "#FF111522", "#FFF4F6FC"),
        ("BorderSoft",   "#FF303449", "#FFE2E6F2"),
        ("CardBg",       "#FF1B2030", "#FFFFFFFF"),
        ("ChipBg",       "#1FFFFFFF", "#14223355"),
        ("TrackBar",     "#FF33394B", "#FFE5E9F4"),
        ("TextPrimary",  "#F2F4FA",   "#FF23283A"),
        ("TextSecondary","#AEB4C2",   "#FF5C6478"),
        ("BarWarn",      "#FFAB40",   "#FFA35D00"),
        ("BarCritical",  "#FF7A7A",   "#FFD14343"),
        ("WarnBar",      "#FFAB40",   "#FFF5941E"),
        ("CriticalBar",  "#FF6B6B",   "#FFE05252"),
        ("DemoFg",       "#FFD9A8",   "#FF9C6A10"),
        ("DemoBg",       "#26FFB74D", "#24F5941E"),
        ("GlmAccent",    "#B3A0FF",   "#FF6A4DFF"),
        ("CodexAccent",  "#72DFD0",   "#FF0FA393"),
        ("AccentFg",     "#FF211C37", "#FFFFFFFF"),
        // 各处悬停/焦点的白色叠加层：浅色下换成同透明度的藏蓝叠加。
        ("ChipHoverBg",  "#38FFFFFF", "#38223355"),
        ("MenuHoverBg",  "#1FFFFFFF", "#1F223355"),
        ("FlatHoverBg",  "#14FFFFFF", "#14223355"),
        ("FocusTint",    "#24B3A0FF", "#246A4DFF"),
        ("TabFocusTint", "#15B3A0FF", "#156A4DFF"),
        ("RailVeil",     "#07FFFFFF", "#07223355"),
        ("Grip",         "#668791A6", "#667286A3"),
        ("ScrollThumb",  "#667F879E", "#66A8B0C4"),
    ];

    // 任务栏挂件：跟随系统任务栏深浅。
    private static readonly (string Key, string Dark, string Light)[] WidgetPalette =
    [
        ("WidgetText",     "#E9ECF4",   "#FF2A3040"),
        ("WidgetName",     "#C7CCDA",   "#FF4A5266"),
        ("WidgetDim",      "#7A8296",   "#FF6A7288"),
        ("WidgetWarn",     "#FFAB40",   "#FFA35D00"),
        ("WidgetCritical", "#FF7A7A",   "#FFD14343"),
        ("WidgetHoverBg",  "#12FFFFFF", "#12223355"),
    ];

    private static readonly Dictionary<string, (string Dark, string Light)> HexMap =
        AppPalette.Concat(WidgetPalette).ToDictionary(p => p.Key, p => (p.Dark, p.Light));

    // AuroraGlow 三段渐变：深色紫→青光晕 / 浅色晨光更淡的同色系。
    private static readonly string[] GlowDark = ["#209B75FF", "#009B75FF", "#1040D4BF"];
    private static readonly string[] GlowLight = ["#1E7C5CFF", "#007C5CFF", "#122BB8AC"];

    /// <summary>创建全部画刷（深色默认值）：单例层 + 字典镜像。必须在任何窗口创建之前调用。</summary>
    private static void EnsureResources()
    {
        var res = Application.Current?.Resources;
        if (res is null) return;
        foreach (var (key, darkHex, _) in AppPalette.Concat(WidgetPalette))
        {
            _live[key] = new SolidColorBrush(Parse(darkHex));
            res[key] = new SolidColorBrush(Parse(darkHex));
        }
        _liveGlow = CreateGlow(GlowDark);
        res["AuroraGlow"] = CreateGlow(GlowDark);
    }

    /// <summary>应用一组键的深/浅调色板：改单例画刷颜色 + 替换字典镜像（DynamicResource 随之刷新）。</summary>
    private static void ApplyPalette(
        (string Key, string Dark, string Light)[] palette, bool dark)
    {
        var res = Application.Current?.Resources;
        foreach (var (key, darkHex, lightHex) in palette)
        {
            var color = Parse(dark ? darkHex : lightHex);
            if (_live.TryGetValue(key, out var brush)) brush.Color = color;
            if (res is not null) res[key] = new SolidColorBrush(color);
        }
    }

    private static void ApplyGlow(bool dark)
    {
        var stops = dark ? GlowDark : GlowLight;
        if (_liveGlow is not null)
            for (var i = 0; i < _liveGlow.GradientStops.Count && i < stops.Length; i++)
                _liveGlow.GradientStops[i].Color = Parse(stops[i]);
        if (Application.Current?.Resources is { } res)
            res["AuroraGlow"] = CreateGlow(stops);
    }

    private static LinearGradientBrush CreateGlow(string[] stops)
    {
        var glow = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        glow.GradientStops.Add(new GradientStop(Parse(stops[0]), 0));
        glow.GradientStops.Add(new GradientStop(Parse(stops[1]), 0.6));
        glow.GradientStops.Add(new GradientStop(Parse(stops[2]), 1));
        return glow;
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>取单例活画刷（ThemeBrush.Find 用）；未初始化时返回 null。</summary>
    internal static SolidColorBrush? LiveOrNull(string key)
        => _live.TryGetValue(key, out var brush) ? brush : null;
}

/// <summary>按 key 取单例活画刷（转换器与代码捕获用），未初始化时回落到字典资源。</summary>
public static class ThemeBrush
{
    public static Brush Find(string key)
        => ThemeManager.LiveOrNull(key)
           ?? Application.Current?.TryFindResource(key) as Brush
           ?? Brushes.Transparent;
}
