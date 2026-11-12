namespace QuotaDesk.Services;

/// <summary>时间展示辅助：全部使用本地时间。</summary>
public static class TimeText
{
    private static readonly string[] WeekCn =
    {
        "周日", "周一", "周二", "周三", "周四", "周五", "周六",
    };

    public static string Week(DayOfWeek day) => WeekCn[(int)day];

    public static string HHmm(DateTimeOffset local) => local.LocalDateTime.ToString("HH:mm");

    /// <summary>5h 窗口重置倒计时，如 “02:18后重置”；不足 1 小时显示 “38分钟后重置”。</summary>
    public static string Countdown(DateTimeOffset now, DateTimeOffset reset)
    {
        var left = reset - now;
        if (left <= TimeSpan.Zero) return "等待更新";
        if (left < TimeSpan.FromHours(1))
            return $"{(int)Math.Ceiling(left.TotalMinutes)}分钟后重置";
        var h = (int)left.TotalHours;
        var m = left.Minutes;
        return $"{h:00}:{m:00}后重置";
    }

    /// <summary>
    /// 周窗口重置时间：本地日期时间，跨年显示年份。
    /// 同年 “周日 14:00”，跨年 “2027-01-03 周日 14:00”。
    /// </summary>
    public static string ResetDateTime(DateTimeOffset now, DateTimeOffset resetLocal)
    {
        var local = resetLocal.LocalDateTime;
        if (local.Year != now.LocalDateTime.Year)
            return $"{local:yyyy-MM-dd} {Week(local.DayOfWeek)} {local:HH:mm}";
        return $"{Week(local.DayOfWeek)} {local:HH:mm}";
    }

    /// <summary>从 now 起下一个 weekly 重置点（本周或下周的指定星期/时刻；当天时刻未过也算本周）。</summary>
    public static DateTimeOffset NextWeeklyReset(DateTimeOffset now, DayOfWeek day, int hour, int minute)
    {
        var offset = now.Offset;
        var local = now.LocalDateTime;
        var candidate = local.Date.AddHours(hour).AddMinutes(minute);
        int delta = ((int)day - (int)local.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(delta);
        if (candidate <= local) candidate = candidate.AddDays(7);
        return new DateTimeOffset(candidate, offset);
    }
}
