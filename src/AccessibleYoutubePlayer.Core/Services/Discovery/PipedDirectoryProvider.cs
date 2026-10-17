using System.Text.Json;
using System.Text.RegularExpressions;
using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccessibleYoutubePlayer.Core.Services.Discovery;

/// <summary>
/// Discovers public Piped API instances from official project-maintained sources.
/// Enforces HTTPS, drops malformed or tracking URLs, and returns sanitized InstanceInfo objects.
/// </summary>
public partial class PipedDirectoryProvider : IInstanceDirectoryProvider
{
    public const string OfficialMarkdownDocUrl = "https://raw.githubusercontent.com/TeamPiped/Documentation/main/content/docs/public-instances/index.md";
    public const string OfficialApiDirectoryUrl = "https://piped-instances.kavin.rocks/";

    private readonly HttpClient _httpClient;
    private readonly ILogger<PipedDirectoryProvider> _logger;

    public ProviderType ProviderType => ProviderType.Piped;

    public PipedDirectoryProvider(HttpClient? httpClient = null, ILogger<PipedDirectoryProvider>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _logger = logger ?? NullLogger<PipedDirectoryProvider>.Instance;
    }

    public async Task<IReadOnlyList<InstanceInfo>> FetchInstancesAsync(CancellationToken cancellationToken = default)
    {
        var discovered = new Dictionary<string, InstanceInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Try official project documentation markdown
        try
        {
            var markdown = await _httpClient.GetStringAsync(OfficialMarkdownDocUrl, cancellationToken);
            var parsedFromMarkdown = ParseMarkdownInstances(markdown);
            foreach (var inst in parsedFromMarkdown)
            {
                discovered[inst.BaseUri.Host] = inst;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve Piped instances from official Documentation markdown. Trying fallback API directory.");
        }

        // 2. If needed or as secondary enrichment, try the kavin.rocks API directory
        if (discovered.Count == 0)
        {
            try
            {
                var json = await _httpClient.GetStringAsync(OfficialApiDirectoryUrl, cancellationToken);
                var parsedFromJson = ParseJsonDirectory(json);
                foreach (var inst in parsedFromJson)
                {
                    discovered[inst.BaseUri.Host] = inst;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve Piped instances from kavin.rocks JSON directory.");
            }
        }

        return discovered.Values.ToList();
    }

    public static IReadOnlyList<InstanceInfo> ParseMarkdownInstances(string markdown)
    {
        var list = new List<InstanceInfo>();
        if (string.IsNullOrWhiteSpace(markdown)) return list;

        // Matches lines in the markdown table: Name | API URL | ...
        var lines = markdown.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains('|') || trimmed.StartsWith("---") || trimmed.StartsWith("Instance Name"))
                continue;

            var parts = trimmed.Split('|');
            if (parts.Length < 2) continue;

            var name = parts[0].Trim();
            var rawUrl = parts[1].Trim();

            if (TrySanitizeHttpsUrl(rawUrl, out var validUri))
            {
                list.Add(new InstanceInfo
                {
                    BaseUri = validUri,
                    Name = string.IsNullOrWhiteSpace(name) ? validUri.Host : name,
                    ProviderType = ProviderType.Piped
                });
            }
        }

        return list;
    }

    public static IReadOnlyList<InstanceInfo> ParseJsonDirectory(string json)
    {
        var list = new List<InstanceInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var apiUrl = el.TryGetProperty("api_url", out var u) ? u.GetString() ?? "" : "";

                if (TrySanitizeHttpsUrl(apiUrl, out var validUri))
                {
                    list.Add(new InstanceInfo
                    {
                        BaseUri = validUri,
                        Name = string.IsNullOrWhiteSpace(name) ? validUri.Host : name,
                        ProviderType = ProviderType.Piped
                    });
                }
            }
        }
        catch
        {
            // Ignore malformed JSON
        }

        return list;
    }

    private static bool TrySanitizeHttpsUrl(string raw, out Uri validUri)
    {
        validUri = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Extract URL if wrapped in markdown link [text](url)
        var match = UrlRegex().Match(raw);
        var candidate = match.Success ? match.Value : raw;

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            // Security rule: Only HTTPS permitted
            if (uri.Scheme == Uri.UriSchemeHttps &&
                !string.IsNullOrWhiteSpace(uri.Host) &&
                !uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !uri.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                // Normalize by stripping trailing slashes
                var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                if (Uri.TryCreate(normalized, UriKind.Absolute, out var cleanUri))
                {
                    validUri = cleanUri;
                    return true;
                }
            }
        }

        return false;
    }

    [GeneratedRegex(@"https://[^\s\)\]\|]+")]
    private static partial Regex UrlRegex();
}
