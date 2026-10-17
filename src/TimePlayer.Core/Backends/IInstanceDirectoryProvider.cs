using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends;

/// <summary>
/// Contract for discovering public API instances from official project sources.
/// </summary>
public interface IInstanceDirectoryProvider
{
    BackendProviderType ProviderType { get; }
    Task<IReadOnlyList<InstanceInfo>> FetchInstancesAsync(CancellationToken cancellationToken = default);
}
