using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace QuotaDesk.Controls;

/// <summary>
/// 环形仪表（方案 D）：轨道圆环 + 比例弧线（圆头、从 12 点顺时针）+ 中心文字。
/// 全部按 DIP 绘制，随 DPI 缩放。
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelSizeProperty = DependencyProperty.Register(
        nameof(LabelSize), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(9.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>填充比例 0..1。</summary>
    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <summary>中心文字（如 "72%" 或 "—"）。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Brush? LabelBrush
    {
        get => (Brush?)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public double LabelSize
    {
        get => (double)GetValue(LabelSizeProperty);
        set => SetValue(LabelSizeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var thickness = Math.Max(3, size * 0.12);
        var radius = size / 2 - thickness / 2;

        var trackPen = new Pen(Track ?? Brushes.Transparent, thickness);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        var fraction = Math.Clamp(Fraction, 0, 1);
        if (fraction > 0.002)
        {
            var strokePen = new Pen(Stroke ?? Brushes.Transparent, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            if (fraction >= 0.998)
            {
                dc.DrawEllipse(null, strokePen, center, radius, radius);
            }
            else
            {
                var angle = 2 * Math.PI * fraction;
                var start = new Point(center.X, center.Y - radius);
                var end = new Point(
                    center.X + radius * Math.Sin(angle),
                    center.Y - radius * Math.Cos(angle));
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(start, isFilled: false, isClosed: false);
                    ctx.ArcTo(
                        end,
                        new Size(radius, radius),
                        rotationAngle: 0,
                        isLargeArc: fraction > 0.5,
                        sweepDirection: SweepDirection.Clockwise,
                        isStroked: true,
                        isSmoothJoin: false);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, strokePen, geometry);
            }
        }

        if (!string.IsNullOrEmpty(Label))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var text = new FormattedText(
                Label,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
                    FontWeights.SemiBold, FontStretches.Normal),
                LabelSize,
                LabelBrush ?? Brushes.White,
                dpi.PixelsPerDip);
            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }
    }
}
