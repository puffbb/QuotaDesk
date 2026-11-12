using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using QuotaDesk.Models;
using QuotaDesk.Providers.Codex;
using Xunit;

namespace QuotaDesk.Tests;

/// <summary>阶段 3：Codex 提供器行为（HTTP 语义、重试、错误分类）。HTTP 层用假 handler。</summary>
public class CodexUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 16, 0, 0, TimeSpan.FromHours(8));

    private static string Fixture(string name) => File.ReadAllText(FixturesHelper.Path(name));

    /// <summary>按脚本回放响应/异常的假 handler，记录收到的请求。</summary>
    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        public List<(string? Auth, string? Account)> Requests { get; } = [];
        public int Attempts => Requests.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("Authorization", out var auth);
            request.Headers.TryGetValues("ChatGPT-Account-Id", out var account);
            Requests.Add((auth?.FirstOrDefault(), account?.FirstOrDefault()));
            var step = steps[Math.Min(Requests.Count - 1, steps.Length - 1)];
            return Task.FromResult(step(request));
        }

        public static HttpResponseMessage Json(int status, string body) => new()
        {
            StatusCode = (HttpStatusCode)status,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        public static HttpResponseMessage Error(Exception ex) => throw ex;
    }

    private static CodexAuthResult FakeAuth() => new()
    {
        Status = CodexAuthStatus.Ok,
        AccessToken = "TEST_TOKEN",
        AccountId = "acct-1",
    };

    /// <summary>用临时 CODEX_HOME 提供固定凭据。</summary>
    private (CodexUsageProvider Provider, FakeHandler Handler, string HomeDir) MakeProvider(
        FakeHandler handler, string budgetSeconds = "30")
    {
        var home = Path.Combine(Path.GetTempPath(), "qd-codex-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "auth.json"),
            """{"tokens": {"access_token": "TEST_TOKEN", "account_id": "acct-1"}}""");
        var provider = new CodexUsageProvider(
            codexHome: home,
            clock: () => Now,
            handler: handler,
            budgetSeconds: int.Parse(budgetSeconds));
        return (provider, handler, home);
    }

    [Fact]
    public async Task Success_200_parses_snapshot_with_account_key()
    {
        var (provider, handler, home) = MakeProvider(new FakeHandler(
            _ => FakeHandler.Json(200, Fixture("codex-usage-real-shape.json"))));
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(ProviderState.Live, snap.State);
            Assert.Equal("acct-1", snap.AccountKey);
            Assert.Equal(2, snap.Windows.Count);
            Assert.Equal(42, snap.Windows[0].UsedPercent);
            // 请求头核对：Bearer + Account-Id
            var (auth, account) = handler.Requests.Single();
            Assert.Equal("Bearer TEST_TOKEN", auth);
            Assert.Equal("acct-1", account);
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Fact]
    public async Task Auth_failure_maps_without_retry()
    {
        var (provider, handler, home) = MakeProvider(new FakeHandler(
            _ => FakeHandler.Json(401, """{"error": "unauthorized"}""")));
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(ProviderState.Unavailable, snap.State);
            Assert.Equal("Unauthorized", snap.ErrorCode);
            Assert.Equal(1, handler.Attempts); // 401 不循环重试
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Fact]
    public async Task Rate_limited_maps_without_retry()
    {
        var (provider, handler, home) = MakeProvider(new FakeHandler(
            _ => FakeHandler.Json(429, """{"error": "rate"}""")));
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal("RateLimited", snap.ErrorCode);
            Assert.Equal(1, handler.Attempts);
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Fact]
    public async Task Transient_network_errors_retry_then_succeed()
    {
        var call = 0;
        var (provider, handler, home) = MakeProvider(new FakeHandler(
            _ => ++call == 1 ? throw new HttpRequestException("connection reset") : FakeHandler.Json(200, Fixture("codex-usage-real-shape.json"))));
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(ProviderState.Live, snap.State);
            Assert.Equal(2, handler.Attempts);
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Fact]
    public async Task Persistent_network_failure_maps_to_network_error()
    {
        var (provider, handler, home) = MakeProvider(new FakeHandler(
            _ => throw new HttpRequestException("proxy down")));
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(ProviderState.Unavailable, snap.State);
            Assert.Equal("NetworkError", snap.ErrorCode);
            Assert.Equal(CodexUsageProvider.TransientRetryLimit + 1, handler.Attempts);
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Fact]
    public async Task Other_http_status_surfaces_code_sanitized()
    {
        var (provider, handler, home) = MakeProvider(new FakeHandler(
            _ => FakeHandler.Json(503, "service unavailable")));
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal("HTTP503", snap.ErrorCode);
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Fact]
    public async Task Missing_auth_file_reports_no_credentials_without_http()
    {
        var emptyHome = Path.Combine(Path.GetTempPath(), "qd-codex-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyHome);
        var handler = new FakeHandler(_ => FakeHandler.Json(200, "{}"));
        var provider = new CodexUsageProvider(codexHome: emptyHome, clock: () => Now, handler: handler);
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal("NoCredentials", snap.ErrorCode);
            Assert.Equal(ProviderState.Unavailable, snap.State);
            Assert.Equal(0, handler.Attempts); // 没有凭据就不发请求
        }
        finally { try { Directory.Delete(emptyHome, true); } catch { } }
    }

    /// <summary>挂起直到被取消的 handler，用于验证取消传播。</summary>
    private sealed class DelayingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(200, cancellationToken);
            return FakeHandler.Json(200, "{}");
        }
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var home = Path.Combine(Path.GetTempPath(), "qd-codex-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "auth.json"),
            """{"tokens": {"access_token": "TEST_TOKEN"}}""");
        var provider = new CodexUsageProvider(codexHome: home, clock: () => Now, handler: new DelayingHandler());
        try
        {
            using var cts = new CancellationTokenSource(50);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provider.GetSnapshotAsync(cts.Token));
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }
}
