using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace QuotaDesk.Controls;

/// <summary>
/// 低干扰连续细进度条：圆角轨道 + 填充。
/// 全部按 DIP 绘制，随 DPI 缩放。
/// </summary>
public sealed class MiniBar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(MiniBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(MiniBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(MiniBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    static MiniBar()
    {
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(MiniBar), new FrameworkPropertyMetadata(true));
    }

    /// <summary>填充比例 0..1。</summary>
    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var radius = Math.Max(1.5, h / 2);
        var full = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Track ?? Brushes.Transparent, null, full, radius, radius);

        var f = Math.Clamp(Fraction, 0, 1);
        if (f > 0)
        {
            var fillWidth = Math.Max(h, w * f); // 至少一个圆头
            var fill = new Rect(0, 0, Math.Min(fillWidth, w), h);
            dc.PushClip(new RectangleGeometry(full, radius, radius));
            dc.DrawRoundedRectangle(Fill ?? Brushes.Transparent, null, fill, radius, radius);
            dc.Pop();
        }

        // 极光方案使用连续的细进度条，保持四个额度的视觉口径一致。
    }
}
