using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Services;

/// <summary>
/// Discovers public API instances for Piped and Invidious from official project directories,
/// validates entries, and manages local persistence fallback.
/// </summary>
public sealed class PublicInstanceDirectory : IPublicInstanceDirectory
{
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configService;
    private readonly FileInstanceCache _cache;
    private readonly ILogger<PublicInstanceDirectory> _logger;

    public PublicInstanceDirectory(
        HttpClient httpClient,
        IConfigurationService configService,
        FileInstanceCache cache,
        ILogger<PublicInstanceDirectory> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<PublicInstance>> GetInstancesAsync(
        BackendProviderType providerType,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var config = _configService.CurrentConfig.Backend;
        var seedList = GetSeedInstances(providerType, config);

        // 1. Check local cache if forceRefresh is false
        if (!forceRefresh)
        {
            var cachedSnapshot = await _cache.LoadSnapshotAsync(providerType, cancellationToken);
            if (cachedSnapshot != null && cachedSnapshot.Instances.Count > 0)
            {
                var cacheAge = DateTimeOffset.UtcNow - cachedSnapshot.CachedAtUtc;
                if (cacheAge.TotalHours < config.InstanceCacheTtlHours)
                {
                    _logger.LogDebug("Using {Count} cached {Provider} instances (cache age: {Age:F1}h)",
                        cachedSnapshot.Instances.Count, providerType, cacheAge.TotalHours);
                    return cachedSnapshot.Instances;
                }
            }
        }

        // 2. Fetch from official live directory
        try
        {
            _logger.LogInformation("Fetching live directory for {Provider}...", providerType);
            var discovered = providerType switch
            {
                BackendProviderType.Piped => await FetchPipedInstancesAsync(config.PipedInstancesDirectoryUrl, cancellationToken),
                BackendProviderType.Invidious => await FetchInvidiousInstancesAsync(config.InvidiousInstancesDirectoryUrl, cancellationToken),
                _ => new List<PublicInstance>()
            };

            var mergedDiscovered = new List<PublicInstance>(seedList);
            foreach (var d in discovered)
            {
                if (!mergedDiscovered.Any(m => m.BaseUri == d.BaseUri))
                {
                    mergedDiscovered.Add(d);
                }
            }

            if (mergedDiscovered.Count > 0)
            {
                _logger.LogInformation("Discovered {Count} valid public {Provider} instances ({Discovered} live, {Seeds} seeds).",
                    mergedDiscovered.Count, providerType, discovered.Count, seedList.Count);
                await _cache.SaveSnapshotAsync(providerType, mergedDiscovered, cancellationToken);
                return mergedDiscovered;
            }

            _logger.LogWarning("Directory for {Provider} returned no valid instances.", providerType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch live instance directory for {Provider}.", providerType);
        }

        // 3. Fallback to stale cache if directory fetch failed
        var staleSnapshot = await _cache.LoadSnapshotAsync(providerType, cancellationToken);
        if (staleSnapshot != null && staleSnapshot.Instances.Count > 0)
        {
            _logger.LogWarning("Falling back to {Count} stale cached {Provider} instances.", staleSnapshot.Instances.Count, providerType);
            var mergedStale = new List<PublicInstance>(seedList);
            foreach (var s in staleSnapshot.Instances)
            {
                if (!mergedStale.Any(m => m.BaseUri == s.BaseUri))
                    mergedStale.Add(s);
            }
            return mergedStale;
        }

        // 4. Fallback to emergency configured seed instances
        _logger.LogWarning("No directory or cache available. Falling back to configured seed instances for {Provider}.", providerType);
        return seedList;
    }

    public Task SaveHealthyInstancesAsync(
        BackendProviderType providerType,
        IReadOnlyList<PublicInstance> instances,
        CancellationToken cancellationToken = default)
    {
        return _cache.SaveSnapshotAsync(providerType, instances, cancellationToken);
    }

    /// <summary>
    /// Parses TeamPiped documentation markdown table of public instances.
    /// URL: https://raw.githubusercontent.com/TeamPiped/documentation/main/content/docs/public-instances/index.md
    /// Table format: Instance Name | Instance API URL | Instance Location(s) | CDN | Registered Users
    /// </summary>
    public static List<PublicInstance> ParsePipedMarkdownDirectory(string markdownContent)
    {
        var results = new List<PublicInstance>();
        if (string.IsNullOrWhiteSpace(markdownContent))
            return results;

        var lines = markdownContent.Split('\n', StringSplitOptions.TrimEntries);
        var urlRegex = new Regex(@"https://[^\s|]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        foreach (var line in lines)
        {
            if (!line.Contains('|'))
                continue;

            // Skip header and separator lines
            if (line.Contains("---") || line.Contains("Instance API URL", StringComparison.OrdinalIgnoreCase))
                continue;

            var columns = line.Split('|', StringSplitOptions.TrimEntries);
            if (columns.Length < 2)
                continue;

            // Find any column that contains an https URL (usually column 1 or 2)
            string? candidateUrl = null;
            foreach (var col in columns)
            {
                var match = urlRegex.Match(col);
                if (match.Success)
                {
                    candidateUrl = match.Value;
                    break;
                }
            }

            if (candidateUrl != null && InstanceUrlValidator.TrySanitizeBaseUri(candidateUrl, out var cleanUri, out _))
            {
                if (!results.Any(r => r.BaseUri == cleanUri))
                {
                    results.Add(new PublicInstance
                    {
                        BaseUri = cleanUri,
                        ProviderType = BackendProviderType.Piped,
                        IsWorking = true,
                        HealthScore = 75.0
                    });
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Parses Invidious official directory JSON schema:
    /// URL: https://api.invidious.io/instances.json?sort_by=health
    /// Schema: array of pairs [ [ "domain", { "api": bool, "type": "https", "uri": "https://...", "monitor": { "uptime": double, "down": bool } } ] ]
    /// </summary>
    public static List<PublicInstance> ParseInvidiousDirectoryJson(string jsonContent)
    {
        var results = new List<PublicInstance>();
        if (string.IsNullOrWhiteSpace(jsonContent))
            return results;

        using var doc = JsonDocument.Parse(jsonContent);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2)
                continue;

            var details = item[1];
            if (details.ValueKind != JsonValueKind.Object)
                continue;

            // Must have API enabled: "api": true
            if (!details.TryGetProperty("api", out var apiProp) || apiProp.ValueKind != JsonValueKind.True)
                continue;

            // Must be HTTPS: "type": "https"
            if (!details.TryGetProperty("type", out var typeProp) ||
                !string.Equals(typeProp.GetString(), "https", StringComparison.OrdinalIgnoreCase))
                continue;

            // Extract URI
            if (!details.TryGetProperty("uri", out var uriProp) || uriProp.ValueKind != JsonValueKind.String)
                continue;

            var rawUri = uriProp.GetString();
            if (!InstanceUrlValidator.TrySanitizeBaseUri(rawUri, out var cleanUri, out _))
                continue;

            // Check monitor status
            double healthScore = 90.0;
            if (details.TryGetProperty("monitor", out var monitor) && monitor.ValueKind == JsonValueKind.Object)
            {
                if (monitor.TryGetProperty("down", out var downProp) && downProp.ValueKind == JsonValueKind.True)
                    continue; // Skip down instances

                if (monitor.TryGetProperty("uptime", out var uptimeProp) && uptimeProp.TryGetDouble(out var uptime))
                {
                    healthScore = uptime;
                }
            }

            string? version = null;
            if (details.TryGetProperty("stats", out var stats) &&
                stats.TryGetProperty("software", out var soft) &&
                soft.TryGetProperty("version", out var verProp))
            {
                version = verProp.GetString();
            }

            if (!results.Any(r => r.BaseUri == cleanUri))
            {
                results.Add(new PublicInstance
                {
                    BaseUri = cleanUri,
                    ProviderType = BackendProviderType.Invidious,
                    IsWorking = true,
                    HealthScore = healthScore,
                    SoftwareVersion = version
                });
            }
        }

        return results;
    }

    private async Task<List<PublicInstance>> FetchPipedInstancesAsync(string directoryUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(directoryUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var markdown = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParsePipedMarkdownDirectory(markdown);
    }

    private async Task<List<PublicInstance>> FetchInvidiousInstancesAsync(string directoryUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(directoryUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseInvidiousDirectoryJson(json);
    }

    private static List<PublicInstance> GetSeedInstances(BackendProviderType providerType, BackendConfig config)
    {
        var seeds = providerType == BackendProviderType.Piped
            ? config.PipedSeedInstances
            : config.InvidiousSeedInstances;

        var list = new List<PublicInstance>();
        foreach (var seed in seeds)
        {
            if (InstanceUrlValidator.TrySanitizeBaseUri(seed, out var cleanUri, out _))
            {
                list.Add(new PublicInstance
                {
                    BaseUri = cleanUri,
                    ProviderType = providerType,
                    IsWorking = true,
                    HealthScore = 100.0,
                    LatencyMs = 200
                });
            }
        }

        return list;
    }
}
