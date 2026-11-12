namespace QuotaDesk.Providers;

/// <summary>阶段 1 的演示场景。数据全部为模拟，界面上始终标“演示”。</summary>
public enum MockScenario
{
    /// <summary>正常：GLM 5h 72% / 周 31%，Codex 5h 48% / 周 16%（与 PRODUCT.md 示例一致）。</summary>
    Normal,
    /// <summary>低额度：剩余接近阈值（橙/红）。</summary>
    Low,
    /// <summary>超额：100% 已用与超过 100%。</summary>
    Exceeded,
    /// <summary>缺失：窗口未报告、数值缺失、未开始。</summary>
    Missing,
    /// <summary>旧数据：读取失败，保留最近成功值并标注时间。</summary>
    Stale,
    /// <summary>请求失败：无可用数据。</summary>
    Error,
    /// <summary>认证失败：需要重新登录 / 检查密钥。</summary>
    AuthError,
}

public static class MockScenarioNames
{
    public static readonly (MockScenario Value, string Label)[] All =
    {
        (MockScenario.Normal, "正常"),
        (MockScenario.Low, "低额度"),
        (MockScenario.Exceeded, "超额"),
        (MockScenario.Missing, "缺失"),
        (MockScenario.Stale, "旧数据"),
        (MockScenario.Error, "请求失败"),
        (MockScenario.AuthError, "认证失败"),
    };

    public static bool TryParse(string? text, out MockScenario value)
    {
        value = MockScenario.Normal;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (Enum.TryParse(text, ignoreCase: true, out value)) return true;
        foreach (var (v, label) in All)
        {
            if (string.Equals(label, text, StringComparison.Ordinal)) { value = v; return true; }
        }
        return false;
    }
}
