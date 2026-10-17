using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends;

/// <summary>
/// Persistence contract for caching known good public instances locally.
/// </summary>
public interface IInstanceCache
{
    Task<IReadOnlyList<InstanceInfo>> LoadCachedInstancesAsync(BackendProviderType providerType, CancellationToken cancellationToken = default);
    Task SaveCachedInstancesAsync(BackendProviderType providerType, IReadOnlyList<InstanceInfo> instances, CancellationToken cancellationToken = default);
}
