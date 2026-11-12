namespace QuotaDesk.Models;

/// <summary>额度窗口种类。按接口返回的实际持续时间识别，不预设 primary 即 5 小时。</summary>
public enum WindowKind
{
    FiveHour,
    Weekly,
    Other,
}

/// <summary>快照状态：Live = 最近一次成功读取；Stale = 读取失败但保留旧值；Unavailable = 无可用数据。</summary>
public enum ProviderState
{
    Live,
    Stale,
    Unavailable,
}

/// <summary>窗口开始状态。只有服务明确表示未开始才算 NotStarted，与"缺失"不同。</summary>
public enum StartState
{
    Started,
    NotStarted,
    Unknown,
}

/// <summary>单个额度窗口的统一数据。</summary>
public sealed class QuotaWindow
{
    public required string Id { get; init; }
    public WindowKind Kind { get; init; }
    /// <summary>范围说明（如作用模型），用于区分同类多窗口。</summary>
    public string? Scope { get; init; }
    /// <summary>原始已用百分比，可为 null（服务未给出），可超过 100（超额），不做 clamp。</summary>
    public double? UsedPercent { get; init; }
    public bool? LimitReached { get; init; }
    /// <summary>窗口时长（秒），用于识别 5h / 周窗口。</summary>
    public double? WindowSeconds { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public StartState StartState { get; init; } = StartState.Unknown;
}

/// <summary>一个平台一次读取得到的统一快照。</summary>
public sealed class ProviderSnapshot
{
    public required string ProviderId { get; init; }
    public string Source { get; init; } = "mock";
    public string? Plan { get; init; }
    public IReadOnlyList<QuotaWindow> Windows { get; init; } = [];
    /// <summary>本次数据的服务端/读取成功时间。</summary>
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset LastAttemptAt { get; init; }
    public ProviderState State { get; init; }
    /// <summary>机器可读错误码（NetworkError / Unauthorized / Timeout …），展示文本由映射层决定。</summary>
    public string? ErrorCode { get; init; }
    /// <summary>账号身份标识（Codex=account_id；GLM=后续按 key 摘要）。仅内存使用，用于缓存隔离：
    /// 账号切换后不能沿用旧账号的最近成功值。不写入日志。</summary>
    public string? AccountKey { get; init; }

    /// <summary>服务要求的退避时长（429 的 Retry-After）。调度器在该窗口内跳过此平台刷新。</summary>
    public TimeSpan? RetryAfter { get; init; }
}
