using TimePlayer.Core.Models;

namespace TimePlayer.Core.Contracts;

/// <summary>
/// Contract for probing instance health, measuring latency, and selecting the highest-performing instance.
/// </summary>
public interface IInstanceHealthService
{
    Task<InstanceHealthMeasurement> ProbeInstanceAsync(
        PublicInstance instance,
        CancellationToken cancellationToken = default);

    Task<PublicInstance?> SelectBestInstanceAsync(
        BackendProviderType providerType,
        CancellationToken cancellationToken = default);

    void RecordSuccess(PublicInstance instance, TimeSpan latency);

    void RecordFailure(PublicInstance instance, string reason);
}
