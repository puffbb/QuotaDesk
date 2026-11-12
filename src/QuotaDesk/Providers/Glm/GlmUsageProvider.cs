using System.Net.Http;
using QuotaDesk.Models;

namespace QuotaDesk.Providers.Glm;

/// <summary>
/// 智谱国内 Coding Plan 真实额度提供器：从 DPAPI 保护的本地存储读取 key，
/// 只向 open.bigmodel.cn 发送凭据。总时间预算默认 18s；瞬时网络错误最多重试 2 次；
/// 401/403/429 不重试。key 变化通过 AccountKey 隔离旧账号缓存。
/// </summary>
public sealed class GlmUsageProvider : IQuotaProvider, IDisposable
{
    public const string Endpoint = "https://open.bigmodel.cn/api/monitor/usage/quota/limit";
    public const int TransientRetryLimit = 2;

    private readonly string _settingsDir;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HttpClient _http;
    private readonly int _budgetSeconds;

    public string ProviderId => "glm";

    public GlmUsageProvider(string settingsDir, Func<DateTimeOffset>? clock = null,
        HttpMessageHandler? handler = null, int budgetSeconds = 18)
    {
        _settingsDir = settingsDir;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _budgetSeconds = budgetSeconds;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = _clock();
        // 未配置与无法解密/读取区分开：后者保留原文件，提示用户在设置中重设。
        var keyRead = GlmKeyStore.TryLoad(_settingsDir);
        switch (keyRead.Status)
        {
            case GlmKeyStatus.NotConfigured:
                return Fail(now, "NoCredentials", accountKey: null);
            case GlmKeyStatus.Unreadable:
                return Fail(now, "KeyFileUnreadable", accountKey: null);
        }

        var key = keyRead.Key!;
        var accountKey = GlmKeyStore.AccountKeyOf(key);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(_budgetSeconds));

        for (var attempt = 0; attempt <= TransientRetryLimit; attempt++)
        {
            if (attempt > 0)
            {
                try { await Task.Delay(600 * attempt, budget.Token); }
                catch (OperationCanceledException) { break; }
            }

            using var request = BuildRequest(key);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, budget.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpRequestException)
            {
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status == 200)
                {
                    var body = await response.Content.ReadAsStringAsync(budget.Token);
                    return ParseSafe(body, now, accountKey);
                }
                if (status == 401 || status == 403)
                    return Fail(_clock(), "Unauthorized", accountKey);
                if (status == 429)
                    return Fail(_clock(), "RateLimited", accountKey, ParseRetryAfter(response));
                return Fail(_clock(), $"HTTP{status}", accountKey);
            }
        }

        return Fail(_clock(), budget.IsCancellationRequested ? "Timeout" : "NetworkError", accountKey);
    }

    /// <summary>响应解析失败（非 JSON/字段异常）转为平台失败状态，不带上敏感内容，也不炸掉整轮刷新。</summary>
    private ProviderSnapshot ParseSafe(string body, DateTimeOffset now, string accountKey)
    {
        try
        {
            return GlmUsageParser.Parse(body, now, accountKey);
        }
        catch (System.Text.Json.JsonException)
        {
            return Fail(now, "BadResponse", accountKey);
        }
    }

    /// <summary>Retry-After：秒数或 HTTP 日期，均换算为距现在的时长；无效则 null。</summary>
    internal TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } delta) return delta;
        if (ra.Date is { } date)
        {
            var until = date - _clock();
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }
        return null;
    }

    private HttpRequestMessage BuildRequest(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "QuotaDesk/0.1");
        return request;
    }

    private static ProviderSnapshot Fail(DateTimeOffset now, string errorCode, string? accountKey, TimeSpan? retryAfter = null) => new()
    {
        ProviderId = "glm",
        Source = GlmUsageParser.Source,
        Windows = [],
        ObservedAt = now,
        LastAttemptAt = now,
        State = ProviderState.Unavailable,
        ErrorCode = errorCode,
        AccountKey = accountKey,
        RetryAfter = retryAfter,
    };

    public void Dispose() => _http.Dispose();
}
