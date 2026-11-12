using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using QuotaDesk.Services;

namespace QuotaDesk.Controls;

/// <summary>
/// 进度条填充 = 平台强调色（健康时）或严重程度色（警告/严重时）。
/// MultiBinding：values[0]=Severity，values[1]=ProviderId。
/// 画刷属性留空时按资源 key 惰性解析（主题切换后取到的是同一共享实例，无需重新绑定）。
/// </summary>
public sealed class AccentBarConverter : IMultiValueConverter
{
    public Brush? NormalGlm { get; set; }
    public Brush? NormalCodex { get; set; }
    public Brush? NormalFallback { get; set; }
    public Brush? Warn { get; set; }
    public Brush? Critical { get; set; }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var severity = values.Length > 0 ? values[0] : null;
        var providerId = values.Length > 1 ? values[1] as string : null;
        if (severity is Severity.Critical) return Critical ?? ThemeBrush.Find("CriticalBar");
        if (severity is Severity.Warn) return Warn ?? ThemeBrush.Find("WarnBar");
        return providerId switch
        {
            "glm" => NormalGlm ?? ThemeBrush.Find("GlmAccent"),
            "codex" => NormalCodex ?? ThemeBrush.Find("CodexAccent"),
            _ => NormalFallback ?? ThemeBrush.Find("TextPrimary"),
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
