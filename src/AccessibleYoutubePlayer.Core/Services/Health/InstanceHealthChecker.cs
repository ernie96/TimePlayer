using System.Diagnostics;
using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccessibleYoutubePlayer.Core.Services.Health;

/// <summary>
/// Concurrently probes candidate instances to verify live API functionality,
/// measure latency, and filter out degraded or blocked servers.
/// Uses strict concurrency caps and short timeouts to prevent service hammering.
/// </summary>
public class InstanceHealthChecker : IInstanceHealthChecker
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InstanceHealthChecker> _logger;
    private readonly TimeSpan _probeTimeout;

    public InstanceHealthChecker(
        HttpClient? httpClient = null,
        TimeSpan? probeTimeout = null,
        ILogger<InstanceHealthChecker>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(4);
        _logger = logger ?? NullLogger<InstanceHealthChecker>.Instance;
    }

    public async Task<InstanceInfo> ProbeInstanceAsync(InstanceInfo instance, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_probeTimeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var probePath = instance.ProviderType switch
            {
                ProviderType.Piped => "/search?q=test&filter=all",
                ProviderType.Invidious => "/api/v1/search?q=test&type=video",
                _ => "/"
            };

            var probeUri = new Uri(instance.BaseUri, probePath);
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
            request.Headers.Add("User-Agent", "AccessibleYoutubePlayer/1.0");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                return instance with
                {
                    IsHealthy = true,
                    LatencyMs = Math.Max(1, stopwatch.ElapsedMilliseconds),
                    LastCheckedUtc = DateTime.UtcNow,
                    LastError = null
                };
            }

            return instance with
            {
                IsHealthy = false,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                LastCheckedUtc = DateTime.UtcNow,
                LastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return instance with
            {
                IsHealthy = false,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                LastCheckedUtc = DateTime.UtcNow,
                LastError = "Connection timed out"
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return instance with
            {
                IsHealthy = false,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                LastCheckedUtc = DateTime.UtcNow,
                LastError = ex.Message
            };
        }
    }

    public async Task<IReadOnlyList<InstanceInfo>> FilterAndRankInstancesAsync(
        IEnumerable<InstanceInfo> instances,
        int maxConcurrency = 4,
        int maxWorkingToKeep = 5,
        CancellationToken cancellationToken = default)
    {
        var candidates = instances.ToList();
        if (candidates.Count == 0) return Array.Empty<InstanceInfo>();

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = candidates.Select(async inst =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await ProbeInstanceAsync(inst, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        // Filter for healthy instances, ordered by lowest latency
        var healthy = results
            .Where(r => r.IsHealthy && r.LatencyMs >= 0)
            .OrderBy(r => r.LatencyMs)
            .Take(maxWorkingToKeep)
            .ToList();

        _logger.LogInformation("Tested {Total} instances. Found {HealthyCount} healthy instances.", candidates.Count, healthy.Count);
        return healthy;
    }
}
