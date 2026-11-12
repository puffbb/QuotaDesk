using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using QuotaDesk.Services;

namespace QuotaDesk.Controls;

/// <summary>providerId → 平台强调色（辉光点等）。属性注入画刷，避免映射层依赖 WPF；留空时按资源 key 惰性解析。</summary>
public sealed class ProviderColorConverter : IValueConverter
{
    public Brush? Glm { get; set; }
    public Brush? Codex { get; set; }
    public Brush? Fallback { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) switch
        {
            "glm" => Glm ?? ThemeBrush.Find("GlmAccent"),
            "codex" => Codex ?? ThemeBrush.Find("CodexAccent"),
            _ => Fallback ?? ThemeBrush.Find("TextSecondary"),
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
