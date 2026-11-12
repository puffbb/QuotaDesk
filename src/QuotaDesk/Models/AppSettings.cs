using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaDesk.Models;

/// <summary>
/// 本地偏好设置。只保存非敏感配置，不含任何密钥或凭据。
/// 默认保存在 %APPDATA%\QuotaDesk\settings.json，可用环境变量 QUOTADESK_SETTINGS_DIR 或
/// 命令行 --settings-dir 覆盖（测试与多开用）。
/// </summary>
public sealed class AppSettings
{
    /// <summary>界面模式：compact（单行极简）/ detailed（数据卡片）/ mini（微缩栏，可停靠屏幕边缘）。</summary>
    public string UiMode { get; set; } = "compact";
    /// <summary>主题："system"（默认，跟随 Windows 深浅色）/ "light"（极光晨光）/ "dark"（极光夜幕）。</summary>
    public string Theme { get; set; } = "system";
    /// <summary>微缩栏停靠边：none（浮动）/ top / bottom / left / right。仅 mini 模式生效。</summary>
    public string DockEdge { get; set; } = "none";
    /// <summary>兼容旧设置文件的布尔模式字段；新代码请使用 UiMode。</summary>
    [JsonPropertyName("DetailedMode")]
    public bool? LegacyDetailedMode { get; set; }
    /// <summary>兼容旧代码的布尔视图。</summary>
    [JsonIgnore]
    public bool DetailedMode
    {
        get => UiMode == "detailed";
        set => UiMode = value ? "detailed" : "compact";
    }
    /// <summary>true = 显示剩余比例（默认），false = 显示已用比例。</summary>
    public bool ShowRemaining { get; set; } = true;
    public bool Topmost { get; set; } = true;
    public bool LockPosition { get; set; } = false;
    public int RefreshSeconds { get; set; } = 120;
    /// <summary>剩余量低于该百分比用橙色。</summary>
    public double WarnRemainingPercent { get; set; } = 20;
    /// <summary>剩余量低于该百分比用红色。</summary>
    public double CriticalRemainingPercent { get; set; } = 10;
    /// <summary>Codex 数据来源："real"（默认，已验证）或 "mock"（演示）。</summary>
    public string CodexSource { get; set; } = "real";
    /// <summary>GLM 数据来源："mock"（默认，真实接口未验证）或 "real"（阶段 4 前回落为未配置提示）。</summary>
    public string GlmSource { get; set; } = "mock";
    /// <summary>低额度通知，默认关闭；开启后按平台/窗口/重置周期/阈值去重。</summary>
    public bool LowQuotaNotify { get; set; } = false;
    /// <summary>任务栏额度挂件（嵌在通知区左侧的双行额度显示），默认开启。</summary>
    public bool TaskbarWidgetEnabled { get; set; } = true;
    /// <summary>
    /// 挂件右缘到任务栏右缘的距离（DIP）。null = 自动吸附在"显示隐藏的图标"按钮左侧；
    /// 用户拖动后写入固定偏移，此后不再自动吸附。
    /// </summary>
    public double? TaskbarOffsetRight { get; set; }
    /// <summary>开机自动启动（可选项，默认关闭；写当前用户注册表 Run 项）。</summary>
    public bool AutoStart { get; set; } = false;
    /// <summary>本机全局截图快捷键；修饰键为 Windows MOD_* 位标志，默认 Alt+A。</summary>
    public bool ScreenshotEnabled { get; set; } = true;
    public int ScreenshotModifiers { get; set; } = 1;
    public int ScreenshotKey { get; set; } = 0x41;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    /// <summary>首次关闭到托盘的行为提示已展示过。</summary>
    public bool CloseHintShown { get; set; } = false;

    [JsonIgnore]
    public static string DefaultDirectory
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("QUOTADESK_SETTINGS_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuotaDesk");
        }
    }

    public static string DefaultFilePath => Path.Combine(DefaultDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var raw = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions);
            if (loaded is null) return new AppSettings();
            // 迁移：旧设置只有 DetailedMode 布尔、没有 UiMode 时，沿用旧值。
            if (loaded.LegacyDetailedMode == true && !raw.Contains("\"UiMode\""))
                loaded.UiMode = "detailed";
            return loaded;
        }
        catch (Exception)
        {
            // 设置损坏时不让应用崩溃，回到默认值。
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
