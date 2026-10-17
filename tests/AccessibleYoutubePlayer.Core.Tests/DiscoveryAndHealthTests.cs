using System.Net;
using AccessibleYoutubePlayer.Core.Models;
using AccessibleYoutubePlayer.Core.Services.Discovery;
using AccessibleYoutubePlayer.Core.Services.Health;
using Xunit;

namespace AccessibleYoutubePlayer.Core.Tests;

public class DiscoveryAndHealthTests
{
    [Fact]
    public void Piped_ParseMarkdown_ExtractsValidHttpsInstances_AndRejectsInsecureOrBadges()
    {
        var sampleMarkdown = @"
Instance Name | Instance API URL | Location | CDN
--- | --- | --- | ---
Ducks Party | https://pipedapi.ducks.party | US | No
Insecure Box | http://pipedapi.insecure.org | FR | No
Bad Host | not_a_url | DE | No
Image Badge | https://pipedapi.kavin.rocks/badge.png | DE | No
Private Coffee | https://api.piped.private.coffee | EU | Yes
";

        var instances = PipedDirectoryProvider.ParseMarkdownInstances(sampleMarkdown);

        Assert.Equal(2, instances.Count);
        Assert.Contains(instances, i => i.BaseUri.ToString().TrimEnd('/') == "https://pipedapi.ducks.party");
        Assert.Contains(instances, i => i.BaseUri.ToString().TrimEnd('/') == "https://api.piped.private.coffee");
        Assert.DoesNotContain(instances, i => i.BaseUri.Scheme == "http");
    }

    [Fact]
    public void Piped_ParseJson_ExtractsValidInstances()
    {
        var sampleJson = @"[
            { ""name"": ""Instance A"", ""api_url"": ""https://api.a.com/"" },
            { ""name"": ""Instance B"", ""api_url"": ""http://insecure.b.com"" }
        ]";

        var instances = PipedDirectoryProvider.ParseJsonDirectory(sampleJson);

        Assert.Single(instances);
        Assert.Equal("https://api.a.com", instances[0].BaseUri.ToString().TrimEnd('/'));
    }

    [Fact]
    public void Invidious_ParseDirectory_FiltersForHttpsAndApiEnabled()
    {
        var sampleJson = @"[
            [
                ""inv.good.com"",
                { ""type"": ""https"", ""api"": true, ""uri"": ""https://inv.good.com"" }
            ],
            [
                ""inv.noapi.com"",
                { ""type"": ""https"", ""api"": false, ""uri"": ""https://inv.noapi.com"" }
            ],
            [
                ""inv.http.com"",
                { ""type"": ""onion"", ""api"": true, ""uri"": ""http://inv.http.com"" }
            ]
        ]";

        var instances = InvidiousDirectoryProvider.ParseInvidiousDirectory(sampleJson);

        Assert.Single(instances);
        Assert.Equal("inv.good.com", instances[0].Name);
        Assert.Equal("https://inv.good.com", instances[0].BaseUri.ToString().TrimEnd('/'));
    }

    [Fact]
    public async Task FileInstanceCache_ReturnsSeeds_WhenFileMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "A11yYtPlayerTests_" + Guid.NewGuid());
        try
        {
            var cache = new FileInstanceCache(tempDir);
            var seeds = await cache.LoadCachedInstancesAsync(ProviderType.Piped);

            Assert.NotNull(seeds);
            Assert.NotEmpty(seeds);
            Assert.All(seeds, s => Assert.Equal(Uri.UriSchemeHttps, s.BaseUri.Scheme));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task FileInstanceCache_SavesAndLoadsInstances()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "A11yYtPlayerTests_" + Guid.NewGuid());
        try
        {
            var cache = new FileInstanceCache(tempDir);
            var testInstances = new List<InstanceInfo>
            {
                new() { BaseUri = new Uri("https://test.instance.org"), Name = "TestInst", ProviderType = ProviderType.Piped, LatencyMs = 120 }
            };

            await cache.SaveCachedInstancesAsync(ProviderType.Piped, testInstances);
            var loaded = await cache.LoadCachedInstancesAsync(ProviderType.Piped);

            Assert.NotNull(loaded);
            Assert.Single(loaded);
            Assert.Equal("https://test.instance.org", loaded[0].BaseUri.ToString().TrimEnd('/'));
            Assert.Equal(120, loaded[0].LatencyMs);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InstanceHealthChecker_RanksHealthyInstancesByLatency()
    {
        var handler = new MockHttpMessageHandler((req) =>
        {
            if (req.RequestUri!.Host.Contains("fast"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.RequestUri!.Host.Contains("slow"))
            {
                Thread.Sleep(50);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        });

        var client = new HttpClient(handler);
        var checker = new InstanceHealthChecker(client, TimeSpan.FromSeconds(2));

        var candidates = new List<InstanceInfo>
        {
            new() { BaseUri = new Uri("https://slow.com"), Name = "slow", ProviderType = ProviderType.Piped },
            new() { BaseUri = new Uri("https://fast.com"), Name = "fast", ProviderType = ProviderType.Piped },
            new() { BaseUri = new Uri("https://dead.com"), Name = "dead", ProviderType = ProviderType.Piped }
        };

        var ranked = await checker.FilterAndRankInstancesAsync(candidates);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("fast", ranked[0].Name);
        Assert.Equal("slow", ranked[1].Name);
    }
}

public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_responder(request));
    }
}
