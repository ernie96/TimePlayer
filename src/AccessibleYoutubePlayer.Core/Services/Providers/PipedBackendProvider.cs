using System.Text.Json;
using System.Web;
using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;
using AccessibleYoutubePlayer.Core.Services.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccessibleYoutubePlayer.Core.Services.Providers;

/// <summary>
/// Primary public backend provider using the Piped API.
/// Automatically rotates to backup instances if requests fail.
/// </summary>
public class PipedBackendProvider : IYouTubeBackendProvider
{
    private readonly HttpClient _httpClient;
    private readonly IInstanceDirectoryProvider _directoryProvider;
    private readonly IInstanceCache _cache;
    private readonly IInstanceHealthChecker _healthChecker;
    private readonly ILogger<PipedBackendProvider> _logger;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private List<InstanceInfo> _workingInstances = new();
    private int _currentInstanceIndex = 0;

    public ProviderType ProviderType => ProviderType.Piped;

    public PipedBackendProvider(
        IInstanceDirectoryProvider directoryProvider,
        IInstanceCache cache,
        IInstanceHealthChecker healthChecker,
        HttpClient? httpClient = null,
        ILogger<PipedBackendProvider>? logger = null)
    {
        _directoryProvider = directoryProvider;
        _cache = cache;
        _healthChecker = healthChecker;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger ?? NullLogger<PipedBackendProvider>.Instance;
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_workingInstances.Count > 0) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_workingInstances.Count > 0) return;

            // 1. Try cached instances
            var candidates = await _cache.LoadCachedInstancesAsync(ProviderType.Piped, cancellationToken);

            // 2. If no cache, discover from directory
            if (candidates == null || candidates.Count == 0)
            {
                candidates = await _directoryProvider.FetchInstancesAsync(cancellationToken);
            }

            // 3. Health check & rank
            var healthy = await _healthChecker.FilterAndRankInstancesAsync(candidates ?? Array.Empty<InstanceInfo>(), 4, 5, cancellationToken);
            if (healthy.Count > 0)
            {
                _workingInstances = healthy.ToList();
                _currentInstanceIndex = 0;
                await _cache.SaveCachedInstancesAsync(ProviderType.Piped, _workingInstances, cancellationToken);
            }
            else
            {
                // Fallback to emergency seeds
                _workingInstances = FileInstanceCache.GetEmergencySeeds(ProviderType.Piped).ToList();
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
            _logger.LogInformation("Rotated to next Piped instance: {Instance}", GetCurrentInstance()?.BaseUri);
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
                var uri = new Uri(instance.BaseUri, $"/search?q={Uri.EscapeDataString(query)}&filter=all");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Add("User-Agent", "AccessibleYoutubePlayer/1.0");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var items = ParsePipedSearchJson(json);

                return new SearchResult
                {
                    Items = items,
                    Query = query,
                    ProviderUsed = ProviderType.Piped,
                    InstanceUsed = instance.Name
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Piped search failed on {Instance}. Rotating instance.", instance.BaseUri);
                RotateInstance();
            }
        }

        throw new InvalidOperationException("All configured Piped instances failed for search.");
    }

    public async Task<VideoMetadata> GetVideoDetailsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var instance = GetCurrentInstance() ?? throw new InvalidOperationException("No Piped instance available.");

        var uri = new Uri(instance.BaseUri, $"/streams/{Uri.EscapeDataString(videoId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("User-Agent", "AccessibleYoutubePlayer/1.0");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var author = root.TryGetProperty("uploader", out var u) ? u.GetString() ?? "" : "";
        var durationSec = root.TryGetProperty("duration", out var d) ? d.GetInt64() : 0;
        var views = root.TryGetProperty("views", out var v) ? v.GetInt64() : 0;
        var desc = root.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
        var thumb = root.TryGetProperty("thumbnailUrl", out var th) ? th.GetString() ?? "" : "";

        return new VideoMetadata
        {
            Id = videoId,
            Title = title,
            Author = author,
            Duration = TimeSpan.FromSeconds(durationSec),
            ViewCount = views,
            Description = desc,
            ThumbnailUrl = thumb
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
                var uri = new Uri(instance.BaseUri, $"/streams/{Uri.EscapeDataString(videoId)}");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Add("User-Agent", "AccessibleYoutubePlayer/1.0");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParsePipedStreamsJson(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve streams on Piped instance {Instance}. Rotating.", instance.BaseUri);
                RotateInstance();
            }
        }

        throw new InvalidOperationException("Failed to retrieve streams from any Piped instance.");
    }

    public async Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(string videoId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var instance = GetCurrentInstance() ?? throw new InvalidOperationException("No Piped instance available.");

        var uri = new Uri(instance.BaseUri, $"/streams/{Uri.EscapeDataString(videoId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("User-Agent", "AccessibleYoutubePlayer/1.0");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var list = new List<SubtitleTrack>();
        if (root.TryGetProperty("subtitles", out var subs) && subs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in subs.EnumerateArray())
            {
                var url = s.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var code = s.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                var auto = s.TryGetProperty("autoGenerated", out var a) && a.GetBoolean();
                var mime = s.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "text/vtt" : "text/vtt";

                if (!string.IsNullOrWhiteSpace(url))
                {
                    list.Add(new SubtitleTrack
                    {
                        Language = code,
                        LanguageName = string.IsNullOrWhiteSpace(name) ? code : name,
                        Url = url,
                        MimeType = mime,
                        IsAutoGenerated = auto
                    });
                }
            }
        }

        return list;
    }

    public static IReadOnlyList<VideoMetadata> ParsePipedSearchJson(string json)
    {
        var list = new List<VideoMetadata>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement itemsArray;
        if (root.ValueKind == JsonValueKind.Array)
        {
            itemsArray = root;
        }
        else if (root.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array)
        {
            itemsArray = it;
        }
        else
        {
            return list;
        }

        foreach (var item in itemsArray.EnumerateArray())
        {
            var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var uploader = item.TryGetProperty("uploaderName", out var up) ? up.GetString() ?? "" : "";
            var duration = item.TryGetProperty("duration", out var d) ? d.GetInt64() : 0;
            var views = item.TryGetProperty("views", out var v) ? v.GetInt64() : 0;
            var uploaded = item.TryGetProperty("uploadedDate", out var ud) ? ud.GetString() ?? "" : "";
            var thumb = item.TryGetProperty("thumbnail", out var th) ? th.GetString() ?? "" : "";
            var isLive = item.TryGetProperty("isLive", out var il) && il.GetBoolean();

            // Extract video ID from URL: e.g. /watch?v=abcd or abcd
            var videoId = ExtractVideoId(url);
            if (!string.IsNullOrWhiteSpace(videoId) && !string.IsNullOrWhiteSpace(title))
            {
                list.Add(new VideoMetadata
                {
                    Id = videoId,
                    Title = title,
                    Author = uploader,
                    Duration = TimeSpan.FromSeconds(Math.Max(0, duration)),
                    ViewCount = Math.Max(0, views),
                    PublishedText = uploaded,
                    ThumbnailUrl = thumb,
                    IsLive = isLive
                });
            }
        }

        return list;
    }

    public static IReadOnlyList<StreamInfo> ParsePipedStreamsJson(string json)
    {
        var list = new List<StreamInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Video + Audio streams
        if (root.TryGetProperty("videoStreams", out var vStreams) && vStreams.ValueKind == JsonValueKind.Array)
        {
            foreach (var vs in vStreams.EnumerateArray())
            {
                var url = vs.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var format = vs.TryGetProperty("format", out var f) ? f.GetString() ?? "" : "";
                var quality = vs.TryGetProperty("quality", out var q) ? q.GetString() ?? "" : "";
                var bitrate = vs.TryGetProperty("bitrate", out var b) ? b.GetInt32() : 0;
                var mime = vs.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
                var codec = vs.TryGetProperty("codec", out var c) ? c.GetString() ?? "" : "";
                var width = vs.TryGetProperty("width", out var w) ? w.GetInt32() : (int?)null;
                var height = vs.TryGetProperty("height", out var h) ? h.GetInt32() : (int?)null;
                var fps = vs.TryGetProperty("fps", out var fp) ? fp.GetInt32() : (int?)null;

                if (!string.IsNullOrWhiteSpace(url))
                {
                    list.Add(new StreamInfo
                    {
                        Url = url,
                        Format = format,
                        Quality = quality,
                        Bitrate = bitrate,
                        MimeType = mime,
                        Codec = codec,
                        Width = width,
                        Height = height,
                        Fps = fps,
                        IsAudioOnly = false
                    });
                }
            }
        }

        // Audio streams
        if (root.TryGetProperty("audioStreams", out var aStreams) && aStreams.ValueKind == JsonValueKind.Array)
        {
            foreach (var as_ in aStreams.EnumerateArray())
            {
                var url = as_.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var format = as_.TryGetProperty("format", out var f) ? f.GetString() ?? "" : "";
                var quality = as_.TryGetProperty("quality", out var q) ? q.GetString() ?? "" : "";
                var bitrate = as_.TryGetProperty("bitrate", out var b) ? b.GetInt32() : 0;
                var mime = as_.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
                var codec = as_.TryGetProperty("codec", out var c) ? c.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(url))
                {
                    list.Add(new StreamInfo
                    {
                        Url = url,
                        Format = format,
                        Quality = quality,
                        Bitrate = bitrate,
                        MimeType = mime,
                        Codec = codec,
                        IsAudioOnly = true
                    });
                }
            }
        }

        return list;
    }

    public static string ExtractVideoId(string urlOrId)
    {
        if (string.IsNullOrWhiteSpace(urlOrId)) return string.Empty;

        if (urlOrId.StartsWith("/watch?v="))
        {
            return urlOrId.Substring("/watch?v=".Length).Split('&')[0];
        }

        if (urlOrId.Contains("watch?v="))
        {
            var query = new Uri(urlOrId).Query;
            var parsed = HttpUtility.ParseQueryString(query);
            return parsed["v"] ?? string.Empty;
        }

        if (urlOrId.Contains("youtu.be/"))
        {
            var path = new Uri(urlOrId).AbsolutePath.Trim('/');
            return path.Split('?')[0];
        }

        return urlOrId;
    }
}
