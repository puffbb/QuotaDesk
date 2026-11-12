using QuotaDesk.Models;
using QuotaDesk.Providers;
using QuotaDesk.Services;
using Xunit;

namespace QuotaDesk.Tests;

/// <summary>展示映射规则测试（PRODUCT.md 验收第 1、2 条的核心逻辑）。</summary>
public class DisplayMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 0, 0, TimeSpan.FromHours(8));

    private static AppSettings Settings(bool showRemaining = true) => new()
    {
        ShowRemaining = showRemaining,
        WarnRemainingPercent = 20,
        CriticalRemainingPercent = 10,
    };

    private static QuotaWindow FiveHour(double? used, DateTimeOffset? reset = null, StartState start = StartState.Started) => new()
    {
        Id = "p-5h",
        Kind = WindowKind.FiveHour,
        UsedPercent = used,
        ResetsAt = reset,
        StartState = start,
    };

    [Fact]
    public void Remaining_is_clamped_for_display_but_raw_used_is_preserved()
    {
        Assert.Equal(72, DisplayMapper.RemainingPercent(28));
        Assert.Equal(0, DisplayMapper.RemainingPercent(137));   // 展示 clamp 到 0
        Assert.Equal(0, DisplayMapper.RemainingPercent(100));
    }

    [Fact]
    public void Exceeded_window_shows_raw_used_and_critical_severity()
    {
        var snap = new ProviderSnapshot
        {
            ProviderId = "codex",
            Windows = [FiveHour(137, Now.AddMinutes(30))],
            ObservedAt = Now,
            LastAttemptAt = Now,
            State = ProviderState.Live,
        };
        var row = DisplayMapper.Map(snap, Settings(), Now).Rows[0];
        var rowUsedMode = DisplayMapper.Map(snap, Settings(showRemaining: false), Now).Rows[0];
        Assert.Equal("137%", rowUsedMode.PercentText);            // 已用口径保留原始超额值
        Assert.Equal("0%", row.PercentText);                      // 剩余口径 clamp
        Assert.Equal(Severity.Critical, row.Severity);
        Assert.Equal("已超额", row.StatusText);
    }

    [Fact]
    public void Missing_window_is_not_reported_and_null_value_is_not_zero()
    {
        var snap = new ProviderSnapshot
        {
            ProviderId = "glm",
            Windows = [], // 服务未返回 5h 与周窗口
            ObservedAt = Now,
            LastAttemptAt = Now,
            State = ProviderState.Live,
        };
        var rows = DisplayMapper.Map(snap, Settings(), Now).Rows;

        Assert.All(rows, r => Assert.Equal("未报告", r.StatusText));
        Assert.All(rows, r => Assert.Equal("—", r.PercentText));
        Assert.All(rows, r => Assert.False(r.HasData));
    }

    [Fact]
    public void Failed_request_shows_no_data_not_not_reported()
    {
        // 请求整体失败（Unavailable）与“服务在线但缺窗口”是不同状态。
        var snap = new ProviderSnapshot
        {
            ProviderId = "glm",
            Windows = [],
            ObservedAt = Now,
            LastAttemptAt = Now,
            State = ProviderState.Unavailable,
            ErrorCode = "NetworkError",
        };
        var rows = DisplayMapper.Map(snap, Settings(), Now).Rows;
        Assert.All(rows, r => Assert.Equal("暂无数据", r.StatusText));
    }

    [Fact]
    public void Explicit_not_started_is_different_from_missing()
    {
        var snap = new ProviderSnapshot
        {
            ProviderId = "codex",
            Windows = [FiveHour(null, start: StartState.NotStarted)],
            ObservedAt = Now,
            LastAttemptAt = Now,
            State = ProviderState.Live,
        };
        var row = DisplayMapper.Map(snap, Settings(), Now).Rows[0];
        Assert.Equal("未开始", row.StatusText);
    }

    [Fact]
    public void Null_value_window_shows_no_data_not_zero()
    {
        var snap = new ProviderSnapshot
        {
            ProviderId = "glm",
            Windows = [FiveHour(null)],
            ObservedAt = Now,
            LastAttemptAt = Now,
            State = ProviderState.Live,
        };
        var row = DisplayMapper.Map(snap, Settings(), Now).Rows[0];
        Assert.Equal("暂无数据", row.StatusText);
        Assert.Equal("—", row.PercentText);
    }

    [Fact]
    public void Passed_reset_marks_waiting_not_recovered()
    {
        var snap = new ProviderSnapshot
        {
            ProviderId = "glm",
            Windows = [FiveHour(28, Now.AddMinutes(-5))], // 重置点已过
            ObservedAt = Now,
            LastAttemptAt = Now,
            State = ProviderState.Live,
        };
        var row = DisplayMapper.Map(snap, Settings(), Now).Rows[0];
        Assert.Equal("等待更新", row.RightText);
        Assert.Equal("28%", DisplayMapper.Map(snap, Settings(showRemaining: false), Now).Rows[0].PercentText);
    }

    [Fact]
    public void Stale_snapshot_is_marked_with_old_data_time()
    {
        var snap = new ProviderSnapshot
        {
            ProviderId = "glm",
            Windows = [FiveHour(28, Now.AddHours(2))],
            ObservedAt = Now.AddMinutes(-37),
            LastAttemptAt = Now,
            State = ProviderState.Stale,
        };
        var display = DisplayMapper.Map(snap, Settings(), Now);
        Assert.EndsWith("（旧）", display.UpdateText);
        Assert.Contains("旧数据", display.Rows[0].StatusText);
    }

    [Fact]
    public void Auth_error_text_differs_by_provider()
    {
        ProviderSnapshot Snap(string id) => new()
        {
            ProviderId = id,
            Windows = [],
            ObservedAt = Now,
            LastAttemptAt = Now,
            State = ProviderState.Unavailable,
            ErrorCode = "Unauthorized",
        };
        Assert.Equal("检查密钥", DisplayMapper.MapError(Snap("glm")));
        Assert.Equal("需要重新登录", DisplayMapper.MapError(Snap("codex")));
    }

    [Fact]
    public void Severity_thresholds_follow_settings()
    {
        var settings = new AppSettings { WarnRemainingPercent = 20, CriticalRemainingPercent = 10 };
        Assert.Equal(Severity.Normal, DisplayMapper.SeverityOf(31, settings));
        Assert.Equal(Severity.Warn, DisplayMapper.SeverityOf(16, settings));
        Assert.Equal(Severity.Critical, DisplayMapper.SeverityOf(7, settings));
    }

    [Fact]
    public void Remaining_and_used_modes_are_consistent_directions()
    {
        // 验收第 1 条：切换后数字、进度方向一致
        var snap = new ProviderSnapshot
        {
            ProviderId = "glm",
            Windows = [FiveHour(28, Now.AddHours(2))],
            ObservedAt = Now,
            LastAttemptAt = Now,
            State = ProviderState.Live,
        };
        var remaining = DisplayMapper.Map(snap, Settings(showRemaining: true), Now).Rows[0];
        var used = DisplayMapper.Map(snap, Settings(showRemaining: false), Now).Rows[0];

        Assert.Equal("72%", remaining.PercentText);
        Assert.Equal(0.72, remaining.Fraction, precision: 3);
        Assert.Equal("28%", used.PercentText);
        Assert.Equal(0.28, used.Fraction, precision: 3);
    }
}

/// <summary>模拟场景不变量。</summary>
public class MockScenarioTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Normal_matches_product_mock_numbers()
    {
        var glm = new MockQuotaProvider("glm", () => Now).Build(Now);
        Assert.Equal(28, glm.Windows[0].UsedPercent);  // 5h 72% 剩余
        Assert.Equal(69, glm.Windows[1].UsedPercent);  // 周 31% 剩余

        var codex = new MockQuotaProvider("codex", () => Now).Build(Now);
        Assert.Equal(52, codex.Windows[0].UsedPercent); // 5h 48% 剩余
        Assert.Equal(84, codex.Windows[1].UsedPercent); // 周 16% 剩余
    }

    [Fact]
    public void Every_scenario_carries_mock_source()
    {
        foreach (MockScenario scenario in Enum.GetValues<MockScenario>())
        {
            var provider = new MockQuotaProvider("glm", () => Now) { Scenario = scenario };
            var snap = provider.Build(Now);
            Assert.Equal("mock", snap.Source);
            Assert.Equal(scenario == MockScenario.Error || scenario == MockScenario.AuthError,
                snap.State == ProviderState.Unavailable);
        }
    }
}
