using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Services;

/// <summary>
/// Probes public instances using documented API endpoints, measures latency,
/// manages cooldowns for failed/rate-limited servers, and selects the highest-performing instance.
/// </summary>
public sealed class InstanceHealthService : IInstanceHealthService
{
    private readonly HttpClient _httpClient;
    private readonly IPublicInstanceDirectory _directory;
    private readonly IConfigurationService _configService;
    private readonly ILogger<InstanceHealthService> _logger;

    private sealed class RuntimeState
    {
        public required PublicInstance Instance { get; set; }
        public DateTimeOffset? CooldownUntilUtc { get; set; }
        public DateTimeOffset? LastProbedUtc { get; set; }
        public bool LastProbeSuccess { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    private readonly ConcurrentDictionary<Uri, RuntimeState> _stateMap = new();

    public InstanceHealthService(
        HttpClient httpClient,
        IPublicInstanceDirectory directory,
        IConfigurationService configService,
        ILogger<InstanceHealthService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<InstanceHealthMeasurement> ProbeInstanceAsync(
        PublicInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var probeUri = instance.ProviderType switch
        {
            BackendProviderType.Piped => new Uri(instance.BaseUri, "trending?region=US"),
            BackendProviderType.Invidious => new Uri(instance.BaseUri, "api/v1/stats"),
            _ => instance.BaseUri
        };

        var stopwatch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3)); // Health probe timeout

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
            // Privacy: Send NO authorization, cookies, or user credentials
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            stopwatch.Stop();

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) // 429 Rate limited
            {
                RecordFailure(instance, "Rate limited (HTTP 429)");
                return new InstanceHealthMeasurement
                {
                    InstanceUri = instance.BaseUri,
                    ProviderType = instance.ProviderType,
                    Latency = stopwatch.Elapsed,
                    IsSuccessful = false,
                    StatusCode = (int)response.StatusCode,
                    ErrorMessage = "Rate limited (HTTP 429)"
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                RecordFailure(instance, error);
                return new InstanceHealthMeasurement
                {
                    InstanceUri = instance.BaseUri,
                    ProviderType = instance.ProviderType,
                    Latency = stopwatch.Elapsed,
                    IsSuccessful = false,
                    StatusCode = (int)response.StatusCode,
                    ErrorMessage = error
                };
            }

            // Verify lightweight JSON shape
            var content = await response.Content.ReadAsStringAsync(cts.Token);
            var isShapeValid = VerifyJsonShape(instance.ProviderType, content);

            if (!isShapeValid)
            {
                RecordFailure(instance, "Invalid API JSON response shape");
                return new InstanceHealthMeasurement
                {
                    InstanceUri = instance.BaseUri,
                    ProviderType = instance.ProviderType,
                    Latency = stopwatch.Elapsed,
                    IsSuccessful = false,
                    StatusCode = (int)response.StatusCode,
                    ErrorMessage = "Invalid API JSON response shape"
                };
            }

            RecordSuccess(instance, stopwatch.Elapsed);
            return new InstanceHealthMeasurement
            {
                InstanceUri = instance.BaseUri,
                ProviderType = instance.ProviderType,
                Latency = stopwatch.Elapsed,
                IsSuccessful = true,
                StatusCode = (int)response.StatusCode
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var error = "Health check timed out (5s limit)";
            RecordFailure(instance, error);
            return new InstanceHealthMeasurement
            {
                InstanceUri = instance.BaseUri,
                ProviderType = instance.ProviderType,
                Latency = stopwatch.Elapsed,
                IsSuccessful = false,
                ErrorMessage = error
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var error = ex.Message;
            RecordFailure(instance, error);
            return new InstanceHealthMeasurement
            {
                InstanceUri = instance.BaseUri,
                ProviderType = instance.ProviderType,
                Latency = stopwatch.Elapsed,
                IsSuccessful = false,
                ErrorMessage = error
            };
        }
    }

    public async Task<PublicInstance?> SelectBestInstanceAsync(
        BackendProviderType providerType,
        CancellationToken cancellationToken = default)
    {
        var instances = await _directory.GetInstancesAsync(providerType, forceRefresh: false, cancellationToken);
        if (instances.Count == 0)
            return null;

        var now = DateTimeOffset.UtcNow;

        // Synchronize in-memory runtime states
        foreach (var inst in instances)
        {
            _stateMap.GetOrAdd(inst.BaseUri, uri => new RuntimeState { Instance = inst });
        }

        // Rank candidates: Exclude active cooldowns, sort by priority score
        var availableCandidates = instances
            .Where(inst =>
            {
                if (_stateMap.TryGetValue(inst.BaseUri, out var state))
                {
                    return !state.CooldownUntilUtc.HasValue || state.CooldownUntilUtc.Value <= now;
                }
                return true;
            })
            .OrderBy(inst =>
            {
                if (_stateMap.TryGetValue(inst.BaseUri, out var state))
                    return state.Instance.PriorityScore;
                return inst.PriorityScore;
            })
            .ToList();

        if (availableCandidates.Count == 0)
        {
            _logger.LogWarning("All {Provider} instances are on cooldown. Resetting earliest cooldown.", providerType);
            // If all candidates are on cooldown, pick the one with earliest expiration
            var earliest = _stateMap.Values
                .Where(s => s.Instance.ProviderType == providerType)
                .OrderBy(s => s.CooldownUntilUtc)
                .FirstOrDefault();

            if (earliest != null)
            {
                earliest.CooldownUntilUtc = null;
                return earliest.Instance;
            }

            return instances[0];
        }

        // Probe up to top 3 candidates if their probe is stale (> 5 minutes old)
        int attempts = 0;
        const int maxAttempts = 3;

        foreach (var candidate in availableCandidates)
        {
            if (attempts++ >= maxAttempts)
                break;

            var state = _stateMap[candidate.BaseUri];

            // If recently verified (< 5 mins), return immediately to avoid hammering public servers
            if (state.LastProbeSuccess &&
                state.LastProbedUtc.HasValue &&
                (now - state.LastProbedUtc.Value).TotalMinutes < 5)
            {
                return state.Instance;
            }

            // Probe candidate
            var measurement = await ProbeInstanceAsync(state.Instance, cancellationToken);
            if (measurement.IsSuccessful)
            {
                return state.Instance;
            }
        }

        // Return best remaining if any succeeded, or first available
        var selected = availableCandidates.FirstOrDefault();
        return selected != null && _stateMap.TryGetValue(selected.BaseUri, out var selectedState) ? selectedState.Instance : selected;
    }

    public void RecordSuccess(PublicInstance instance, TimeSpan latency)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var state = _stateMap.GetOrAdd(instance.BaseUri, uri => new RuntimeState { Instance = instance });
        state.ConsecutiveFailures = 0;
        state.CooldownUntilUtc = null;
        state.LastProbedUtc = DateTimeOffset.UtcNow;
        state.LastProbeSuccess = true;

        var updatedInstance = state.Instance with
        {
            IsWorking = true,
            LatencyMs = (long)latency.TotalMilliseconds,
            ConsecutiveFailures = 0,
            LastCheckedUtc = DateTimeOffset.UtcNow,
            HealthScore = Math.Min(100.0, state.Instance.HealthScore + 2.0),
            ErrorMessage = null
        };

        state.Instance = updatedInstance;
    }

    public void RecordFailure(PublicInstance instance, string reason)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var state = _stateMap.GetOrAdd(instance.BaseUri, uri => new RuntimeState { Instance = instance });
        state.ConsecutiveFailures++;
        state.LastProbedUtc = DateTimeOffset.UtcNow;
        state.LastProbeSuccess = false;

        // Rate limiting (HTTP 429) triggers longer cooldown
        TimeSpan cooldown = reason.Contains("429")
            ? TimeSpan.FromMinutes(15)
            : TimeSpan.FromMinutes(Math.Min(30, 2 * state.ConsecutiveFailures));

        state.CooldownUntilUtc = DateTimeOffset.UtcNow.Add(cooldown);

        var isStillWorking = state.ConsecutiveFailures < 4;
        var updatedInstance = state.Instance with
        {
            IsWorking = isStillWorking,
            ConsecutiveFailures = state.ConsecutiveFailures,
            LastCheckedUtc = DateTimeOffset.UtcNow,
            HealthScore = Math.Max(0.0, state.Instance.HealthScore - 15.0),
            ErrorMessage = reason
        };

        state.Instance = updatedInstance;
        _logger.LogWarning("Recorded failure for {Uri}: {Reason}. Cooldown until {Cooldown}",
            instance.BaseUri, reason, state.CooldownUntilUtc);
    }

    private static bool VerifyJsonShape(BackendProviderType providerType, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (providerType == BackendProviderType.Piped)
            {
                // Piped /trending returns an array of video items
                return doc.RootElement.ValueKind == JsonValueKind.Array;
            }
            if (providerType == BackendProviderType.Invidious)
            {
                // Invidious /api/v1/stats returns an object with "version" or "software"
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return false;

                return doc.RootElement.TryGetProperty("software", out _) ||
                       doc.RootElement.TryGetProperty("version", out _);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
