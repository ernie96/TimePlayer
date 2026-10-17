using System.Text.Json;
using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccessibleYoutubePlayer.Core.Services.Discovery;

/// <summary>
/// Persists the last known good instance directory on disk.
/// Includes embedded emergency seed instances so the app can function even if official
/// directories are unreachable during initial launch.
/// </summary>
public class FileInstanceCache : IInstanceCache
{
    private readonly string _cacheDirectory;
    private readonly ILogger<FileInstanceCache> _logger;

    public FileInstanceCache(string? cacheDirectory = null, ILogger<FileInstanceCache>? logger = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibleYoutubePlayer", "Cache");
        _logger = logger ?? NullLogger<FileInstanceCache>.Instance;
    }

    public async Task<IReadOnlyList<InstanceInfo>?> LoadCachedInstancesAsync(ProviderType providerType, CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(providerType);
        if (!File.Exists(filePath))
        {
            return GetEmergencySeeds(providerType);
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var deserialized = JsonSerializer.Deserialize<List<CachedInstanceDto>>(json);
            if (deserialized == null || deserialized.Count == 0)
            {
                return GetEmergencySeeds(providerType);
            }

            var result = new List<InstanceInfo>();
            foreach (var dto in deserialized)
            {
                if (Uri.TryCreate(dto.BaseUri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    result.Add(new InstanceInfo
                    {
                        BaseUri = uri,
                        Name = dto.Name,
                        ProviderType = providerType,
                        IsHealthy = dto.IsHealthy,
                        LatencyMs = dto.LatencyMs,
                        LastCheckedUtc = dto.LastCheckedUtc
                    });
                }
            }

            return result.Count > 0 ? result : GetEmergencySeeds(providerType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read cached instances for {Provider}. Using emergency seeds.", providerType);
            return GetEmergencySeeds(providerType);
        }
    }

    public async Task SaveCachedInstancesAsync(ProviderType providerType, IReadOnlyList<InstanceInfo> instances, CancellationToken cancellationToken = default)
    {
        if (instances == null || instances.Count == 0) return;

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var filePath = GetFilePath(providerType);

            var dtos = instances.Select(i => new CachedInstanceDto
            {
                BaseUri = i.BaseUri.ToString(),
                Name = i.Name,
                IsHealthy = i.IsHealthy,
                LatencyMs = i.LatencyMs,
                LastCheckedUtc = i.LastCheckedUtc
            }).ToList();

            var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist instance cache for {Provider}.", providerType);
        }
    }

    private string GetFilePath(ProviderType providerType)
    {
        return Path.Combine(_cacheDirectory, $"instances_{providerType.ToString().ToLowerInvariant()}.json");
    }

    public static IReadOnlyList<InstanceInfo> GetEmergencySeeds(ProviderType providerType)
    {
        return providerType switch
        {
            ProviderType.Piped => new List<InstanceInfo>
            {
                new() { BaseUri = new Uri("https://pipedapi.ducks.party"), Name = "ducks.party", ProviderType = ProviderType.Piped },
                new() { BaseUri = new Uri("https://api.piped.private.coffee"), Name = "private.coffee", ProviderType = ProviderType.Piped },
                new() { BaseUri = new Uri("https://pipedapi.adminforge.de"), Name = "adminforge.de", ProviderType = ProviderType.Piped }
            },
            ProviderType.Invidious => new List<InstanceInfo>
            {
                new() { BaseUri = new Uri("https://invidious.f5.si"), Name = "f5.si", ProviderType = ProviderType.Invidious },
                new() { BaseUri = new Uri("https://inv.nadeko.net"), Name = "nadeko.net", ProviderType = ProviderType.Invidious },
                new() { BaseUri = new Uri("https://invidious.nerdvpn.de"), Name = "nerdvpn.de", ProviderType = ProviderType.Invidious }
            },
            _ => Array.Empty<InstanceInfo>()
        };
    }

    private record CachedInstanceDto
    {
        public string BaseUri { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsHealthy { get; init; }
        public long LatencyMs { get; init; }
        public DateTime? LastCheckedUtc { get; init; }
    }
}
