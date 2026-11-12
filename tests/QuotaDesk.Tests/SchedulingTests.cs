using QuotaDesk.Models;
using QuotaDesk.Providers;
using QuotaDesk.Scheduling;
using QuotaDesk.Services;
using QuotaDesk.ViewModels;
using Xunit;

namespace QuotaDesk.Tests;

/// <summary>阶段 5：调度、合并、退避、重置去重与通知去重（可控时钟 + 假提供器）。</summary>
public class RefreshCoordinatorTests
{
    private DateTimeOffset Now { get; set; } = new(2026, 10, 8, 18, 0, 0, TimeSpan.FromHours(8));

    /// <summary>可编排的假提供器：按脚本返回快照，记录调用次数。</summary>
    private sealed class FakeProvider(string id, Func<int, Task<ProviderSnapshot>> impl) : IQuotaProvider
    {
        public string ProviderId { get; } = id;
        public int Calls { get; private set; }

        public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return await impl(Calls);
        }
    }

    private static ProviderSnapshot LiveSnap(string id, double used = 30) => new()
    {
        ProviderId = id,
        Source = "test",
        Windows = [new QuotaWindow { Id = $"{id}-5h", Kind = WindowKind.FiveHour, UsedPercent = used }],
        ObservedAt = DateTimeOffset.Now,
        LastAttemptAt = DateTimeOffset.Now,
        State = ProviderState.Live,
    };

    private static ProviderSnapshot RateLimited(string id, TimeSpan? retryAfter) => new()
    {
        ProviderId = id,
        Source = "test",
        Windows = [],
        ObservedAt = DateTimeOffset.Now,
        LastAttemptAt = DateTimeOffset.Now,
        State = ProviderState.Unavailable,
        ErrorCode = "RateLimited",
        RetryAfter = retryAfter,
    };

    [Fact]
    public async Task Concurrent_refreshes_merge_into_one_request()
    {
        var provider = new FakeProvider("glm", async call =>
        {
            await Task.Delay(250);
            return LiveSnap("glm");
        });
        var coordinator = new RefreshCoordinator([provider], () => Now);
        var received = new List<ProviderSnapshot>();
        Task OnSnapshot(string id, ProviderSnapshot s) => Task.CompletedTask;

        var t1 = coordinator.RefreshAsync(provider, OnSnapshot, CancellationToken.None);
        var t2 = coordinator.RefreshAsync(provider, OnSnapshot, CancellationToken.None);
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, provider.Calls);            // 合并为一次请求
        Assert.Same(t1, t2);                        // 复用同一在途任务
    }

    [Fact]
    public async Task Backoff_skips_provider_until_window_passes_and_caps_long_values()
    {
        // 第 1 次调用：429 + RetryAfter 6 小时；此后：正常数据。
        var provider = new FakeProvider("codex", call =>
            Task.FromResult(call == 1 ? RateLimited("codex", TimeSpan.FromHours(6)) : LiveSnap("codex")));
        var coordinator = new RefreshCoordinator([provider], () => Now);
        var received = new List<ProviderSnapshot>();
        Task OnSnapshot(string id, ProviderSnapshot s)
        {
            received.Add(s);
            return Task.CompletedTask;
        }

        await coordinator.RefreshAsync(provider, OnSnapshot, CancellationToken.None); // 429 + RetryAfter 6h
        Assert.Equal(1, provider.Calls);
        Assert.Equal("RateLimited", received[^1].ErrorCode);

        Now += TimeSpan.FromSeconds(60);
        await coordinator.RefreshAsync(provider, OnSnapshot, CancellationToken.None);
        Assert.Equal(1, provider.Calls);            // 退避窗口内跳过

        // 6h 超过 15min 上限：15 分钟后即可再请求
        Now += TimeSpan.FromMinutes(16);
        await coordinator.RefreshAsync(provider, OnSnapshot, CancellationToken.None);
        Assert.Equal(2, provider.Calls);            // 退避解除
        Assert.Equal(ProviderState.Live, received[^1].State);
    }

    [Fact]
    public async Task Backoff_on_one_provider_does_not_block_the_other()
    {
        var rateLimited = new FakeProvider("glm", _ => Task.FromResult(RateLimited("glm", TimeSpan.FromMinutes(10))));
        var healthy = new FakeProvider("codex", _ => Task.FromResult(LiveSnap("codex")));
        var coordinator = new RefreshCoordinator([rateLimited, healthy], () => Now);
        Task OnSnapshot(string id, ProviderSnapshot s) => Task.CompletedTask;

        await coordinator.RefreshAllAsync(OnSnapshot, CancellationToken.None);  // glm 进入退避
        Now += TimeSpan.FromSeconds(5);
        await coordinator.RefreshAllAsync(OnSnapshot, CancellationToken.None);

        Assert.Equal(1, rateLimited.Calls);         // glm 被跳过
        Assert.Equal(2, healthy.Calls);             // codex 正常刷新
    }
}

/// <summary>真并发合并与异常隔离（RefreshCoordinator 的登记原子性与平台级故障兜底）。</summary>
public class CoordinatorConcurrencyAndIsolationTests
{
    private DateTimeOffset Now { get; set; } = new(2026, 10, 8, 18, 0, 0, TimeSpan.FromHours(8));

    private sealed class FakeProvider(string id, Func<int, Task<ProviderSnapshot>> impl) : IQuotaProvider
    {
        public string ProviderId { get; } = id;
        public int Calls { get; private set; }

        public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return await impl(Calls);
        }
    }

    private static ProviderSnapshot LiveSnap(string id, double used = 30) => new()
    {
        ProviderId = id,
        Source = "test",
        Windows = [new QuotaWindow { Id = $"{id}-5h", Kind = WindowKind.FiveHour, UsedPercent = used }],
        ObservedAt = DateTimeOffset.Now,
        LastAttemptAt = DateTimeOffset.Now,
        State = ProviderState.Live,
    };

    [Fact]
    public async Task Truly_concurrent_callers_share_single_request()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider("glm", async _ =>
        {
            entered.TrySetResult();
            await release.Task; // 提供器挂起：并发窗口完全打开
            return LiveSnap("glm");
        });
        var coordinator = new RefreshCoordinator([provider], () => Now);
        Task OnSnapshot(string id, ProviderSnapshot s) => Task.CompletedTask;

        // 8 个线程同时请求；修复前"先启动后登记"窗口内会各自发起。
        var returned = new Task[8];
        var callers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            returned[i] = coordinator.RefreshAsync(provider, OnSnapshot, CancellationToken.None);
        })).ToArray();
        await Task.WhenAny(entered.Task, Task.Delay(2000));
        await Task.Delay(200); // 给其余调用者足够时间进入（它们此刻必须被合并）

        release.TrySetResult();
        await Task.WhenAll(callers);
        Assert.Equal(1, provider.Calls);                        // 只发了一次请求
        Assert.All(returned, t => Assert.Same(returned[0], t)); // 全部复用同一在途任务
    }

    [Fact]
    public async Task Sequential_refreshes_each_issue_new_request()
    {
        var provider = new FakeProvider("glm", _ => Task.FromResult(LiveSnap("glm")));
        var coordinator = new RefreshCoordinator([provider], () => Now);
        Task OnSnapshot(string id, ProviderSnapshot s) => Task.CompletedTask;

        await coordinator.RefreshAsync(provider, OnSnapshot, CancellationToken.None);
        await coordinator.RefreshAsync(provider, OnSnapshot, CancellationToken.None);
        Assert.Equal(2, provider.Calls); // 在途登记完成后必须释放，否则永远合并在旧任务上
    }

    [Fact]
    public async Task Provider_parse_exception_is_isolated_and_platform_recovers()
    {
        // 首轮返回坏 JSON，次轮正常 → 平台失败被隔离且可自行恢复。
        var failing = new FakeProvider("glm", call =>
            call == 1 ? throw new System.Text.Json.JsonException("bad json") : Task.FromResult(LiveSnap("glm")));
        var healthy = new FakeProvider("codex", _ => Task.FromResult(LiveSnap("codex")));
        var coordinator = new RefreshCoordinator([failing, healthy], () => Now);
        var received = new Dictionary<string, ProviderSnapshot>();
        Task OnSnapshot(string id, ProviderSnapshot s)
        {
            received[id] = s;
            return Task.CompletedTask;
        }

        await coordinator.RefreshAllAsync(OnSnapshot, CancellationToken.None);
        Assert.Equal(ProviderState.Unavailable, received["glm"].State); // 异常转为平台失败
        Assert.Equal("BadResponse", received["glm"].ErrorCode);
        Assert.Equal(ProviderState.Live, received["codex"].State);      // 另一平台不受影响

        // 下一轮恢复
        await coordinator.RefreshAllAsync(OnSnapshot, CancellationToken.None);
        Assert.Equal(ProviderState.Live, received["glm"].State);
    }

    [Fact]
    public async Task Unexpected_provider_exception_becomes_internal_error_snapshot()
    {
        var failing = new FakeProvider("glm", _ => throw new InvalidOperationException("boom"));
        var coordinator = new RefreshCoordinator([failing], () => Now);
        ProviderSnapshot? got = null;
        await coordinator.RefreshAsync(failing, (id, s) => { got = s; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(ProviderState.Unavailable, got!.State);
        Assert.Equal("InternalError", got.ErrorCode);
        Assert.NotEqual("mock", got.Source); // 失败快照不得让真实平台闪现"模拟"标识
    }

    [Fact]
    public async Task Timeout_snapshot_has_no_mock_source()
    {
        // 遵守取消令牌的慢提供器：预算（80ms）先耗尽 → 协调器归类为 Timeout。
        var slow = new SlowProvider("codex", TimeSpan.FromSeconds(2));
        var coordinator = new RefreshCoordinator([slow], () => Now) { Timeout = TimeSpan.FromMilliseconds(80) };
        ProviderSnapshot? got = null;
        await coordinator.RefreshAsync(slow, (id, s) => { got = s; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("Timeout", got!.ErrorCode);
        Assert.NotEqual("mock", got.Source);
    }

    private sealed class SlowProvider(string id, TimeSpan delay) : IQuotaProvider
    {
        public string ProviderId { get; } = id;

        public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return LiveSnap(ProviderId);
        }
    }
}

/// <summary>重置点触发一次刷新（去重）与通知去重。</summary>
public class ResetAndNotifyTests
{
    private DateTimeOffset _now = new(2026, 10, 8, 18, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task Reset_point_triggers_exactly_one_refresh()
    {
        var settings = new AppSettings();
        DateTimeOffset clock() => _now;

        // 提供器每次返回一个 5h 窗口：ResetsAt = 当前时钟 + 5 秒（跨过即到重置点）。
        var calls = 0;
        var provider = new CountingProvider("glm", () =>
        {
            calls++;
            return new ProviderSnapshot
            {
                ProviderId = "glm",
                Source = "test",
                Windows = [new QuotaWindow
                {
                    Id = "glm-5h",
                    Kind = WindowKind.FiveHour,
                    UsedPercent = 28,
                    ResetsAt = clock().AddSeconds(5),
                }],
                ObservedAt = clock(),
                LastAttemptAt = clock(),
                State = ProviderState.Live,
            };
        });

        var vm = new MainViewModel(settings, clock, providerFactory:
            (id, source, c, dir) => id == "glm" ? provider : new MockQuotaProvider(id, c));

        await vm.RefreshAsync(manual: false);
        Assert.Equal(1, calls); // 首次刷新 glm 1 次（codex 是另一个 mock，不计数）

        // 时钟跨过重置点后，多次秒级 tick 只应触发一次刷新
        _now += TimeSpan.FromSeconds(6);
        for (var i = 0; i < 5; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            await vm.TickSecondAsync();
        }
        Assert.Equal(2, calls); // 恰好 +1，不因每秒 tick 重复请求
    }

    private sealed class CountingProvider(string id, Func<ProviderSnapshot> impl) : IQuotaProvider
    {
        public string ProviderId { get; } = id;
        public Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(impl());
    }

    [Fact]
    public void Low_quota_notification_dedupes_by_cycle_and_band()
    {
        var settings = new AppSettings { WarnRemainingPercent = 20, CriticalRemainingPercent = 10 };
        var notifier = new LowQuotaNotifier { Enabled = true };

        ProviderSnapshot Snap(double used, int resetYear = 2026) => new()
        {
            ProviderId = "glm",
            Source = "test",
            Windows = [new QuotaWindow
            {
                Id = "glm-5h",
                Kind = WindowKind.FiveHour,
                UsedPercent = used,
                ResetsAt = new DateTimeOffset(resetYear, 10, 9, 0, 0, 0, TimeSpan.FromHours(8)),
            }],
            ObservedAt = DateTimeOffset.Now,
            LastAttemptAt = DateTimeOffset.Now,
            State = ProviderState.Live,
        };

        // 首次跌破警告线：1 条
        Assert.Single(notifier.Evaluate("glm", Snap(85), settings));
        // 同周期同档位：0 条
        Assert.Empty(notifier.Evaluate("glm", Snap(85), settings));
        // 升级到严重档：1 条
        Assert.Single(notifier.Evaluate("glm", Snap(93), settings));
        Assert.Empty(notifier.Evaluate("glm", Snap(93), settings));
        // 新的重置周期：再次提醒
        Assert.Single(notifier.Evaluate("glm", Snap(93, resetYear: 2027), settings));
        // 关闭后：0 条
        notifier.Enabled = false;
        Assert.Empty(notifier.Evaluate("glm", Snap(93, resetYear: 2028), settings));
    }
}
