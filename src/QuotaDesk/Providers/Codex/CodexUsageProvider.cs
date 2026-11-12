using System.Net.Http;
using QuotaDesk.Models;

namespace QuotaDesk.Providers.Codex;

/// <summary>
/// 真实 Codex 额度提供器：读取本机 OAuth 凭据（只读），请求客户端用量接口。
/// - 使用系统代理与正常 HTTPS 证书校验（HttpClient 默认走系统代理）。
/// - 总时间预算默认 18s；瞬时网络错误最多重试 2 次（600/1200ms 退避）；401/403/429 不重试。
/// - 不会刷新/改写登录凭据，不执行任何登录命令。
/// </summary>
public sealed class CodexUsageProvider : IQuotaProvider, IDisposable
{
    public const int TransientRetryLimit = 2;

    private readonly string? _codexHome;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HttpClient _http;
    private readonly int _budgetSeconds;

    public string ProviderId => "codex";

    public CodexUsageProvider(string? codexHome = null, Func<DateTimeOffset>? clock = null,
        HttpMessageHandler? handler = null, int budgetSeconds = 18)
    {
        _codexHome = codexHome;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _budgetSeconds = budgetSeconds;
        // HttpClient 默认 handler 使用系统代理（WebRequest.DefaultWebProxy），保持证书校验。
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // 超时由总预算统一控制
        };
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var auth = CodexAuthReader.Read(CodexAuthReader.ResolveAuthPath(_codexHome));
        if (!auth.IsOk)
        {
            return Fail(_clock(), auth.Status switch
            {
                CodexAuthStatus.Unreadable => "AuthFileUnreadable",
                _ => "NoCredentials",
            }, accountKey: null);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(_budgetSeconds));

        Exception? lastError = null;
        for (var attempt = 0; attempt <= TransientRetryLimit; attempt++)
        {
            if (attempt > 0)
            {
                try
                {
                    await Task.Delay(600 * attempt, budget.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            using var request = BuildRequest(auth);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, budget.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // 调用方取消：原样上抛
            }
            catch (OperationCanceledException)
            {
                break; // 总预算耗尽 → 超时
            }
            catch (HttpRequestException ex)
            {
                // 瞬时网络错误：有限重试（连接被拒/重置/DNS 等）。代理断开也在此类。
                lastError = ex;
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status == 200)
                {
                    var body = await response.Content.ReadAsStringAsync(budget.Token);
                    // 响应不是合法 JSON/字段异常 → 平台失败状态（BadResponse），不影响另一平台与后续刷新。
                    try
                    {
                        return CodexUsageParser.Parse(body, _clock(), accountKey: auth.AccountId);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        return Fail(_clock(), "BadResponse", auth.AccountId);
                    }
                }
                // 认证失败与限流不做重试，直接进入对应状态。
                if (status == 401 || status == 403)
                    return Fail(_clock(), "Unauthorized", auth.AccountId);
                if (status == 429)
                    return Fail(_clock(), "RateLimited", auth.AccountId, ParseRetryAfter(response));
                return Fail(_clock(), $"HTTP{status}", auth.AccountId);
            }
        }

        // 预算耗尽视为超时；其余为网络错误。
        return Fail(_clock(), budget.IsCancellationRequested ? "Timeout" : "NetworkError", auth.AccountId);
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

    private HttpRequestMessage BuildRequest(CodexAuthResult auth)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + auth.AccessToken);
        // 空的 account 头不发送（头值无意义）。
        if (!string.IsNullOrEmpty(auth.AccountId))
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "QuotaDesk/0.1");
        return request;
    }

    private static ProviderSnapshot Fail(DateTimeOffset now, string errorCode, string? accountKey, TimeSpan? retryAfter = null) => new()
    {
        ProviderId = "codex",
        Source = CodexUsageParser.Source,
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
