using System.IO;
using System.Reflection;
using QuotaDesk.Models;
using QuotaDesk.Providers.Codex;
using QuotaDesk.Providers.Glm;
using Xunit;

namespace QuotaDesk.Tests;

/// <summary>阶段 2：真实接口解析器测试。验证状态标注见 DATA-SOURCES.md。</summary>
public class CodexParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.FromHours(8));

    /// <summary>接口响应结构的合成样例。</summary>
    [Fact]
    public void Parses_real_shape_response()
    {
        var snap = CodexUsageParser.Parse(File.ReadAllText(FixturesHelper.Path("codex-usage-real-shape.json")), Now);

        Assert.Equal("codex", snap.ProviderId);
        Assert.Equal("plus", snap.Plan);
        Assert.Equal(ProviderState.Live, snap.State);
        Assert.Equal(2, snap.Windows.Count);

        var fiveHour = snap.Windows.Single(w => w.Kind == WindowKind.FiveHour);
        Assert.Equal(42, fiveHour.UsedPercent);
        Assert.Equal(18000, fiveHour.WindowSeconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), fiveHour.ResetsAt);
        Assert.Null(fiveHour.LimitReached);

        var weekly = snap.Windows.Single(w => w.Kind == WindowKind.Weekly);
        Assert.Equal(31, weekly.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1894060800), weekly.ResetsAt);
    }

    [Fact]
    public void Additional_rate_limits_map_to_scoped_windows_and_duration_decides_kind()
    {
        var snap = CodexUsageParser.Parse(File.ReadAllText(FixturesHelper.Path("codex-usage-additional-groups.json")), Now);

        // 主组：Pro 无 5h 窗口，周窗口出现在 primary —— 按时长识别而不是槽位。
        var main = snap.Windows.Where(w => w.Scope is null).ToList();
        var mainWeekly = main.Single(w => w.Kind == WindowKind.Weekly);
        Assert.Equal(137.5, mainWeekly.UsedPercent); // 超额原样保留

        var pro = snap.Windows.Where(w => w.Scope == "gpt-5-pro").ToList();
        Assert.Equal(2, pro.Count);
        Assert.All(pro, w => Assert.True(w.LimitReached)); // 组级 limit_reached 传递
        Assert.Equal(100, pro.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);

        // 86400 秒（1 天）不是 5h 也不是周 → Other，scope 保留模型名。
        var other = snap.Windows.Single(w => w.Kind == WindowKind.Other);
        Assert.Equal("codex-model-x", other.Scope);
        Assert.Equal(41, other.UsedPercent);
    }

    [Fact]
    public void Missing_fields_stay_missing_not_zero()
    {
        var snap = CodexUsageParser.Parse(File.ReadAllText(FixturesHelper.Path("codex-usage-missing-fields.json")), Now);

        var w = snap.Windows.Single();
        Assert.Null(w.UsedPercent);
        Assert.Null(w.ResetsAt);
        Assert.Equal(WindowKind.FiveHour, w.Kind);
    }

    [Fact]
    public void Empty_response_yields_live_snapshot_without_windows()
    {
        var snap = CodexUsageParser.Parse("""{"plan_type":"plus"}""", Now);
        Assert.Empty(snap.Windows);
        Assert.Equal(ProviderState.Live, snap.State);
    }
}

public class CodexAuthReaderTests : IDisposable
{
    private readonly string _dir;

    public CodexAuthReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qd-auth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Reads_tokens_from_fixture()
    {
        var path = Path.Combine(_dir, "auth.json");
        File.WriteAllText(path, File.ReadAllText(FixturesHelper.Path("codex-auth-fixture.json")));

        var result = CodexAuthReader.Read(path);
        Assert.True(result.IsOk);
        Assert.Equal("FAKE_ACCESS_TOKEN_FOR_TESTS", result.AccessToken);
        Assert.Equal("00000000-0000-0000-0000-000000000001", result.AccountId);
    }

    [Fact]
    public void CODEX_HOME_overrides_default_path()
    {
        var resolved = CodexAuthReader.ResolveAuthPath(codexHome: @"D:\custom\codex");
        Assert.Equal(@"D:\custom\codex\auth.json", resolved);
    }

    [Fact]
    public void Missing_file_and_missing_fields_are_distinct_failures()
    {
        Assert.Equal(CodexAuthStatus.FileMissing, CodexAuthReader.Read(Path.Combine(_dir, "nope.json")).Status);

        var path = Path.Combine(_dir, "auth.json");
        File.WriteAllText(path, """{"tokens": {"refresh_token": "x"}}""");
        Assert.Equal(CodexAuthStatus.MissingAccessToken, CodexAuthReader.Read(path).Status);

        File.WriteAllText(path, """{"OPENAI_API_KEY": null}""");
        Assert.Equal(CodexAuthStatus.MissingTokens, CodexAuthReader.Read(path).Status);
    }
}

/// <summary>
/// GLM 解析器测试。⚠ 全部为“源码分析 + 脱敏 fixture”级验证，
/// 尚无真实智谱国内账号响应核对（见 DATA-SOURCES.md）。
/// </summary>
public class GlmParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Parses_normal_envelope_with_5h_and_weekly()
    {
        var snap = GlmUsageParser.Parse(File.ReadAllText(FixturesHelper.Path("glm-quota-normal.json")), Now);

        Assert.Equal("glm", snap.ProviderId);
        Assert.Contains("GLM Coding Pro", snap.Plan);
        Assert.Equal(2, snap.Windows.Count);

        var fiveHour = snap.Windows.Single(w => w.Kind == WindowKind.FiveHour);
        Assert.Equal(28, fiveHour.UsedPercent); // percentage 按“已用”处理
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791437995000), fiveHour.ResetsAt);

        var weekly = snap.Windows.Single(w => w.Kind == WindowKind.Weekly);
        Assert.Equal(69, weekly.UsedPercent);
    }

    [Fact]
    public void Edge_cases_unknown_units_time_limits_and_missing_percentage()
    {
        var snap = GlmUsageParser.Parse(File.ReadAllText(FixturesHelper.Path("glm-quota-edge.json")), Now);

        // 30 天 TOKENS_LIMIT → Other（月度），105% 原样保留。
        var monthly = snap.Windows.Single(w => w.Scope == "TOKENS_LIMIT");
        Assert.Equal(WindowKind.Other, monthly.Kind);
        Assert.Equal(105, monthly.UsedPercent);

        // TIME_LIMIT（MCP）不携带配额，不造窗口。
        Assert.Equal(2, snap.Windows.Count);

        // CREDIT_LIMIT 未知 unit → Other；percentage 缺失保持缺失。
        var credit = snap.Windows.Single(w => w.Scope == "CREDIT_LIMIT");
        Assert.Null(credit.UsedPercent);
        Assert.Null(credit.ResetsAt);
    }

    [Theory]
    [InlineData(1000, "身份验证失败", "Unauthorized")]
    [InlineData(401, "", "Unauthorized")]
    [InlineData(403, "forbidden", "Unauthorized")]
    [InlineData(429, "rate limited", "RateLimited")]
    [InlineData(500, "当前账号未订阅 coding plan", "NoCodingPlan")]
    [InlineData(500, "其他错误", "ServerError")]
    public void Envelope_failures_are_classified(int code, string msg, string expected)
    {
        Assert.Equal(expected, GlmUsageParser.Classify(code, msg));
    }

    [Fact]
    public void Parses_real_response_shape_lite_plan()
    {
        // 合成样例：两个 CREDIT_LIMIT 窗口，percentage=已用比例。
        var snap = GlmUsageParser.Parse(File.ReadAllText(FixturesHelper.Path("glm-quota-real-shape.json")), Now);

        Assert.Equal("lite", snap.Plan);
        Assert.Equal(ProviderState.Live, snap.State);
        Assert.Equal(2, snap.Windows.Count);

        var fiveHour = snap.Windows.Single(w => w.Kind == WindowKind.FiveHour);
        Assert.Equal("CREDIT_LIMIT", fiveHour.Scope);
        Assert.Equal(25, fiveHour.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1893456000000), fiveHour.ResetsAt);

        var weekly = snap.Windows.Single(w => w.Kind == WindowKind.Weekly);
        Assert.Equal(2.5, weekly.UsedPercent);
    }

    [Fact]
    public void Error_envelope_maps_to_unavailable_snapshot()
    {
        var snap = GlmUsageParser.Parse(File.ReadAllText(FixturesHelper.Path("glm-quota-auth-error.json")), Now);
        Assert.Equal(ProviderState.Unavailable, snap.State);
        Assert.Equal("Unauthorized", snap.ErrorCode);
        Assert.Empty(snap.Windows);
    }
}

public static class FixturesHelper
{
    public static string Path(string name) => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "Fixtures", name);
}
