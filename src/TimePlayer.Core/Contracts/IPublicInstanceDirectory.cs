using TimePlayer.Core.Models;

namespace TimePlayer.Core.Contracts;

/// <summary>
/// Contract for discovering and persisting public backend instances (Piped, Invidious).
/// </summary>
public interface IPublicInstanceDirectory
{
    Task<IReadOnlyList<PublicInstance>> GetInstancesAsync(
        BackendProviderType providerType,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task SaveHealthyInstancesAsync(
        BackendProviderType providerType,
        IReadOnlyList<PublicInstance> instances,
        CancellationToken cancellationToken = default);
}
