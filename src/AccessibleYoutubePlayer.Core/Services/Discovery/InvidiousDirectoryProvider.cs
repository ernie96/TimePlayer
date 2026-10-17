using System.Text.Json;
using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccessibleYoutubePlayer.Core.Services.Discovery;

/// <summary>
/// Discovers public Invidious API instances from official project-maintained directory (api.invidious.io).
/// Filters strictly for active HTTPS API-enabled instances, sanitizing all URLs.
/// </summary>
public class InvidiousDirectoryProvider : IInstanceDirectoryProvider
{
    public const string OfficialInvidiousDirectoryUrl = "https://api.invidious.io/instances.json?sort_by=health";

    private readonly HttpClient _httpClient;
    private readonly ILogger<InvidiousDirectoryProvider> _logger;

    public ProviderType ProviderType => ProviderType.Invidious;

    public InvidiousDirectoryProvider(HttpClient? httpClient = null, ILogger<InvidiousDirectoryProvider>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _logger = logger ?? NullLogger<InvidiousDirectoryProvider>.Instance;
    }

    public async Task<IReadOnlyList<InstanceInfo>> FetchInstancesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _httpClient.GetStringAsync(OfficialInvidiousDirectoryUrl, cancellationToken);
            return ParseInvidiousDirectory(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve Invidious instances from official directory.");
            return Array.Empty<InstanceInfo>();
        }
    }

    public static IReadOnlyList<InstanceInfo> ParseInvidiousDirectory(string json)
    {
        var list = new List<InstanceInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var pair in doc.RootElement.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;

                var domain = pair[0].GetString() ?? string.Empty;
                var meta = pair[1];

                if (meta.ValueKind != JsonValueKind.Object) continue;

                // Check type == "https" and api == true
                var type = meta.TryGetProperty("type", out var t) ? t.GetString() : "";
                var hasApi = meta.TryGetProperty("api", out var a) && a.GetBoolean();

                if (!string.Equals(type, "https", StringComparison.OrdinalIgnoreCase) || !hasApi)
                    continue;

                // Prefer explicit uri property, fallback to https://{domain}
                var rawUri = meta.TryGetProperty("uri", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(rawUri))
                {
                    rawUri = $"https://{domain}";
                }

                if (Uri.TryCreate(rawUri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                    if (Uri.TryCreate(normalized, UriKind.Absolute, out var cleanUri))
                    {
                        list.Add(new InstanceInfo
                        {
                            BaseUri = cleanUri,
                            Name = string.IsNullOrWhiteSpace(domain) ? cleanUri.Host : domain,
                            ProviderType = ProviderType.Invidious
                        });
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed JSON entries
        }

        return list;
    }
}
