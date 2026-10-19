using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;
using TimePlayer.Core.Services;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Services;

public class PublicInstanceDirectoryTests
{
    private static readonly string SampleInvidiousJson = """
        [
          [
            "inv.nadeko.net",
            {
              "type": "https",
              "uri": "https://inv.nadeko.net",
              "api": false,
              "monitor": { "uptime": 99.5, "down": false }
            }
          ],
          [
            "invidious.f5.si",
            {
              "type": "https",
              "uri": "https://invidious.f5.si",
              "api": true,
              "monitor": { "uptime": 98.2, "down": false },
              "stats": {
                "software": { "name": "invidious", "version": "2026.10.11-7286c6d" }
              }
            }
          ],
          [
            "down.invidious.io",
            {
              "type": "https",
              "uri": "https://down.invidious.io",
              "api": true,
              "monitor": { "uptime": 45.0, "down": true }
            }
          ],
          [
            "http.invidious.io",
            {
              "type": "http",
              "uri": "http://http.invidious.io",
              "api": true,
              "monitor": { "uptime": 95.0, "down": false }
            }
          ]
        ]
        """;

    [Fact]
    public void ParseInvidiousDirectoryJson_FiltersForApiEnabledAndHttpsAndNotDown()
    {
        var instances = PublicInstanceDirectory.ParseInvidiousDirectoryJson(SampleInvidiousJson);

        Assert.Single(instances);
        var item = instances[0];
        Assert.Equal(new Uri("https://invidious.f5.si/"), item.BaseUri);
        Assert.Equal(BackendProviderType.Invidious, item.ProviderType);
        Assert.Equal(98.2, item.HealthScore);
        Assert.Equal("2026.10.11-7286c6d", item.SoftwareVersion);
    }

    [Fact]
    public async Task GetInstancesAsync_ReturnsFreshCachedInstancesWithoutMakingNetworkCall()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "timeplayer_tests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FileInstanceCache(tempDir);
            var canned = new List<PublicInstance>
            {
                new()
                {
                    BaseUri = new Uri("https://cached.piped.example/"),
                    ProviderType = BackendProviderType.Invidious,
                    IsWorking = true
                }
            };
            await cache.SaveSnapshotAsync(BackendProviderType.Invidious, canned);

            var handler = MockHttpMessageHandler.Throws(new InvalidOperationException("Network should not be called on cache hit!"));
            var httpClient = new HttpClient(handler);
            var config = new ConfigurationService(Options.Create(new AppConfig()));

            var directory = new PublicInstanceDirectory(
                httpClient,
                config,
                cache,
                NullLogger<PublicInstanceDirectory>.Instance);

            var results = await directory.GetInstancesAsync(BackendProviderType.Invidious, forceRefresh: false);

            Assert.Single(results);
            Assert.Equal("https://cached.piped.example/", results[0].BaseUri.AbsoluteUri);
            Assert.Empty(handler.Requests); // Zero network calls made
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GetInstancesAsync_FallsBackToConfiguredSeedsWhenNetworkAndCacheFail()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "timeplayer_tests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FileInstanceCache(tempDir);
            var handler = MockHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
            var httpClient = new HttpClient(handler);
            var config = new ConfigurationService(Options.Create(new AppConfig
            {
                Backend = new BackendConfig
                {
                    InvidiousSeedInstances = new[] { "https://seed1.invidious.example", "https://seed2.invidious.example" }
                }
            }));

            var directory = new PublicInstanceDirectory(
                httpClient,
                config,
                cache,
                NullLogger<PublicInstanceDirectory>.Instance);

            var results = await directory.GetInstancesAsync(BackendProviderType.Invidious, forceRefresh: true);

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.BaseUri == new Uri("https://seed1.invidious.example/"));
            Assert.Contains(results, r => r.BaseUri == new Uri("https://seed2.invidious.example/"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
