using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;
using TimePlayer.Core.Services;
using Xunit;

namespace TimePlayer.Tests.Services;

public class LiveDirectoryIntegrationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public LiveDirectoryIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "LiveNetwork")]
    public async Task LiveNetwork_FetchAndParseInvidiousDirectory_DiscoversWorkingInstances()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var tempDir = Path.Combine(Path.GetTempPath(), "live_invidious_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FileInstanceCache(tempDir);
            var config = new ConfigurationService(Options.Create(new AppConfig()));
            var directory = new PublicInstanceDirectory(client, config, cache, NullLogger<PublicInstanceDirectory>.Instance);

            var instances = await directory.GetInstancesAsync(BackendProviderType.Invidious, forceRefresh: true);

            Assert.NotEmpty(instances);
            Assert.All(instances, i => Assert.Equal("https", i.BaseUri.Scheme));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "LiveNetwork")]
    public async Task LiveNetwork_FetchAndParsePipedDirectory_DiscoversInstances()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var tempDir = Path.Combine(Path.GetTempPath(), "live_piped_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FileInstanceCache(tempDir);
            var config = new ConfigurationService(Options.Create(new AppConfig()));
            var directory = new PublicInstanceDirectory(client, config, cache, NullLogger<PublicInstanceDirectory>.Instance);

            var instances = await directory.GetInstancesAsync(BackendProviderType.Piped, forceRefresh: true);

            Assert.NotEmpty(instances);
            Assert.All(instances, i => Assert.Equal("https", i.BaseUri.Scheme));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "LiveNetwork")]
    public async Task LiveNetwork_PipedSearch_ReturnsResults()
    {
        using var client = new HttpClient();
        var tempDir = Path.Combine(Path.GetTempPath(), "live_piped_search_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FileInstanceCache(tempDir);
            var config = new ConfigurationService(Options.Create(new AppConfig()));
            var directory = new PublicInstanceDirectory(client, config, cache, NullLogger<PublicInstanceDirectory>.Instance);
            var health = new InstanceHealthService(client, directory, config, NullLogger<InstanceHealthService>.Instance);
            var piped = new TimePlayer.Core.Backends.Piped.PipedBackendProvider(client, health, config, NullLogger<TimePlayer.Core.Backends.Piped.PipedBackendProvider>.Instance);

            var instances = await directory.GetInstancesAsync(BackendProviderType.Piped);
            _output.WriteLine($"TOTAL INSTANCES: {instances.Count}");
            foreach (var inst in instances)
            {
                _output.WriteLine($"  INSTANCE: {inst.BaseUri}, Health: {inst.HealthScore}, Working: {inst.IsWorking}");
            }

            var res = await piped.SearchAsync("test");
            _output.WriteLine($"IS_SUCCESS: {res.IsSuccess}, ERROR: {res.Error?.Message}");
            Assert.True(res.IsSuccess, $"Piped search failed: {res.Error?.Message}");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "LiveNetwork")]
    public async Task LiveNetwork_InvidiousSearch_ReturnsResults()
    {
        using var client = new HttpClient();
        var tempDir = Path.Combine(Path.GetTempPath(), "live_invidious_search_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FileInstanceCache(tempDir);
            var config = new ConfigurationService(Options.Create(new AppConfig()));
            var directory = new PublicInstanceDirectory(client, config, cache, NullLogger<PublicInstanceDirectory>.Instance);
            var health = new InstanceHealthService(client, directory, config, NullLogger<InstanceHealthService>.Instance);
            var invidious = new TimePlayer.Core.Backends.Invidious.InvidiousBackendProvider(client, health, config, NullLogger<TimePlayer.Core.Backends.Invidious.InvidiousBackendProvider>.Instance);

            var res = await invidious.SearchAsync("test");
            _output.WriteLine($"INVIDIOUS IS_SUCCESS: {res.IsSuccess}, ERROR: {res.Error?.Message}, COUNT: {res.Value?.Items.Count}");
            Assert.True(res.IsSuccess, $"Invidious search failed: {res.Error?.Message}");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

