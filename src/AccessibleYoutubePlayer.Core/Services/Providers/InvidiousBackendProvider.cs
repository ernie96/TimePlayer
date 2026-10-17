using System.Text.Json;
using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;
using AccessibleYoutubePlayer.Core.Services.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccessibleYoutubePlayer.Core.Services.Providers;

/// <summary>
/// Secondary public backend provider using the Invidious API.
/// Operates as the secondary failover tier when Piped instances are unavailable.
/// </summary>
public class InvidiousBackendProvider : IYouTubeBackendProvider
{
    private readonly HttpClient _httpClient;
    private readonly IInstanceDirectoryProvider _directoryProvider;
    private readonly IInstanceCache _cache;
    private readonly IInstanceHealthChecker _healthChecker;
    private readonly ILogger<InvidiousBackendProvider> _logger;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private List<InstanceInfo> _workingInstances = new();
    private int _currentInstanceIndex = 0;

    public ProviderType ProviderType => ProviderType.Invidious;

    public InvidiousBackendProvider(
        IInstanceDirectoryProvider directoryProvider,
        IInstanceCache cache,
        IInstanceHealthChecker healthChecker,
        HttpClient? httpClient = null,
        ILogger<InvidiousBackendProvider>? logger = null)
    {
        _directoryProvider = directoryProvider;
        _cache = cache;
        _healthChecker = healthChecker;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger ?? NullLogger<InvidiousBackendProvider>.Instance;
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_workingInstances.Count > 0) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_workingInstances.Count > 0) return;

            var candidates = await _cache.LoadCachedInstancesAsync(ProviderType.Invidious, cancellationToken);
            if (candidates == null || candidates.Count == 0)
            {
                candidates = await _directoryProvider.FetchInstancesAsync(cancellationToken);
            }

            var healthy = await _healthChecker.FilterAndRankInstancesAsync(candidates ?? Array.Empty<InstanceInfo>(), 4, 5, cancellationToken);
            if (healthy.Count > 0)
            {
                _workingInstances = healthy.ToList();
                _currentInstanceIndex = 0;
                await _cache.SaveCachedInstancesAsync(ProviderType.Invidious, _workingInstances, cancellationToken);
            }
            else
            {
                _workingInstances = FileInstanceCache.GetEmergencySeeds(ProviderType.Invidious).ToList();
                _currentInstanceIndex = 0;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    public InstanceInfo? GetCurrentInstance()
    {
        if (_workingInstances.Count == 0) return null;
        return _workingInstances[_currentInstanceIndex % _workingInstances.Count];
    }

    public void RotateInstance()
    {
        if (_workingInstances.Count > 1)
        {
            _currentInstanceIndex = (_currentInstanceIndex + 1) % _workingInstances.Count;
            _logger.LogInformation("Rotated to next Invidious instance: {Instance}", GetCurrentInstance()?.BaseUri);
        }
    }

    public async Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var attempts = 0;
        var maxAttempts = Math.Max(1, _workingInstances.Count);

        while (attempts < maxAttempts)
        {
            attempts++;
            var instance = GetCurrentInstance();
            if (instance == null) break;

            try
            {
                var uri = new Uri(instance.BaseUri, $"/api/v1/search?q={Uri.EscapeDataString(query)}&type=video");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Add("User-Agent", "AccessibleYoutubePlayer/1.0");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var items = ParseInvidiousSearchJson(json);

                return new SearchResult
                {
                    Items = items,
                    Query = query,
                    ProviderUsed = ProviderType.Invidious,
                    InstanceUsed = instance.Name
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invidious search failed on {Instance}. Rotating instance.", instance.BaseUri);
                RotateInstance();
            }
        }

        throw new InvalidOperationException("All configured Invidious instances failed for search.");
    }

    public async Task<VideoMetadata> GetVideoDetailsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var instance = GetCurrentInstance() ?? throw new InvalidOperationException("No Invidious instance available.");

        var uri = new Uri(instance.BaseUri, $"/api/v1/videos/{Uri.EscapeDataString(videoId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("User-Agent", "AccessibleYoutubePlayer/1.0");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var author = root.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
        var lengthSeconds = root.TryGetProperty("lengthSeconds", out var l) ? l.GetInt64() : 0;
        var viewCount = root.TryGetProperty("viewCount", out var v) ? v.GetInt64() : 0;
        var desc = root.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
        var isLive = root.TryGetProperty("liveNow", out var ln) && ln.GetBoolean();

        return new VideoMetadata
        {
            Id = videoId,
            Title = title,
            Author = author,
            Duration = TimeSpan.FromSeconds(lengthSeconds),
            ViewCount = viewCount,
            Description = desc,
            IsLive = isLive
        };
    }

    public async Task<IReadOnlyList<StreamInfo>> GetStreamsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var attempts = 0;
        var maxAttempts = Math.Max(1, _workingInstances.Count);

        while (attempts < maxAttempts)
        {
            attempts++;
            var instance = GetCurrentInstance();
            if (instance == null) break;

            try
            {
                var uri = new Uri(instance.BaseUri, $"/api/v1/videos/{Uri.EscapeDataString(videoId)}");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Add("User-Agent", "AccessibleYoutubePlayer/1.0");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseInvidiousStreamsJson(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve streams on Invidious instance {Instance}. Rotating.", instance.BaseUri);
                RotateInstance();
            }
        }

        throw new InvalidOperationException("Failed to retrieve streams from any Invidious instance.");
    }

    public async Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(string videoId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var instance = GetCurrentInstance() ?? throw new InvalidOperationException("No Invidious instance available.");

        var uri = new Uri(instance.BaseUri, $"/api/v1/videos/{Uri.EscapeDataString(videoId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("User-Agent", "AccessibleYoutubePlayer/1.0");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var list = new List<SubtitleTrack>();
        if (root.TryGetProperty("captions", out var captions) && captions.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in captions.EnumerateArray())
            {
                var label = c.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                var code = c.TryGetProperty("languageCode", out var lc) ? lc.GetString() ?? "" : "";
                var url = c.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(url))
                {
                    // Resolve relative URLs against instance base
                    var fullUrl = url.StartsWith("http") ? url : new Uri(instance.BaseUri, url).ToString();
                    list.Add(new SubtitleTrack
                    {
                        Language = code,
                        LanguageName = string.IsNullOrWhiteSpace(label) ? code : label,
                        Url = fullUrl,
                        MimeType = "text/vtt"
                    });
                }
            }
        }

        return list;
    }

    public static IReadOnlyList<VideoMetadata> ParseInvidiousSearchJson(string json)
    {
        var list = new List<VideoMetadata>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var ty) ? ty.GetString() : "video";
            if (!string.Equals(type, "video", StringComparison.OrdinalIgnoreCase)) continue;

            var id = item.TryGetProperty("videoId", out var vid) ? vid.GetString() ?? "" : "";
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
            var length = item.TryGetProperty("lengthSeconds", out var ls) ? ls.GetInt64() : 0;
            var views = item.TryGetProperty("viewCount", out var vc) ? vc.GetInt64() : 0;
            var published = item.TryGetProperty("publishedText", out var pt) ? pt.GetString() ?? "" : "";
            var isLive = item.TryGetProperty("liveNow", out var ln) && ln.GetBoolean();

            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title))
            {
                list.Add(new VideoMetadata
                {
                    Id = id,
                    Title = title,
                    Author = author,
                    Duration = TimeSpan.FromSeconds(Math.Max(0, length)),
                    ViewCount = Math.Max(0, views),
                    PublishedText = published,
                    IsLive = isLive
                });
            }
        }

        return list;
    }

    public static IReadOnlyList<StreamInfo> ParseInvidiousStreamsJson(string json)
    {
        var list = new List<StreamInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Adaptive Formats (contains separated audio and video streams)
        if (root.TryGetProperty("adaptiveFormats", out var adaptive) && adaptive.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in adaptive.EnumerateArray())
            {
                var url = s.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var type = s.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                var bitrate = s.TryGetProperty("bitrate", out var b) ? b.GetInt32() : 0;
                var quality = s.TryGetProperty("qualityLabel", out var ql) ? ql.GetString() ?? "" : "";
                var isAudio = type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(url))
                {
                    list.Add(new StreamInfo
                    {
                        Url = url,
                        Format = isAudio ? "audio" : "video",
                        Quality = quality,
                        Bitrate = bitrate,
                        MimeType = type,
                        IsAudioOnly = isAudio
                    });
                }
            }
        }

        // Format streams (muxed audio+video)
        if (root.TryGetProperty("formatStreams", out var formatStreams) && formatStreams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in formatStreams.EnumerateArray())
            {
                var url = s.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var quality = s.TryGetProperty("qualityLabel", out var ql) ? ql.GetString() ?? "" : "";
                var type = s.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(url))
                {
                    list.Add(new StreamInfo
                    {
                        Url = url,
                        Format = "video/mp4",
                        Quality = quality,
                        MimeType = type,
                        IsAudioOnly = false
                    });
                }
            }
        }

        return list;
    }
}
