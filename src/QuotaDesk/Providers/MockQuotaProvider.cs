using QuotaDesk.Models;
using QuotaDesk.Services;

namespace QuotaDesk.Providers;

/// <summary>
/// 阶段 1 模拟数据提供器：带延迟的假请求，覆盖正常、低额度、超额、缺失、旧数据、错误等场景。
/// 所有数据标记 Source="mock"，界面常驻“演示”标识。
/// </summary>
public sealed class MockQuotaProvider : IQuotaProvider
{
    private readonly Func<DateTimeOffset> _clock;

    public string ProviderId { get; }
    public MockScenario Scenario { get; set; } = MockScenario.Normal;

    public MockQuotaProvider(string providerId, Func<DateTimeOffset>? clock = null)
    {
        ProviderId = providerId;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        // 模拟网络延迟，给后续调度合并/竞态测试留出空间。
        await Task.Delay(Random.Shared.Next(120, 380), cancellationToken);
        return Build(_clock());
    }

    public ProviderSnapshot Build(DateTimeOffset now) => Scenario switch
    {
        MockScenario.Low => Low(now),
        MockScenario.Exceeded => Exceeded(now),
        MockScenario.Missing => Missing(now),
        MockScenario.Stale => Stale(now),
        MockScenario.Error => Fail("NetworkError", now),
        MockScenario.AuthError => Fail("Unauthorized", now),
        _ => Normal(now),
    };

    private QuotaWindow FiveHour(double used, int hours, int minutes) => new()
    {
        Id = $"{ProviderId}-5h",
        Kind = WindowKind.FiveHour,
        WindowSeconds = 5 * 3600,
        UsedPercent = used,
        ResetsAt = _clock().AddHours(hours).AddMinutes(minutes),
        StartState = StartState.Started,
    };

    private QuotaWindow Weekly(double used, DayOfWeek day, int hour, int minute) => new()
    {
        Id = $"{ProviderId}-weekly",
        Kind = WindowKind.Weekly,
        WindowSeconds = 7 * 24 * 3600,
        UsedPercent = used,
        ResetsAt = TimeText.NextWeeklyReset(_clock(), day, hour, minute),
        StartState = StartState.Started,
    };

    private ProviderSnapshot Normal(DateTimeOffset now)
    {
        var windows = ProviderId switch
        {
            // 数字与 PRODUCT.md 紧凑模式示例一致。
            "glm" => new List<QuotaWindow>
            {
                FiveHour(28, 2, 18),
                Weekly(69, DayOfWeek.Sunday, 14, 0),
            },
            _ => new List<QuotaWindow>
            {
                FiveHour(52, 3, 42),
                Weekly(84, DayOfWeek.Monday, 9, 0),
            },
        };
        return Snapshot(windows, ProviderState.Live, now, plan: ProviderId == "glm" ? "GLM Coding Plan（模拟）" : "Codex（模拟）");
    }

    private ProviderSnapshot Low(DateTimeOffset now) => ProviderId switch
    {
        // GLM：橙色（剩余 12%）；Codex：红色（剩余 7%）。
        "glm" => Snapshot(new List<QuotaWindow> { FiveHour(88, 0, 41), Weekly(78, DayOfWeek.Sunday, 14, 0) },
            ProviderState.Live, now),
        _ => Snapshot(new List<QuotaWindow> { FiveHour(93, 0, 17), Weekly(64, DayOfWeek.Monday, 9, 0) },
            ProviderState.Live, now),
    };

    private ProviderSnapshot Exceeded(DateTimeOffset now) => ProviderId switch
    {
        // GLM：恰好 100% 已用；Codex：137% 已用（超额，原始值保留不 clamp）。
        "glm" => Snapshot(new List<QuotaWindow>
        {
            new() { Id = "glm-5h", Kind = WindowKind.FiveHour, WindowSeconds = 5 * 3600, UsedPercent = 100, LimitReached = true, ResetsAt = _clock().AddMinutes(23), StartState = StartState.Started },
            Weekly(69, DayOfWeek.Sunday, 14, 0),
        }, ProviderState.Live, now),
        _ => Snapshot(new List<QuotaWindow>
        {
            new() { Id = "codex-5h", Kind = WindowKind.FiveHour, WindowSeconds = 5 * 3600, UsedPercent = 137, LimitReached = true, ResetsAt = _clock().AddMinutes(52), StartState = StartState.Started },
            Weekly(84, DayOfWeek.Monday, 9, 0),
        }, ProviderState.Live, now),
    };

    private ProviderSnapshot Missing(DateTimeOffset now) => ProviderId switch
    {
        // GLM：周窗口未报告；5h 有窗口但数值缺失。
        "glm" => Snapshot(new List<QuotaWindow>
        {
            new() { Id = "glm-5h", Kind = WindowKind.FiveHour, WindowSeconds = 5 * 3600, UsedPercent = null, ResetsAt = _clock().AddMinutes(95), StartState = StartState.Unknown },
        }, ProviderState.Live, now),
        // Codex：5h 明确未开始（与缺失区分）；周窗口未报告。
        _ => Snapshot(new List<QuotaWindow>
        {
            new() { Id = "codex-5h", Kind = WindowKind.FiveHour, WindowSeconds = 5 * 3600, UsedPercent = null, StartState = StartState.NotStarted },
        }, ProviderState.Live, now),
    };

    private ProviderSnapshot Stale(DateTimeOffset now)
    {
        var baseSnap = Normal(now);
        return new ProviderSnapshot
        {
            ProviderId = ProviderId,
            Source = baseSnap.Source,
            Plan = baseSnap.Plan,
            Windows = baseSnap.Windows,
            ObservedAt = now - TimeSpan.FromMinutes(37),
            LastAttemptAt = now,
            State = ProviderState.Stale,
            ErrorCode = "NetworkError",
        };
    }

    private ProviderSnapshot Fail(string errorCode, DateTimeOffset now) => new()
    {
        ProviderId = ProviderId,
        Source = "mock",
        Windows = [],
        ObservedAt = now,
        LastAttemptAt = now,
        State = ProviderState.Unavailable,
        ErrorCode = errorCode,
    };

    private ProviderSnapshot Snapshot(List<QuotaWindow> windows, ProviderState state, DateTimeOffset now, string? plan = null) =>
        new()
        {
            ProviderId = ProviderId,
            Source = "mock",
            Plan = plan,
            Windows = windows,
            ObservedAt = now,
            LastAttemptAt = now,
            State = state,
        };
}
