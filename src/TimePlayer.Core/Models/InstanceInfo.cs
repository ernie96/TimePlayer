namespace TimePlayer.Core.Models;

/// <summary>
/// Status and metadata for a public Piped or Invidious instance.
/// </summary>
public record InstanceInfo
{
    public required Uri BaseUri { get; init; }
    public required BackendProviderType ProviderType { get; init; }
    public bool IsWorking { get; init; }
    public long LatencyMs { get; init; } = -1;
    public double HealthScore { get; init; } = 100.0;
    public DateTimeOffset? LastCheckedUtc { get; init; }
    public int ConsecutiveFailures { get; init; }
    public bool IsCustom { get; init; }

    /// <summary>
    /// Computes overall priority score for automatic selection. Lower is better.
    /// Healthy, low-latency instances rank highest.
    /// </summary>
    public double PriorityScore
    {
        get
        {
            if (!IsWorking) return double.MaxValue;
            // Balance latency with health score; failures heavily penalize
            var latencyPenalty = LatencyMs > 0 ? LatencyMs : 1000;
            var healthPenalty = Math.Max(0, 100.0 - HealthScore) * 50;
            var failurePenalty = ConsecutiveFailures * 5000;
            return latencyPenalty + healthPenalty + failurePenalty;
        }
    }
}
