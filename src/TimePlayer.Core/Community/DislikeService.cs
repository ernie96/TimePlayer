using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Configuration;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Core.Community;

/// <summary>Likes and estimated dislikes for a video.</summary>
public sealed record VideoVotes(long Likes, long Dislikes)
{
    /// <summary>"12,345 likes, 678 dislikes"</summary>
    public string Spoken => Plural(Likes, "{0:N0} like", "{0:N0} likes") + ", " + Plural(Dislikes, "{0:N0} dislike", "{0:N0} dislikes");
}

public interface IDislikeService
{
    /// <summary>Null when turned off or unavailable.</summary>
    Task<VideoVotes?> GetVotesAsync(string videoId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Return YouTube Dislike (returnyoutubedislike.com). YouTube hid dislike counts in 2021; this project
/// estimates them from archived data and its extension's users. Only the video id is sent.
/// </summary>
public sealed class DislikeService : IDislikeService
{
    private readonly HttpClient _http;
    private readonly IConfigurationService _config;
    private readonly ILogger<DislikeService> _logger;

    public DislikeService(HttpClient http, IConfigurationService config, ILogger<DislikeService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<VideoVotes?> GetVotesAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var settings = _config.CurrentConfig.Dislikes;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(videoId))
            return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var url = $"{settings.ApiUrl.TrimEnd('/')}/votes?videoId={Uri.EscapeDataString(videoId)}";
            using var response = await _http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            return Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _logger.LogInformation("Return YouTube Dislike lookup failed: {Message}", ex.Message);
            return null;
        }
    }

    public static VideoVotes? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("likes", out var likes) || !likes.TryGetInt64(out var likeCount) ||
            !root.TryGetProperty("dislikes", out var dislikes) || !dislikes.TryGetInt64(out var dislikeCount))
            return null;
        return new VideoVotes(likeCount, dislikeCount);
    }
}
