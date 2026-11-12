using QuotaDesk.Services;
using Xunit;

namespace QuotaDesk.Tests;

public class ScreenshotHotkeyTests
{
    [Theory]
    [InlineData(1, 0x41, true)]
    [InlineData(3, 0x5A, true)]
    [InlineData(4, 0x7B, true)]
    [InlineData(0, 0x41, false)]
    [InlineData(8, 0x41, false)]
    [InlineData(1, 0x40, false)]
    [InlineData(1, 0x7C, false)]
    public void Key_combinations_are_validated(int modifiers, int key, bool valid)
        => Assert.Equal(valid, ScreenshotHotkey.IsValid(modifiers, key));

    [Fact]
    public void Display_text_uses_standard_order_and_function_key_names()
    {
        Assert.Equal("Alt + A", ScreenshotHotkey.Format(1, 0x41));
        Assert.Equal("Ctrl + Alt + Shift + F12", ScreenshotHotkey.Format(7, 0x7B));
    }
}
