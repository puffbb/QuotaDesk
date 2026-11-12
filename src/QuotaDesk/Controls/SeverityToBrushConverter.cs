using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using QuotaDesk.Services;

namespace QuotaDesk.Controls;

/// <summary>剩余量严重程度 → 进度条/数字颜色。画刷属性留空时按资源 key 惰性解析。</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    /// <summary>true = 用任务栏挂件调色板（WidgetText/WidgetWarn/WidgetCritical，跟随任务栏深浅）。</summary>
    public bool Widget { get; set; }
    public Brush? Normal { get; set; }
    public Brush? Warn { get; set; }
    public Brush? Critical { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var normal = Normal ?? ThemeBrush.Find(Widget ? "WidgetText" : "TextPrimary");
        return value switch
        {
            Severity.Warn => Warn ?? ThemeBrush.Find(Widget ? "WidgetWarn" : "BarWarn") ?? normal,
            Severity.Critical => Critical ?? ThemeBrush.Find(Widget ? "WidgetCritical" : "BarCritical") ?? normal,
            _ => normal,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
