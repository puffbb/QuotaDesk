using QuotaDesk.Models;

namespace QuotaDesk.Providers;

/// <summary>额度数据提供器。真实实现阶段按平台各写一个，接口保持不变。</summary>
public interface IQuotaProvider
{
    string ProviderId { get; }
    Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
