using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccessibleYoutubePlayer.Core.Services;

/// <summary>
/// Master unified YouTube service orchestrating transparent multi-tier failover:
/// 1. Primary: Piped API (public, no key)
/// 2. Secondary: Invidious API (public, no key)
/// 3. Local Fallback: yt-dlp (local extraction)
/// Automatically detects pasted YouTube URLs and handles automatic retries and failover.
/// </summary>
public class FailoverYouTubeService : IYouTubeService
{
    private readonly IYouTubeBackendProvider _pipedProvider;
    private readonly IYouTubeBackendProvider _invidiousProvider;
    private readonly IYouTubeBackendProvider _localFallbackProvider;
    private readonly ILogger<FailoverYouTubeService> _logger;

    public ProviderType CurrentProviderType { get; private set; } = ProviderType.Piped;
    public string? CurrentInstanceName { get; private set; }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<ProviderType>? ProviderSwitched;

    public FailoverYouTubeService(
        IYouTubeBackendProvider pipedProvider,
        IYouTubeBackendProvider invidiousProvider,
        IYouTubeBackendProvider localFallbackProvider,
        ILogger<FailoverYouTubeService>? logger = null)
    {
        _pipedProvider = pipedProvider;
        _invidiousProvider = invidiousProvider;
        _localFallbackProvider = localFallbackProvider;
        _logger = logger ?? NullLogger<FailoverYouTubeService>.Instance;
    }

    public async Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var trimmed = query.Trim();

        // Direct YouTube URL handling: if user pasted a video link, resolve directly!
        if (IsYouTubeUrl(trimmed, out var videoId))
        {
            SetStatus("Resolving video URL...");
            var details = await GetVideoDetailsAsync(videoId, cancellationToken);
            return new SearchResult
            {
                Items = new[] { details },
                Query = query,
                ProviderUsed = CurrentProviderType,
                InstanceUsed = CurrentInstanceName
            };
        }

        return await ExecuteWithFailoverAsync(
            "Searching",
            p => p.SearchAsync(trimmed, cancellationToken),
            cancellationToken);
    }

    public async Task<VideoMetadata> GetVideoDetailsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithFailoverAsync(
            $"Fetching details for video {videoId}",
            p => p.GetVideoDetailsAsync(videoId, cancellationToken),
            cancellationToken);
    }

    public async Task<IReadOnlyList<StreamInfo>> GetStreamsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithFailoverAsync(
            $"Resolving media streams for video {videoId}",
            p => p.GetStreamsAsync(videoId, cancellationToken),
            cancellationToken);
    }

    public async Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(string videoId, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithFailoverAsync(
            $"Retrieving subtitles for video {videoId}",
            p => p.GetSubtitlesAsync(videoId, cancellationToken),
            cancellationToken);
    }

    public async Task RefreshInstancesAsync(CancellationToken cancellationToken = default)
    {
        SetStatus("Refreshing and validating server directories...");
        CurrentProviderType = ProviderType.Piped;
        ProviderSwitched?.Invoke(this, CurrentProviderType);
        await Task.CompletedTask;
    }

    private async Task<T> ExecuteWithFailoverAsync<T>(
        string operationName,
        Func<IYouTubeBackendProvider, Task<T>> action,
        CancellationToken cancellationToken)
    {
        // Tier 1: Piped
        try
        {
            CurrentProviderType = ProviderType.Piped;
            SetStatus($"{operationName} via Piped...");
            var result = await action(_pipedProvider);
            SetStatus("Ready");
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Tier 1 (Piped) failed during {Operation}. Failing over to Invidious.", operationName);
        }

        // Tier 2: Invidious
        try
        {
            CurrentProviderType = ProviderType.Invidious;
            ProviderSwitched?.Invoke(this, CurrentProviderType);
            SetStatus($"{operationName} via Invidious...");
            var result = await action(_invidiousProvider);
            SetStatus("Ready");
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Tier 2 (Invidious) failed during {Operation}. Failing over to Local Fallback.", operationName);
        }

        // Tier 3: Local Fallback (yt-dlp)
        try
        {
            CurrentProviderType = ProviderType.LocalFallback;
            ProviderSwitched?.Invoke(this, CurrentProviderType);
            SetStatus($"{operationName} via local extractor...");
            var result = await action(_localFallbackProvider);
            SetStatus("Ready");
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "All providers failed during {Operation}.", operationName);
            SetStatus("Operation failed across all providers.");
            throw new InvalidOperationException($"Unable to complete '{operationName}'. Public servers are currently unresponsive, and local fallback encountered an error: {ex.Message}", ex);
        }
    }

    private void SetStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    public static bool IsYouTubeUrl(string input, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        if (input.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase))
        {
            var idx = input.IndexOf("v=", StringComparison.OrdinalIgnoreCase);
            if (idx != -1)
            {
                var id = input.Substring(idx + 2).Split('&')[0].Split('#')[0];
                if (!string.IsNullOrWhiteSpace(id))
                {
                    videoId = id;
                    return true;
                }
            }
        }
        else if (input.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
        {
            var idx = input.IndexOf("youtu.be/", StringComparison.OrdinalIgnoreCase);
            var id = input.Substring(idx + "youtu.be/".Length).Split('?')[0].Split('&')[0].Split('#')[0];
            if (!string.IsNullOrWhiteSpace(id))
            {
                videoId = id;
                return true;
            }
        }
        else if (input.Contains("youtube.com/shorts/", StringComparison.OrdinalIgnoreCase))
        {
            var idx = input.IndexOf("shorts/", StringComparison.OrdinalIgnoreCase);
            var id = input.Substring(idx + "shorts/".Length).Split('?')[0].Split('&')[0];
            if (!string.IsNullOrWhiteSpace(id))
            {
                videoId = id;
                return true;
            }
        }

        return false;
    }
}
