using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Models;

public class InstanceInfoTests
{
    [Fact]
    public void PriorityScore_ReturnsMaxValueWhenNotWorking()
    {
        var instance = new InstanceInfo
        {
            BaseUri = new Uri("https://piped.example.com"),
            ProviderType = BackendProviderType.Piped,
            IsWorking = false,
            LatencyMs = 120
        };

        Assert.Equal(double.MaxValue, instance.PriorityScore);
    }

    [Fact]
    public void PriorityScore_FastHealthyInstanceRanksHigherThanSlowOrUnhealthyInstance()
    {
        var fastHealthy = new InstanceInfo
        {
            BaseUri = new Uri("https://fast.example.com"),
            ProviderType = BackendProviderType.Piped,
            IsWorking = true,
            LatencyMs = 85,
            HealthScore = 99.0,
            ConsecutiveFailures = 0
        };

        var slowInstance = new InstanceInfo
        {
            BaseUri = new Uri("https://slow.example.com"),
            ProviderType = BackendProviderType.Piped,
            IsWorking = true,
            LatencyMs = 850,
            HealthScore = 99.0,
            ConsecutiveFailures = 0
        };

        var failingInstance = new InstanceInfo
        {
            BaseUri = new Uri("https://failing.example.com"),
            ProviderType = BackendProviderType.Piped,
            IsWorking = true,
            LatencyMs = 100,
            HealthScore = 50.0,
            ConsecutiveFailures = 2
        };

        Assert.True(fastHealthy.PriorityScore < slowInstance.PriorityScore);
        Assert.True(slowInstance.PriorityScore < failingInstance.PriorityScore);
    }
}
