using QuotaDesk.Models;

namespace QuotaDesk.Services;

/// <summary>
/// 低额度通知判定：默认关闭；开启后按 平台|窗口|重置周期|阈值档位 去重——
/// 同一窗口同一重置周期内同一档位只提醒一次，档位升级（警告→严重）会再次提醒。
/// </summary>
public sealed class LowQuotaNotifier
{
    private readonly HashSet<string> _notified = new();

    public bool Enabled { get; set; }

    public IReadOnlyList<(string Title, string Text)> Evaluate(string providerId, ProviderSnapshot snapshot, AppSettings settings)
    {
        if (!Enabled || snapshot.State == ProviderState.Unavailable) return [];

        var displayName = providerId switch { "glm" => "GLM", "codex" => "Codex", var id => id };
        var messages = new List<(string Title, string Text)>();
        foreach (var w in snapshot.Windows)
        {
            if (w.UsedPercent is not { } used) continue;
            var remaining = DisplayMapper.RemainingPercent(used);
            if (remaining > settings.WarnRemainingPercent) continue;

            var critical = remaining <= settings.CriticalRemainingPercent;
            var key = $"{providerId}|{w.Id}|{w.ResetsAt?.UtcTicks}|{(critical ? "critical" : "warn")}";
            if (!_notified.Add(key)) continue;

            var label = w.Kind switch
            {
                WindowKind.FiveHour => "5h",
                WindowKind.Weekly => "周",
                _ => w.Scope ?? "其他",
            };
            var bandText = critical
                ? $"已低于严重阈值（剩余 ≤ {settings.CriticalRemainingPercent:0.#}%）"
                : $"已低于警告阈值（剩余 ≤ {settings.WarnRemainingPercent:0.#}%）";
            messages.Add(($"{displayName} {label} 额度偏低", $"{label} 剩余 {Math.Round(remaining)}%，{bandText}。重置时间见悬浮窗。"));
        }
        return messages;
    }
}
