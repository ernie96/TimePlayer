using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Services;

public record CachedInstancesSnapshot
{
    public BackendProviderType ProviderType { get; init; }
    public DateTimeOffset CachedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<PublicInstance> Instances { get; init; } = Array.Empty<PublicInstance>();
}

/// <summary>
/// File-based persistence for discovered public instances under LocalApplicationData.
/// Never allows cached records to bypass URL security validation.
/// </summary>
public sealed class FileInstanceCache
{
    private readonly string _cacheDirectory;
    private readonly ILogger<FileInstanceCache>? _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public FileInstanceCache(string? cacheDirectory = null, ILogger<FileInstanceCache>? logger = null)
    {
        _logger = logger;
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TimePlayer",
            "cache");
    }

    private string GetCacheFilePath(BackendProviderType providerType)
    {
        return Path.Combine(_cacheDirectory, $"instances_{providerType.ToString().ToLowerInvariant()}.json");
    }

    public async Task<CachedInstancesSnapshot?> LoadSnapshotAsync(BackendProviderType providerType, CancellationToken cancellationToken = default)
    {
        var filePath = GetCacheFilePath(providerType);
        if (!File.Exists(filePath))
        {
            _logger?.LogDebug("No local cache file found at {FilePath}", filePath);
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            var snapshot = await JsonSerializer.DeserializeAsync<CachedInstancesSnapshot>(stream, _jsonOptions, cancellationToken);
            if (snapshot == null || snapshot.Instances.Count == 0)
                return null;

            // Re-validate every cached instance URI for security
            var sanitizedList = new List<PublicInstance>();
            foreach (var inst in snapshot.Instances)
            {
                if (InstanceUrlValidator.IsValidInstanceUri(inst.BaseUri, out var reason))
                {
                    sanitizedList.Add(inst);
                }
                else
                {
                    _logger?.LogWarning("Discarding cached instance {Uri}: {Reason}", inst.BaseUri, reason);
                }
            }

            return snapshot with { Instances = sanitizedList };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to read cached instances from {FilePath}", filePath);
            return null;
        }
    }

    public async Task SaveSnapshotAsync(BackendProviderType providerType, IReadOnlyList<PublicInstance> instances, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instances);

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var filePath = GetCacheFilePath(providerType);

            // Filter before saving to guarantee only valid instances are saved
            var validInstances = instances
                .Where(inst => InstanceUrlValidator.IsValidInstanceUri(inst.BaseUri, out _))
                .ToList();

            var snapshot = new CachedInstancesSnapshot
            {
                ProviderType = providerType,
                CachedAtUtc = DateTimeOffset.UtcNow,
                Instances = validInstances
            };

            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken);

            _logger?.LogInformation("Saved {Count} {Provider} instances to cache at {FilePath}",
                validInstances.Count, providerType, filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save instances cache for {Provider}", providerType);
        }
    }
}
