using System.Collections.ObjectModel;
using System.IO;
using QuotaDesk.Models;
using QuotaDesk.Providers;
using QuotaDesk.Scheduling;
using QuotaDesk.Services;

namespace QuotaDesk.ViewModels;

/// <summary>主视图模型：两个平台按固定顺序排列（GLM、Codex），各自独立刷新与状态。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _settingsDir;
    private readonly Func<string, string, Func<DateTimeOffset>, string, IQuotaProvider> _providerFactory;
    private readonly HashSet<string> _resetRefreshed = new(); // 平台|窗口|重置点 → 已请求过
    private RefreshCoordinator _coordinator;
    private DateTimeOffset _lastRefreshAt = DateTimeOffset.MinValue;
    private bool _refreshing;
    private CancellationTokenSource? _refreshCts;
    /// <summary>配置代次：ReconfigureProviders 递增；旧代次的在途回调不得写入新视图。</summary>
    private int _configGeneration;
    /// <summary>刷新序号：新刷新开始即递增；仅最新一次刷新能复位 IsRefreshing。</summary>
    private int _refreshEpoch;
    /// <summary>当前在途刷新所属的配置代次。</summary>
    private int _activeGeneration = -1;

    /// <summary>低额度通知判定（Enabled 由设置控制，默认关闭）。</summary>
    public LowQuotaNotifier Notifier { get; } = new();

    public ObservableCollection<ProviderViewModel> Providers { get; }

    public event EventHandler? RefreshCompleted;
    public event EventHandler<(string Title, string Text)>? NotificationRaised;

    public MainViewModel(AppSettings settings, Func<DateTimeOffset>? clock = null, string? settingsDir = null,
        Func<string, string, Func<DateTimeOffset>, string, IQuotaProvider>? providerFactory = null)
    {
        _settings = settings;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _settingsDir = settingsDir ?? Path.GetDirectoryName(AppSettings.DefaultFilePath)!;
        _providerFactory = providerFactory ?? ProviderFactory.Create;
        Notifier.Enabled = settings.LowQuotaNotify;
        Providers = [];
        BuildProviders();
        _coordinator = new RefreshCoordinator(Providers.Select(p => p.Provider), _clock);
    }

    public AppSettings Settings => _settings;

    /// <summary>设置目录（GLM key 存储位置等）。</summary>
    public string SettingsDir => _settingsDir;

    /// <summary>全局“剩余/已用”标识文字，始终可见。</summary>
    public string RatioLabel => _settings.ShowRemaining ? "剩余" : "已用";

    public string RatioToolTip => _settings.ShowRemaining
        ? "当前显示剩余比例，点击切换为已用"
        : "当前显示已用比例，点击切换为剩余";

    /// <summary>任一平台处于模拟来源时显示“演示”标识。</summary>
    public bool HasDemo => Providers.Any(p => p.IsDemo);

    public bool IsRefreshing
    {
        get => _refreshing;
        private set => Set(ref _refreshing, value);
    }

    public ProviderViewModel? Find(string providerId) =>
        Providers.FirstOrDefault(p => p.ProviderId == providerId);

    private void BuildProviders()
    {
        foreach (var p in Providers)
            (p.Provider as IDisposable)?.Dispose();
        Providers.Clear();
        // 两平台排列固定：GLM 在前，Codex 在后。
        Providers.Add(new ProviderViewModel("glm", _settings.GlmSource, _clock, _settingsDir, _providerFactory));
        Providers.Add(new ProviderViewModel("codex", _settings.CodexSource, _clock, _settingsDir, _providerFactory));
        foreach (var p in Providers)
            p.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProviderViewModel.IsDemo)) Raise(nameof(HasDemo));
            };
    }

    /// <summary>
    /// 来源设置变化后重建提供器。先递增配置代次并取消在途请求，再释放旧提供器，
    /// 保证旧请求的结果不会写入新视图（回调按代次校验，提供器 Dispose 后也不再可写）。
    /// </summary>
    public void ReconfigureProviders()
    {
        _configGeneration++;
        _refreshCts?.Cancel();
        BuildProviders();
        _coordinator = new RefreshCoordinator(Providers.Select(p => p.Provider), _clock);
        Raise(nameof(HasDemo));
        _ = RefreshAsync(manual: false);
    }

    /// <summary>手动/定时刷新。手动带 3 秒冷却；同代次在途请求自动合并；429 退避窗口内跳过。</summary>
    public async Task RefreshAsync(bool manual)
    {
        var generation = _configGeneration;
        var epochAtEntry = _refreshEpoch;
        var now = _clock();
        // 同代次且未被更新的刷新取代（epoch 未变）时直接合并；新代次（配置刚切换）不拦，必须能立即刷。
        if (IsRefreshing && epochAtEntry == _refreshEpoch && _activeGeneration == generation) return;
        if (manual && now - _lastRefreshAt < TimeSpan.FromSeconds(3)) return;

        var epoch = ++_refreshEpoch;
        _activeGeneration = generation;
        IsRefreshing = true;
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        _lastRefreshAt = now;
        try
        {
            var token = _refreshCts.Token;
            await _coordinator.RefreshAllAsync((id, snap) =>
            {
                if (generation != _configGeneration) return Task.CompletedTask; // 旧配置请求的回调不生效
                var vm = Find(id);
                vm?.ApplySnapshot(snap, _settings, _clock());
                foreach (var message in Notifier.Evaluate(id, snap, _settings))
                    NotificationRaised?.Invoke(this, message);
                return Task.CompletedTask;
            }, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 被新刷新或新配置取代：正常路径，不视为错误。
        }
        finally
        {
            if (_refreshEpoch == epoch) IsRefreshing = false; // 更新的刷新已接管时不清掉它的状态
            RefreshCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 本地秒级计时：更新倒计时文本；某平台有重置点已过且该重置点尚未请求过时，
    /// 触发一次刷新（去重），不生成虚假恢复值。
    /// </summary>
    public async Task TickSecondAsync()
    {
        var now = _clock();
        var resetDue = false;
        foreach (var p in Providers)
        {
            p.TickCountdown(_settings, now);
            if (!resetDue && p.ConsumeResetDue(now, _resetRefreshed))
                resetDue = true;
        }
        if (_resetRefreshed.Count > 256) _resetRefreshed.Clear(); // 防御性上限
        if (resetDue)
            await RefreshAsync(manual: false).ConfigureAwait(true);
    }

    public void OnSettingsChanged()
    {
        Raise(nameof(RatioLabel));
        Raise(nameof(RatioToolTip));
        foreach (var p in Providers)
            p.TickCountdown(_settings, _clock());
    }
}
