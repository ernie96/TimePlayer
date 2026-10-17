namespace TimePlayer.Core.Models;

/// <summary>
/// Health measurement result from probing an individual public instance.
/// </summary>
public record InstanceHealthMeasurement
{
    public required Uri InstanceUri { get; init; }
    public required BackendProviderType ProviderType { get; init; }
    public TimeSpan Latency { get; init; }
    public bool IsSuccessful { get; init; }
    public int? StatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}
