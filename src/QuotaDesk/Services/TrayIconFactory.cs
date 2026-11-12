using System.Drawing;
using System.Drawing.Drawing2D;

namespace QuotaDesk.Services;

/// <summary>运行时绘制托盘图标，不依赖外部资源文件。</summary>
public static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(255, 34, 36, 46));
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 8);
            g.FillPath(bg, path);

            using var pen = new Pen(Color.FromArgb(255, 127, 166, 255), 3.2f);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            // 左半弧
            g.DrawArc(pen, 7, 7, 18, 18, 90, 180);
            // 右上填充段
            using var fg = new SolidBrush(Color.FromArgb(255, 255, 169, 64));
            g.FillPie(fg, 9, 9, 14, 14, 250, 60);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
