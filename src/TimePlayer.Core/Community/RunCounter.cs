using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Configuration;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Core.Community;

public interface IRunCounter
{
    /// <summary>Counts this start on this computer and saves it. Returns the new count.</summary>
    Task<long> RecordRunAsync();

    /// <summary>
    /// Adds this start to the shared count when the person chose to, and returns that total.
    /// Null when they didn't, or the counter can't be reached.
    /// </summary>
    Task<long?> SendRunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// How many times TimePlayer has started: on this computer, and (if the person agrees) everyone's starts
/// added together. The shared total is a public counter on Abacus (abacus.jasoncameron.dev). Each start
/// adds 1 to it; nothing else is sent, so nobody can tell who ran TimePlayer or how often.
/// </summary>
public sealed class RunCounter : IRunCounter
{
    public const string SharedCounterUrl = "https://abacus.jasoncameron.dev/hit/timeplayer-ernie96/runs";

    private readonly HttpClient _http;
    private readonly IConfigurationService _config;
    private readonly ILogger<RunCounter> _logger;

    public RunCounter(HttpClient http, IConfigurationService config, ILogger<RunCounter> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<long> RecordRunAsync()
    {
        var current = _config.CurrentConfig;
        var count = current.Runs.Count + 1;
        try
        {
            await _config.UpdateConfigAsync(current with { Runs = current.Runs with { Count = count } });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the run count.");
        }
        return count;
    }

    public async Task<long?> SendRunAsync(CancellationToken cancellationToken = default)
    {
        if (_config.CurrentConfig.Runs.Share != true)
            return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.GetAsync(SharedCounterUrl, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("The shared run counter answered {Status}.", (int)response.StatusCode);
                return null;
            }
            return ParseTotal(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _logger.LogInformation("Could not add to the shared run counter: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Abacus answers {"value": 12345}.</summary>
    public static long? ParseTotal(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty("value", out var value) && value.TryGetInt64(out var total) && total > 0
            ? total
            : null;
    }

    /// <summary>"TimePlayer has been run 42 times on this computer. Everyone together: 12,345 times."</summary>
    public static string Spoken(long mine, long? everyone)
    {
        var text = Plural(mine, "TimePlayer has been run {0:N0} time on this computer.", "TimePlayer has been run {0:N0} times on this computer.");
        if (everyone is { } total)
            text += " " + Plural(total, "Everyone together: {0:N0} time.", "Everyone together: {0:N0} times.");
        return text;
    }
}
