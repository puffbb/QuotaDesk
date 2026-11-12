using QuotaDesk.Services;
using Xunit;

namespace QuotaDesk.Tests;

public class TimeTextTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 8, 14, 0, 0, TimeSpan.FromHours(8)); // 周四

    [Fact]
    public void Countdown_over_one_hour_uses_hhmm()
    {
        Assert.Equal("02:18后重置", TimeText.Countdown(Now, Now.AddHours(2).AddMinutes(18)));
    }

    [Fact]
    public void Countdown_under_one_hour_uses_minutes()
    {
        Assert.Equal("38分钟后重置", TimeText.Countdown(Now, Now.AddMinutes(38)));
    }

    [Fact]
    public void Countdown_past_reset_waits_for_update()
    {
        Assert.Equal("等待更新", TimeText.Countdown(Now, Now.AddMinutes(-1)));
    }

    [Fact]
    public void Weekly_reset_same_year_omits_year()
    {
        // 2026-10-08 是周四；下一个周日是 2026-10-11
        var reset = TimeText.NextWeeklyReset(Now, DayOfWeek.Sunday, 14, 0);
        Assert.Equal("周日 14:00", TimeText.ResetDateTime(Now, reset));
    }

    [Fact]
    public void Weekly_reset_cross_year_includes_year()
    {
        var reset = new DateTimeOffset(2027, 1, 3, 14, 0, 0, TimeSpan.FromHours(8)); // 周日
        var text = TimeText.ResetDateTime(Now, reset);
        Assert.Equal("2027-01-03 周日 14:00", text);
    }

    [Fact]
    public void Next_weekly_reset_skips_to_next_week_when_today_slot_passed()
    {
        // 周四 14:00 → 下一个周一 09:00 是 2026-10-12
        var reset = TimeText.NextWeeklyReset(Now, DayOfWeek.Monday, 9, 0);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(8)), reset);
    }

    [Fact]
    public void Next_weekly_reset_same_day_if_slot_not_passed()
    {
        // 周四 14:00，问周四 20:00 → 就是今天
        var reset = TimeText.NextWeeklyReset(Now, DayOfWeek.Thursday, 20, 0);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 20, 0, 0, TimeSpan.FromHours(8)), reset);
    }
}
