using QuotaDesk.Models;
using QuotaDesk.Providers;
using QuotaDesk.Services;

namespace QuotaDesk.ViewModels;

/// <summary>
/// 一个平台的视图状态：持有最近快照（含最后成功值），请求失败时保留旧值并明确标注；
/// 账号身份变化时不沿用旧账号数据。每个平台独立，互不影响。
/// </summary>
public sealed class ProviderViewModel : ObservableObject
{
    private readonly IQuotaProvider _provider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _isMock;
    private ProviderSnapshot? _lastGood;
    private bool _showingStale; // 当前展示是否为旧数据（失败但保留了最近成功值）

    private string? _updateText;
    private string? _errorText;
    private string? _source;
    private bool _isDemo = true;
    private MockScenario _scenario = MockScenario.Normal;

    public ProviderViewModel(string providerId, string source, Func<DateTimeOffset> clock, string settingsDir,
        Func<string, string, Func<DateTimeOffset>, string, IQuotaProvider>? providerFactory = null)
    {
        ProviderId = providerId;
        _clock = clock;
        _isMock = source != "real";
        _provider = (providerFactory ?? ProviderFactory.Create)(providerId, source, clock, settingsDir);
        DisplayName = providerId switch
        {
            "glm" => "GLM",
            "codex" => "Codex",
            var id => id,
        };
        Rows =
        [
            new RowViewModel(), // 5h
            new RowViewModel(), // 周
        ];
        ApplyOffline("正在获取数据…");
    }

    public string ProviderId { get; }
    public string DisplayName { get; }
    public IReadOnlyList<RowViewModel> Rows { get; }

    /// <summary>当前演示场景。仅模拟来源有效；真实来源静默忽略。</summary>
    public MockScenario Scenario
    {
        get => _scenario;
        set
        {
            if (!Set(ref _scenario, value)) return;
            if (_provider is MockQuotaProvider mock) mock.Scenario = value;
        }
    }

    public IQuotaProvider Provider => _provider;
    public bool IsMock => _isMock;

    public string? UpdateText { get => _updateText; private set { if (Set(ref _updateText, value)) Raise(nameof(StatusText)); } }
    public string? ErrorText { get => _errorText; private set { if (Set(ref _errorText, value)) { Raise(nameof(StatusText)); Raise(nameof(HasError)); } } }

    /// <summary>平台状态行文字：有错误优先显示错误，否则显示更新时间。</summary>
    public string? StatusText => _errorText ?? _updateText;
    public bool HasError => _errorText != null;

    /// <summary>当前展示的是旧数据（刷新失败但保留了最近成功值），直到下一次成功刷新为止。</summary>
    public bool IsStale => _showingStale;

    /// <summary>尚未拿到过任何成功数据（首次启动的获取阶段），此时任务栏不标"错"。</summary>
    public bool IsAwaitingFirstData { get; private set; } = true;

    /// <summary>来源的可读名称，用于详情展示（不含凭据）。</summary>
    public string SourceLabel => Source switch
    {
        null or "" => "未获取",
        "mock" => "模拟数据",
        "glm-monitor" => "智谱用量接口",
        "codex-wham" => "ChatGPT 用量接口",
        var s => s,
    };

    /// <summary>悬停详情卡的状态行：更新时间 + 来源（错误原因由 ErrorText 单独一行展示）。</summary>
    public string? DetailLine
    {
        get
        {
            var parts = new[] { UpdateText, $"来源：{SourceLabel}" }
                .Where(s => !string.IsNullOrEmpty(s));
            var text = string.Join(" · ", parts);
            return text.Length == 0 ? null : text;
        }
    }

    private string? _rowIssueText;
    /// <summary>窗口级异常（未报告/暂无数据/等待更新/旧数据等），取第一个有异常的窗口。</summary>
    public string? RowIssueText { get => _rowIssueText; private set => Set(ref _rowIssueText, value); }

    /// <summary>最近一次快照的来源标识（codex-wham / mock / unconfigured）。</summary>
    public string? Source { get => _source; private set => Set(ref _source, value); }
    public bool IsDemo { get => _isDemo; private set => Set(ref _isDemo, value); }

    /// <summary>应用新快照。Unavailable 时保留最近成功值并转为旧数据展示；账号变化时丢弃旧值。</summary>
    public void ApplySnapshot(ProviderSnapshot incoming, AppSettings settings, DateTimeOffset now)
    {
        IsAwaitingFirstData = false;
        // 缓存按账号身份隔离：换账号后旧账号的最近成功值不可沿用。
        if (incoming.AccountKey is { } key && _lastGood?.AccountKey is { } prev && prev != key)
            _lastGood = null;

        ProviderSnapshot effective;
        if (incoming.State == ProviderState.Unavailable && _lastGood is not null)
        {
            effective = StaleOf(_lastGood, incoming);
            _showingStale = true;
        }
        else
        {
            effective = incoming;
            if (incoming.State == ProviderState.Live)
            {
                _lastGood = incoming;
                _showingStale = false;
            }
            else if (incoming.State == ProviderState.Stale)
            {
                _showingStale = true; // 数据源自行声明的旧数据（如演示场景）同样按旧数据展示
            }
        }

        var display = DisplayMapper.Map(effective, settings, now);
        // 状态字段先于行更新：行 PropertyChanged 会同步触发挂件 SetLine 读取
        // ErrorText/Source/IsStale，若后赋值，挂件会拿到上一轮的状态（悬停提示滞后一轮）。
        UpdateText = display.UpdateText;
        ErrorText = incoming.State == ProviderState.Unavailable
            ? DisplayMapper.MapError(incoming)
            : display.ErrorText;
        Source = effective.Source; // 展示中的数据来自哪个来源（旧数据沿用旧来源；本轮失败无来源则为空）
        IsDemo = effective.Source == "mock";
        Raise(nameof(IsStale));
        for (var i = 0; i < Rows.Count && i < display.Rows.Count; i++)
            Rows[i].Update(display.Rows[i]);
        RowIssueText = Rows.Select(r => r.StatusText).FirstOrDefault(s => !string.IsNullOrEmpty(s));
    }

    /// <summary>
    /// 1 秒本地计时：只刷新倒计时类文本，不发起网络。
    /// 旧数据状态必须跨 tick 保持：按当前展示状态（而非一律 Live）重映射行数据，
    /// "旧数据"标记持续到下一次成功结果到来。不触碰 UpdateText/ErrorText（由 ApplySnapshot 管理）。
    /// </summary>
    public void TickCountdown(AppSettings settings, DateTimeOffset now)
    {
        if (_lastGood is null) return;
        var mapped = _showingStale ? StaleOf(_lastGood, attempt: null) : _lastGood;
        var display = DisplayMapper.Map(mapped, settings, now);
        for (var i = 0; i < Rows.Count && i < display.Rows.Count; i++)
            Rows[i].Update(display.Rows[i]);
        RowIssueText = Rows.Select(r => r.StatusText).FirstOrDefault(s => !string.IsNullOrEmpty(s));
    }

    private static ProviderSnapshot StaleOf(ProviderSnapshot good, ProviderSnapshot? attempt) => new()
    {
        ProviderId = good.ProviderId,
        Source = good.Source,
        Plan = good.Plan,
        Windows = good.Windows,
        ObservedAt = good.ObservedAt,
        LastAttemptAt = attempt?.LastAttemptAt ?? good.LastAttemptAt,
        State = ProviderState.Stale,
        ErrorCode = attempt?.ErrorCode,
        AccountKey = good.AccountKey,
    };

    /// <summary>窗口重置点已过且尚未为该重置点请求过新数据时，登记并要求刷新一次（去重）。</summary>
    public bool ConsumeResetDue(DateTimeOffset now, HashSet<string> doneKeys)
    {
        if (_lastGood is null) return false;
        foreach (var w in _lastGood.Windows)
        {
            if (w.ResetsAt is not { } reset || reset > now) continue;
            var key = $"{ProviderId}|{w.Id}|{reset.UtcTicks}";
            if (doneKeys.Add(key)) return true;
        }
        return false;
    }

    private void ApplyOffline(string message)
    {
        UpdateText = null;
        ErrorText = message;
        for (var i = 0; i < Rows.Count; i++)
            Rows[i].Update(new RowDisplay
            {
                Label = i == 0 ? "5h" : "周",
                PercentText = "—",
                Fraction = 0,
                Severity = Severity.Normal,
                HasData = false,
                StatusText = null,
            });
    }
}
