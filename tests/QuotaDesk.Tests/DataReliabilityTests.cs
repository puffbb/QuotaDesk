using QuotaDesk.Models;
using QuotaDesk.Providers;
using QuotaDesk.Services;
using QuotaDesk.ViewModels;
using Xunit;

namespace QuotaDesk.Tests;

/// <summary>
/// 数据可信度：刷新失败后旧数据标记必须在多次秒级 tick 中保持；
/// 刷新期间切换配置时旧请求的结果不得写入新视图。
/// </summary>
public class StaleMarkerTests
{
    private DateTimeOffset _now = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8));

    private ProviderViewModel MakeVm() => new("glm", "mock", () => _now, "unused-dir",
        (id, source, clock, dir) => new MockQuotaProvider(id, clock));

    private static ProviderSnapshot Live(double used = 30, DateTimeOffset? observedAt = null) => new()
    {
        ProviderId = "glm",
        Source = "test",
        Windows = [new QuotaWindow { Id = "glm-5h", Kind = WindowKind.FiveHour, UsedPercent = used }],
        ObservedAt = observedAt ?? DateTimeOffset.Now,
        LastAttemptAt = DateTimeOffset.Now,
        State = ProviderState.Live,
    };

    private static ProviderSnapshot Failed(string code = "NetworkError", string? accountKey = null) => new()
    {
        ProviderId = "glm",
        Source = "test",
        Windows = [],
        ObservedAt = DateTimeOffset.Now,
        LastAttemptAt = DateTimeOffset.Now,
        State = ProviderState.Unavailable,
        ErrorCode = code,
        AccountKey = accountKey,
    };

    [Fact]
    public void Stale_marker_survives_many_second_ticks()
    {
        var settings = new AppSettings();
        var vm = MakeVm();
        var observed = _now.AddMinutes(-12);
        vm.ApplySnapshot(Live(30, observed), settings, _now);
        Assert.False(vm.IsStale);
        Assert.Equal("70%", vm.Rows[0].PercentText);
        Assert.Null(vm.Rows[0].StatusText);

        // 刷新失败：保留旧值并标记
        vm.ApplySnapshot(Failed(), settings, _now);
        Assert.True(vm.IsStale);
        Assert.Equal("70%", vm.Rows[0].PercentText);       // 旧值仍在
        Assert.Contains("旧数据", vm.Rows[0].StatusText);   // 行级标记
        Assert.Equal("旧", vm.Rows[0].CompactSuffix);
        Assert.Equal("网络错误", vm.ErrorText);             // 失败原因可见
        Assert.Contains("（旧）", vm.UpdateText);           // 最近成功时间（旧）
        Assert.Equal("旧数据", vm.RowIssueText);

        // 30 个秒级 tick 后标记不消失（修复前会被 _lastGood 的 Live 重映射抹掉）
        for (var i = 0; i < 30; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            vm.TickCountdown(settings, _now);
            Assert.True(vm.IsStale, $"tick {i}: 旧数据状态丢失");
            Assert.Contains("旧数据", vm.Rows[0].StatusText);
            Assert.Equal("旧", vm.Rows[0].CompactSuffix);
        }
        Assert.Equal("网络错误", vm.ErrorText);
        Assert.Contains("（旧）", vm.UpdateText);
        Assert.Equal("旧数据", vm.RowIssueText);

        // 新成功结果到来：标记清除
        vm.ApplySnapshot(Live(40), settings, _now);
        Assert.False(vm.IsStale);
        Assert.Null(vm.Rows[0].StatusText);
        Assert.Equal("60%", vm.Rows[0].PercentText);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public void Failed_refresh_without_previous_data_shows_no_data_not_zero()
    {
        var settings = new AppSettings();
        var vm = MakeVm();
        vm.ApplySnapshot(Failed(), settings, _now); // 从未成功过
        Assert.False(vm.IsStale);
        Assert.Equal("—", vm.Rows[0].PercentText);  // 不补 0、不虚构
        Assert.False(vm.Rows[0].HasData);
        Assert.Equal("暂无数据", vm.Rows[0].StatusText);
        Assert.Equal("网络错误", vm.ErrorText);
    }

    [Fact]
    public void Reset_passed_while_stale_shows_waiting_not_recovered_value()
    {
        var settings = new AppSettings();
        var vm = MakeVm();
        var resetAt = _now.AddSeconds(5);
        var snap = new ProviderSnapshot
        {
            ProviderId = "glm",
            Source = "test",
            Windows = [new QuotaWindow
            {
                Id = "glm-5h", Kind = WindowKind.FiveHour, UsedPercent = 90, ResetsAt = resetAt,
            }],
            ObservedAt = _now,
            LastAttemptAt = _now,
            State = ProviderState.Live,
        };
        vm.ApplySnapshot(snap, settings, _now);
        vm.ApplySnapshot(Failed(), settings, _now);
        _now = resetAt + TimeSpan.FromSeconds(10); // 重置点已过但刷新仍失败
        for (var i = 0; i < 5; i++)
        {
            vm.TickCountdown(settings, _now);
            _now += TimeSpan.FromSeconds(1);
        }
        Assert.Equal("等待更新", vm.Rows[0].RightText); // 不虚构恢复
        Assert.Contains("旧数据", vm.Rows[0].StatusText);
    }

    [Fact]
    public void Taskbar_line_reads_fresh_state_when_rows_update()
    {
        // 回归：行 PropertyChanged 同步触发挂件 SetLine 读取 ErrorText/Source；
        // 若这些字段在行更新之后才赋值，挂件会一直显示上一轮状态。
        var settings = new AppSettings();
        var vm = MakeVm();
        string? errorSeenByLine = null;
        string? sourceSeenByLine = null;
        vm.Rows[0].PropertyChanged += (_, _) =>
        {
            errorSeenByLine = vm.ErrorText;
            sourceSeenByLine = vm.SourceLabel;
        };

        vm.ApplySnapshot(Failed(), settings, _now); // 从未成功过 → 直接失败
        Assert.Equal("网络错误", errorSeenByLine);   // 行更新时错误文本必须已是新值
        Assert.Equal("test", sourceSeenByLine);      // 来源用失败快照自带的原始来源
        Assert.False(vm.IsAwaitingFirstData);
    }

    [Fact]
    public void Provider_declared_stale_is_marked_and_survives_ticks()
    {
        var settings = new AppSettings();
        var vm = MakeVm();
        var snap = new ProviderSnapshot
        {
            ProviderId = "glm",
            Source = "mock",
            Windows = [new QuotaWindow { Id = "glm-5h", Kind = WindowKind.FiveHour, UsedPercent = 28 }],
            ObservedAt = _now.AddMinutes(-37),
            LastAttemptAt = _now,
            State = ProviderState.Stale, // 数据源自行声明旧数据
        };
        vm.ApplySnapshot(snap, settings, _now);
        Assert.True(vm.IsStale);
        Assert.Contains("旧数据", vm.Rows[0].StatusText);
        Assert.Contains("（旧）", vm.UpdateText);
        for (var i = 0; i < 10; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            vm.TickCountdown(settings, _now);
            Assert.True(vm.IsStale);
            Assert.Equal("旧", vm.Rows[0].CompactSuffix);
        }
        // 恢复实时后标记清除
        vm.ApplySnapshot(Live(50), settings, _now);
        Assert.False(vm.IsStale);
        Assert.Null(vm.Rows[0].CompactSuffix);
    }

    [Fact]
    public void Source_label_and_detail_line_describe_origin()
    {
        var settings = new AppSettings();
        var vm = MakeVm();
        Assert.Equal("未获取", vm.SourceLabel);

        vm.ApplySnapshot(new ProviderSnapshot
        {
            ProviderId = "glm", Source = "glm-monitor", State = ProviderState.Live,
            Windows = [new QuotaWindow { Id = "w", Kind = WindowKind.FiveHour, UsedPercent = 50 }],
            ObservedAt = _now, LastAttemptAt = _now,
        }, settings, _now);
        Assert.Equal("智谱用量接口", vm.SourceLabel);
        Assert.Contains("更新于", vm.DetailLine);
        Assert.Contains("来源：智谱用量接口", vm.DetailLine);

        // 刷新失败：详情行保留最近成功时间与来源，错误文本单独可见
        vm.ApplySnapshot(Failed(), settings, _now);
        Assert.Contains("来源：智谱用量接口", vm.DetailLine);
        Assert.Equal("网络错误", vm.ErrorText);
    }
}

/// <summary>任务栏数字行的紧凑状态标记与悬停提示（TaskbarText 扩展）。</summary>
public class TaskbarStateMarkerTests
{
    [Theory]
    [InlineData(false, false, true, false, null)]     // 正常实时
    [InlineData(false, true, true, false, "旧")]      // 旧数据
    [InlineData(false, false, false, false, "错")]    // 失败且无数据
    [InlineData(false, true, false, true, null)]      // 首次获取中不标"错"
    [InlineData(true, false, true, false, "模")]      // 模拟来源
    [InlineData(true, true, true, false, "旧")]       // 旧优先于模
    public void Marker_priority(bool isMock, bool isStale, bool hasData, bool awaiting, string? expected)
    {
        Assert.Equal(expected, TaskbarText.StateMarker(isMock, isStale, hasData, awaiting));
    }

    [Fact]
    public void Tooltip_joins_available_lines_only()
    {
        var tip = TaskbarText.WidgetLineToolTip("GLM", "智谱用量接口", "更新于 09:50（旧）", "网络错误");
        Assert.Equal("GLM · 智谱用量接口\n更新于 09:50（旧）\n网络错误", tip);

        var minimal = TaskbarText.WidgetLineToolTip("Codex", null, null, null);
        Assert.Equal("Codex", minimal);
    }
}

/// <summary>刷新期间切换数据源：旧配置的请求结果不得写入新视图（配置代次隔离）。</summary>
public class ReconfigureGenerationTests
{
    private DateTimeOffset _now = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8));

    /// <summary>忽略取消令牌、由测试控制完成时机的提供器（模拟已在途的旧请求）。</summary>
    private sealed class GateProvider(string id, TaskCompletionSource<ProviderSnapshot> gate) : IQuotaProvider
    {
        public string ProviderId { get; } = id;
        public Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => gate.Task;
    }

    private static ProviderSnapshot Live(string id, double used) => new()
    {
        ProviderId = id,
        Source = "test",
        Windows = [new QuotaWindow { Id = $"{id}-5h", Kind = WindowKind.FiveHour, UsedPercent = used }],
        ObservedAt = DateTimeOffset.Now,
        LastAttemptAt = DateTimeOffset.Now,
        State = ProviderState.Live,
    };

    [Fact]
    public async Task Old_inflight_result_cannot_override_new_configuration()
    {
        var settings = new AppSettings();
        var oldGate = new TaskCompletionSource<ProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        IQuotaProvider current = new GateProvider("glm", oldGate);

        var vm = new MainViewModel(settings, () => _now, providerFactory:
            (id, source, clock, dir) => current);

        _ = vm.RefreshAsync(manual: false); // 旧配置的请求发出并挂在 gate 上（在途，不 await）
        await Task.Delay(50);               // 让它完成登记

        // 切换配置：新提供器立即返回 77%
        var newSnap = Live("glm", 77);
        current = new DelegateProvider("glm", _ => Task.FromResult(newSnap));
        vm.ReconfigureProviders();

        // 等新配置的数据落地
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (vm.Find("glm")!.Rows[0].PercentText != "23%" && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal("23%", vm.Find("glm")!.Rows[0].PercentText); // 剩余 100-77

        // 此刻才放行旧请求：其结果不得覆盖新配置的视图
        oldGate.TrySetResult(Live("glm", 10));
        await Task.Delay(100);
        Assert.Equal("23%", vm.Find("glm")!.Rows[0].PercentText);
        Assert.False(vm.IsRefreshing); // 新配置的刷新正常收尾
    }

    private sealed class DelegateProvider(string id, Func<CancellationToken, Task<ProviderSnapshot>> impl) : IQuotaProvider
    {
        public string ProviderId { get; } = id;
        public Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => impl(cancellationToken);
    }
}
