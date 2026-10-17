using TimePlayer.Core.Common;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;

namespace TimePlayer.Tests.Helpers;

/// <summary>
/// Controllable fake backend provider for orchestrating unit tests.
/// </summary>
public class FakeBackendProvider : IBackendProvider
{
    public BackendProviderType ProviderType { get; set; }

    public Func<string, string?, CancellationToken, Task<Result<PagedResult<SearchResult>>>>? SearchHandler { get; set; }
    public Func<YouTubeVideoId, CancellationToken, Task<Result<VideoDetails>>>? GetVideoDetailsHandler { get; set; }
    public Func<YouTubeVideoId, QualityPreference, bool, CancellationToken, Task<Result<StreamResolutionResult>>>? ResolveStreamsHandler { get; set; }

    public int SearchCallCount { get; private set; }
    public int ResolveStreamsCallCount { get; private set; }
    public int GetDetailsCallCount { get; private set; }

    public FakeBackendProvider(BackendProviderType providerType)
    {
        ProviderType = providerType;
    }

    public Task<Result<PagedResult<SearchResult>>> SearchAsync(
        string query,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SearchCallCount++;
        if (SearchHandler != null)
        {
            return SearchHandler(query, pageToken, cancellationToken);
        }

        var results = new List<SearchResult>
        {
            new SearchResult
            {
                Id = "dQw4w9WgXcQ",
                Title = $"Video for {query} via {ProviderType}",
                ChannelTitle = "Test Channel",
                Duration = TimeSpan.FromMinutes(3)
            }
        };

        return Task.FromResult(Result<PagedResult<SearchResult>>.Success(PagedResult<SearchResult>.SinglePage(results)));
    }

    public Task<Result<VideoDetails>> GetVideoDetailsAsync(
        YouTubeVideoId videoId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetDetailsCallCount++;
        if (GetVideoDetailsHandler != null)
        {
            return GetVideoDetailsHandler(videoId, cancellationToken);
        }

        var details = new VideoDetails
        {
            Id = videoId,
            Title = $"Details for {videoId.Value}",
            Description = "Test description",
            Channel = new ChannelInfo { Id = "UC123", Name = "Test Channel" },
            Duration = TimeSpan.FromMinutes(4)
        };

        return Task.FromResult(Result<VideoDetails>.Success(details));
    }

    public Task<Result<StreamResolutionResult>> ResolveStreamsAsync(
        YouTubeVideoId videoId,
        QualityPreference qualityPreference = QualityPreference.Auto,
        bool audioOnly = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResolveStreamsCallCount++;
        if (ResolveStreamsHandler != null)
        {
            return ResolveStreamsHandler(videoId, qualityPreference, audioOnly, cancellationToken);
        }

        var candidate = new StreamCandidate
        {
            Url = $"https://stream.example.com/{ProviderType}/{videoId.Value}.mp4",
            QualityLabel = "1080p",
            Height = 1080,
            IsProgressive = true
        };

        var stream = new StreamResolutionResult
        {
            VideoId = videoId,
            SelectedVideoStream = candidate,
            DirectPlaybackUrl = candidate.Url,
            AudioTrackUrl = null,
            SourceProvider = ProviderType
        };

        return Task.FromResult(Result<StreamResolutionResult>.Success(stream));
    }
}
