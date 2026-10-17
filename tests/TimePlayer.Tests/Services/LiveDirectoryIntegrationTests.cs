using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;
using TimePlayer.Core.Services;
using Xunit;

namespace TimePlayer.Tests.Services;

public class LiveDirectoryIntegrationTests
{
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
}
