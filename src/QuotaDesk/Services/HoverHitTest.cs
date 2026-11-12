using System.Windows;

namespace QuotaDesk.Services;

/// <summary>判断鼠标是否位于挂件、卡片或两者之间的短暂通道；所有坐标使用屏幕物理像素。</summary>
public static class HoverHitTest
{
    public static bool Contains(Rect widget, Rect? card, Point cursor)
    {
        if (widget.Contains(cursor)) return true;
        if (card is not { } surface) return false;
        if (surface.Contains(cursor)) return true;
        var left = Math.Max(widget.Left, surface.Left);
        var right = Math.Min(widget.Right, surface.Right);
        var top = Math.Min(widget.Bottom, surface.Bottom);
        var bottom = Math.Max(widget.Top, surface.Top);
        return right > left && bottom > top && new Rect(new Point(left, top), new Point(right, bottom)).Contains(cursor);
    }
}
