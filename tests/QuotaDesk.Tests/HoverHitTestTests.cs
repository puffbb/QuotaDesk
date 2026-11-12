using System.Windows;
using QuotaDesk.Services;
using Xunit;

namespace QuotaDesk.Tests;

public class HoverHitTestTests
{
    [Fact]
    public void Pointer_can_move_from_widget_to_card_through_gap_without_dismissing()
    {
        var widget = new Rect(100, 200, 100, 36);
        var card = new Rect(80, 46, 120, 150);
        Assert.True(HoverHitTest.Contains(widget, card, new Point(150, 210)));
        Assert.True(HoverHitTest.Contains(widget, card, new Point(150, 198)));
        Assert.True(HoverHitTest.Contains(widget, card, new Point(150, 100)));
        Assert.False(HoverHitTest.Contains(widget, card, new Point(60, 198)));
        Assert.False(HoverHitTest.Contains(widget, card, new Point(250, 210)));
    }

    [Fact]
    public void Reversed_top_taskbar_and_absent_card_are_handled()
    {
        var widget = new Rect(-200, 10, 100, 36);
        var card = new Rect(-220, 50, 120, 150);
        Assert.True(HoverHitTest.Contains(widget, card, new Point(-150, 48)));
        Assert.False(HoverHitTest.Contains(widget, null, new Point(-150, 48)));
        Assert.False(HoverHitTest.Contains(widget, new Rect(10, 50, 120, 150), new Point(-150, 48)));
    }
}
