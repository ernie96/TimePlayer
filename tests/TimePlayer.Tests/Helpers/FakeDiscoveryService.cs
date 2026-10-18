using TimePlayer.Core.Common;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;

namespace TimePlayer.Tests.Helpers;

public sealed class FakeDiscoveryService : IDiscoveryService
{
    public List<SearchResult> Featured { get; } = new();
    public Dictionary<string, ChannelUploads> Channels { get; } = new();
    public int FeedRequests { get; private set; }

    public Task<Result<IReadOnlyList<SearchResult>>> GetFeaturedAsync(FeaturedCategory category, string region, CancellationToken cancellationToken = default) =>
        Task.FromResult(Featured.Count > 0
            ? Result<IReadOnlyList<SearchResult>>.Success(Featured.ToList())
            : Result<IReadOnlyList<SearchResult>>.Failure("EMPTY_FEED", "No featured videos."));

    public Task<Result<ChannelUploads>> GetChannelUploadsAsync(string channelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Channels.TryGetValue(channelId, out var uploads)
            ? Result<ChannelUploads>.Success(uploads)
            : Result<ChannelUploads>.Failure("CHANNEL_FEED", "Unknown channel."));

    public List<SearchResult> OlderUploads { get; } = new();

    public Task<Result<PagedResult<SearchResult>>> GetChannelUploadsPageAsync(string channelId, string? pageToken, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<PagedResult<SearchResult>>.Success(pageToken is null
            ? new PagedResult<SearchResult> { Items = OlderUploads.ToList(), HasNextPage = false }
            : PagedResult<SearchResult>.Empty()));

    public Task<SubscriptionFeedResult> GetSubscriptionFeedAsync(
        IReadOnlyList<ChannelSubscription> subscriptions,
        IProgress<int>? channelsDone = null,
        CancellationToken cancellationToken = default)
    {
        FeedRequests++;
        var videos = subscriptions
            .SelectMany(s => Channels.TryGetValue(s.ChannelId, out var u) ? u.Videos : Array.Empty<SearchResult>())
            .OrderByDescending(v => v.PublishedAt)
            .ToList();
        return Task.FromResult(new SubscriptionFeedResult(videos, Array.Empty<ChannelSubscription>(), DateTimeOffset.UtcNow));
    }
}
