using QuotaDesk.Services;
using Xunit;

namespace QuotaDesk.Tests;

/// <summary>任务栏挂件紧凑文本的拼接规则。</summary>
public class TaskbarTextTests
{
    [Theory]
    [InlineData("82%", "82")]
    [InlineData("137%", "137")]
    [InlineData(" 7% ", "7")]
    [InlineData("—", "—")]
    [InlineData("", "—")]
    [InlineData(null, "—")]
    public void CompactPercent_StripsPercentSign(string? input, string expected)
    {
        Assert.Equal(expected, TaskbarText.CompactPercent(input));
    }

    [Theory]
    [InlineData("82%", "37%", "82/37")]
    [InlineData("—", "6%", "—/6")]
    [InlineData(null, null, "—/—")]
    public void TwoWindows_JoinsWithSlash(string? fiveHour, string? week, string expected)
    {
        Assert.Equal(expected, TaskbarText.TwoWindows(fiveHour, week));
    }
}
