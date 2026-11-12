using QuotaDesk.Models;

namespace QuotaDesk.Providers;

/// <summary>按平台与来源创建数据提供器。来源：real = 真实接口，mock = 演示数据。</summary>
public static class ProviderFactory
{
    public static IQuotaProvider Create(string providerId, string source, Func<DateTimeOffset> clock, string settingsDir) =>
        (providerId, source) switch
        {
            ("codex", "real") => new Codex.CodexUsageProvider(clock: clock),
            ("glm", "real") => new Glm.GlmUsageProvider(settingsDir, clock),
            // GLM 真实路径需要设置里已配置智谱国内 key；未配置时提供器返回“未配置密钥”。
            _ => new MockQuotaProvider(providerId, clock),
        };
}
