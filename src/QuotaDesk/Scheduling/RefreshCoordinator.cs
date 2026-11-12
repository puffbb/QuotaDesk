using System.Text.Json;
using QuotaDesk.Models;
using QuotaDesk.Providers;

namespace QuotaDesk.Scheduling;

/// <summary>
/// 每平台独立的异步刷新协调：
/// - 同平台请求合并：登记与启动在同一把锁内完成，真正并发的后到者必然复用先到者的任务，不重复发起；
/// - 旧请求结果不覆盖新结果（序号校验，合并机制下的兜底）；
/// - 429 退避：快照带 RetryAfter 时，该平台在退避窗口内被跳过（上限 BackoffCap）；
/// - 总时间预算（Timeout）内未完成的按超时处理，一个平台失败不影响另一个；
/// - 解析异常、凭据 IO 等意外错误隔离为该平台的 Unavailable 快照（不炸掉整轮刷新，不含敏感内容）。
/// </summary>
public sealed class RefreshCoordinator
{
    private readonly IQuotaProvider[] _providers;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _sequence = new();
    private readonly Dictionary<string, Task> _inFlight = new();
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new();

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(18);

    /// <summary>429 Retry-After 退避上限，防止服务给出超长退避把平台永久停掉。</summary>
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromMinutes(15);

    public RefreshCoordinator(IEnumerable<IQuotaProvider> providers, Func<DateTimeOffset>? clock = null)
    {
        _providers = providers.ToArray();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public Task RefreshAllAsync(Func<string, ProviderSnapshot, Task> onSnapshot, CancellationToken ct)
        => Task.WhenAll(_providers.Select(p => RefreshAsync(p, onSnapshot, ct)));

    public Task RefreshAsync(IQuotaProvider provider, Func<string, ProviderSnapshot, Task> onSnapshot, CancellationToken ct)
    {
        var now = _clock();
        Task work;
        lock (_gate)
        {
            if (_inFlight.TryGetValue(provider.ProviderId, out var running) && !running.IsCompleted)
                return running; // 同平台请求合并
            if (_notBefore.TryGetValue(provider.ProviderId, out var notBefore) && now < notBefore)
                return Task.CompletedTask; // 退避窗口内跳过
            // 占位与启动在同一临界区：并发的后到者只可能看到已登记的在途任务，不会各自发起。
            work = RunAsync(provider, onSnapshot, ct);
            _inFlight[provider.ProviderId] = work;
        }
        ReleaseWhenDone(provider.ProviderId, work);
        return work;
    }

    /// <summary>完成后清掉自己登记的在途项（只清自己，避免误删已合并/已覆盖的条目）。</summary>
    private void ReleaseWhenDone(string providerId, Task work)
    {
        work.ContinueWith((_, state) =>
        {
            var (task, id) = ((Task, string))state!;
            lock (_gate)
            {
                if (_inFlight.TryGetValue(id, out var current) && ReferenceEquals(current, task))
                    _inFlight.Remove(id);
            }
        }, (work, providerId), CancellationToken.None,
           TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunAsync(IQuotaProvider provider, Func<string, ProviderSnapshot, Task> onSnapshot, CancellationToken ct)
    {
        int seq;
        lock (_gate)
        {
            seq = _sequence.TryGetValue(provider.ProviderId, out var s) ? s + 1 : 1;
            _sequence[provider.ProviderId] = seq;
        }

        ProviderSnapshot snapshot;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Timeout);
            snapshot = await provider.GetSnapshotAsync(budget.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 调用方取消，原样上抛
        }
        catch (OperationCanceledException)
        {
            snapshot = Unavailable(provider.ProviderId, "Timeout");
        }
        catch (JsonException)
        {
            // 响应不是合法 JSON：平台级失败，不带上敏感内容，另一平台与后续刷新不受影响。
            snapshot = Unavailable(provider.ProviderId, "BadResponse");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 意外异常（字段异常、临时 IO、提供器内部错误）：同样隔离为平台失败。
            snapshot = Unavailable(provider.ProviderId, "InternalError");
        }

        if (snapshot.RetryAfter is { } backoff)
        {
            lock (_gate)
            {
                var capped = backoff > BackoffCap ? BackoffCap : backoff;
                _notBefore[provider.ProviderId] = _clock() + capped;
            }
        }

        lock (_gate)
        {
            // 序号校验：理论上合并后不会出现乱序，保留作为兜底。
            if (_sequence[provider.ProviderId] != seq) return;
        }
        await onSnapshot(provider.ProviderId, snapshot).ConfigureAwait(true);
    }

    private ProviderSnapshot Unavailable(string providerId, string errorCode) => new()
    {
        ProviderId = providerId,
        // 本轮没有任何来源（超时/异常）。不能落默认值 "mock"——那会让真实平台闪现"模拟"徽标。
        Source = "",
        Windows = [],
        ObservedAt = _clock(),
        LastAttemptAt = _clock(),
        State = ProviderState.Unavailable,
        ErrorCode = errorCode,
    };
}
