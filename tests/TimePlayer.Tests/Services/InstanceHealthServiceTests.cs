using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;
using TimePlayer.Core.Services;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Services;

public class InstanceHealthServiceTests
{
    private sealed class StubDirectory : IPublicInstanceDirectory
    {
        public List<PublicInstance> Instances { get; set; } = new();

        public Task<IReadOnlyList<PublicInstance>> GetInstancesAsync(BackendProviderType providerType, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<PublicInstance>>(Instances);
        }

        public Task SaveHealthyInstancesAsync(BackendProviderType providerType, IReadOnlyList<PublicInstance> instances, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ProbeInstanceAsync_InvidiousStatsObject_ReturnsSuccess()
    {
        var json = """{ "version": "2.0", "software": { "name": "invidious", "version": "2026.10" } }""";
        var handler = MockHttpMessageHandler.StringResponse(json);
        var httpClient = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var directory = new StubDirectory();

        var service = new InstanceHealthService(
            httpClient,
            directory,
            NullLogger<InstanceHealthService>.Instance);

        var instance = new PublicInstance
        {
            BaseUri = new Uri("https://invidious.f5.si"),
            ProviderType = BackendProviderType.Invidious
        };

        var measurement = await service.ProbeInstanceAsync(instance);

        Assert.True(measurement.IsSuccessful);
        Assert.Equal(200, measurement.StatusCode);
        Assert.EndsWith("api/v1/stats", handler.Requests[0].RequestUri?.ToString());
    }

    [Fact]
    public async Task ProbeInstanceAsync_Http429_TriggersRateLimitFailureAndCooldown()
    {
        var handler = MockHttpMessageHandler.Status(HttpStatusCode.TooManyRequests);
        var httpClient = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var directory = new StubDirectory();

        var service = new InstanceHealthService(
            httpClient,
            directory,
            NullLogger<InstanceHealthService>.Instance);

        var instance = new PublicInstance
        {
            BaseUri = new Uri("https://piped.rate.limited"),
            ProviderType = BackendProviderType.Invidious
        };

        var measurement = await service.ProbeInstanceAsync(instance);

        Assert.False(measurement.IsSuccessful);
        Assert.Equal(429, measurement.StatusCode);
        Assert.Contains("Rate limited", measurement.ErrorMessage);
    }

    [Fact]
    public async Task ProbeInstanceAsync_InvalidJsonShape_ReturnsFailure()
    {
        // Invidious answers a JSON object; here it returns HTML
        var handler = MockHttpMessageHandler.StringResponse("<html><body>Welcome</body></html>", HttpStatusCode.OK, "text/html");
        var httpClient = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var directory = new StubDirectory();

        var service = new InstanceHealthService(
            httpClient,
            directory,
            NullLogger<InstanceHealthService>.Instance);

        var instance = new PublicInstance
        {
            BaseUri = new Uri("https://piped.badjson.com"),
            ProviderType = BackendProviderType.Invidious
        };

        var measurement = await service.ProbeInstanceAsync(instance);

        Assert.False(measurement.IsSuccessful);
        Assert.Contains("Invalid API JSON response shape", measurement.ErrorMessage);
    }

    [Fact]
    public async Task SelectBestInstanceAsync_SkipsFailingInstanceAndPicksHealthy()
    {
        var inst1 = new PublicInstance { BaseUri = new Uri("https://failing.example.com/"), ProviderType = BackendProviderType.Invidious, LatencyMs = 50 };
        var inst2 = new PublicInstance { BaseUri = new Uri("https://working.example.com/"), ProviderType = BackendProviderType.Invidious, LatencyMs = 120 };

        var directory = new StubDirectory { Instances = new List<PublicInstance> { inst1, inst2 } };

        // inst1 fails probe, inst2 succeeds probe
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host == "failing.example.com")
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"software\":{\"name\":\"invidious\"}}", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var service = new InstanceHealthService(
            httpClient,
            directory,
            NullLogger<InstanceHealthService>.Instance);

        var selected = await service.SelectBestInstanceAsync(BackendProviderType.Invidious);

        Assert.NotNull(selected);
        Assert.Equal(new Uri("https://working.example.com/"), selected.BaseUri);
    }
}
