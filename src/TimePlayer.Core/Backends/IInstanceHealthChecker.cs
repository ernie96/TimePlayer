using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends;

/// <summary>
/// Health and latency probe for public backend instances.
/// </summary>
public interface IInstanceHealthChecker
{
    Task<InstanceInfo> CheckHealthAsync(InstanceInfo instance, CancellationToken cancellationToken = default);
}
