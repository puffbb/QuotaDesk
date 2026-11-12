using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using QuotaDesk.Models;
using QuotaDesk.Providers.Glm;
using Xunit;

namespace QuotaDesk.Tests;

/// <summary>阶段 4：GlmKeyStore（DPAPI）与 GlmUsageProvider 行为测试。</summary>
public class GlmKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qd-glm-key-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Save_load_roundtrip()
    {
        GlmKeyStore.Save(_dir, "  test-key-1234  ");
        Assert.Equal("test-key-1234", GlmKeyStore.Load(_dir));
        Assert.True(File.Exists(GlmKeyStore.FilePath(_dir)));
        // 落盘内容不是明文
        var raw = File.ReadAllBytes(GlmKeyStore.FilePath(_dir));
        Assert.DoesNotContain("test-key"u8.ToArray(), raw);
    }

    [Fact]
    public void Delete_removes_key()
    {
        GlmKeyStore.Save(_dir, "k1");
        GlmKeyStore.Delete(_dir);
        Assert.Null(GlmKeyStore.Load(_dir));
    }

    [Fact]
    public void Missing_dir_returns_null()
    {
        Assert.Null(GlmKeyStore.Load(Path.Combine(_dir, "not-exist")));
    }

    [Fact]
    public void Corrupt_file_reports_unreadable_and_is_preserved()
    {
        // 解密失败不能静默删密钥文件：区分"未配置"与"无法解密"，由用户主动重设或删除。
        Directory.CreateDirectory(_dir);
        var path = GlmKeyStore.FilePath(_dir);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var read = GlmKeyStore.TryLoad(_dir);
        Assert.Equal(GlmKeyStatus.Unreadable, read.Status);
        Assert.Null(read.Key);
        Assert.True(File.Exists(path)); // 原文件保留
        Assert.Null(GlmKeyStore.Load(_dir));
    }

    [Fact]
    public void Not_configured_status_when_no_file()
    {
        var read = GlmKeyStore.TryLoad(_dir);
        Assert.Equal(GlmKeyStatus.NotConfigured, read.Status);
        Assert.Null(read.Key);
    }

    [Fact]
    public void Account_key_is_stable_hash_not_the_key()
    {
        var a = GlmKeyStore.AccountKeyOf("102ab99ffdc84a3c.Zf8PQpNQbcgbFS5K");
        var b = GlmKeyStore.AccountKeyOf(" 102ab99ffdc84a3c.Zf8PQpNQbcgbFS5K ");
        Assert.Equal(a, b);                     // trim 后稳定
        Assert.Equal(16, a.Length);
        Assert.DoesNotContain("102ab99ffdc84a3c", a);
        Assert.Equal(a, GlmKeyStore.AccountKeyOf("102ab99ffdc84a3c.Zf8PQpNQbcgbFS5K"));
    }
}

public class GlmUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 17, 0, 0, TimeSpan.FromHours(8));

    private static string RealShape() => File.ReadAllText(FixturesHelper.Path("glm-quota-real-shape.json"));

    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        public List<(string? Auth, string Url)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("Authorization", out var auth);
            Requests.Add((auth?.FirstOrDefault(), request.RequestUri!.ToString()));
            var step = steps[Math.Min(Requests.Count - 1, steps.Length - 1)];
            return Task.FromResult(step(request));
        }

        public static HttpResponseMessage Json(int status, string body) => new()
        {
            StatusCode = (HttpStatusCode)status,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>预置一个合法 key 的临时设置目录。</summary>
    private (GlmUsageProvider Provider, FakeHandler Handler, string Dir, string AccountKey) MakeProvider(
        FakeHandler handler, string? key = "FAKE-GLM-KEY-0001", int budgetSeconds = 30)
    {
        var dir = Path.Combine(Path.GetTempPath(), "qd-glm-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? accountKey = null;
        if (key is not null)
        {
            GlmKeyStore.Save(dir, key);
            accountKey = GlmKeyStore.AccountKeyOf(key);
        }
        var provider = new GlmUsageProvider(dir, clock: () => Now, handler: handler, budgetSeconds: budgetSeconds);
        return (provider, handler, dir, accountKey!);
    }

    [Fact]
    public async Task Success_200_parses_real_shape_with_account_key()
    {
        var (provider, handler, dir, accountKey) = MakeProvider(new FakeHandler(
            _ => FakeHandler.Json(200, RealShape())));
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(ProviderState.Live, snap.State);
            Assert.Equal("glm-monitor", snap.Source);
            Assert.Equal(accountKey, snap.AccountKey);
            Assert.Equal(2, snap.Windows.Count);

            var (auth, url) = handler.Requests.Single();
            Assert.Equal("Bearer FAKE-GLM-KEY-0001", auth);
            Assert.Equal(GlmUsageProvider.Endpoint, url); // 凭据只发往确认过的域名
            Assert.Single(handler.Requests);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Auth_failure_maps_without_retry()
    {
        var (provider, handler, dir, _) = MakeProvider(new FakeHandler(
            _ => FakeHandler.Json(401, """{"code":401,"msg":"bad key"}""")));
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal("Unauthorized", snap.ErrorCode);
            Assert.Single(handler.Requests);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Envelope_failure_is_classified()
    {
        var (provider, _, dir, _) = MakeProvider(new FakeHandler(
            _ => FakeHandler.Json(200, """{"success": false, "code": 1000, "msg": "身份验证失败"}""")));
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(ProviderState.Unavailable, snap.State);
            Assert.Equal("Unauthorized", snap.ErrorCode);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Missing_key_reports_no_credentials_without_http()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(200, "{}"));
        var (provider, _, dir, _) = MakeProvider(handler, key: null);
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal("NoCredentials", snap.ErrorCode);
            Assert.Empty(handler.Requests);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Transient_failure_retries_then_succeeds()
    {
        var call = 0;
        var (provider, handler, dir, _) = MakeProvider(new FakeHandler(
            _ => ++call == 1 ? throw new HttpRequestException("reset") : FakeHandler.Json(200, RealShape())));
        try
        {
            var snap = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(ProviderState.Live, snap.State);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Account_key_changes_with_key_for_cache_isolation()
    {
        var keyA = GlmKeyStore.AccountKeyOf("KEY-ACCOUNT-A");
        var keyB = GlmKeyStore.AccountKeyOf("KEY-ACCOUNT-B");
        Assert.NotEqual(keyA, keyB);
    }
}
