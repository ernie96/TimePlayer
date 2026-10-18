using TimePlayer.Core.Common;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;

namespace TimePlayer.Tests.Helpers;

public sealed class FakeDiscoveryService : IDiscoveryService
{
    public List<SearchResult> Featured { get; } = new();
    public Dictionary<string, ChannelUploads> Channels { get; } = new();
    public int FeedRequests { get; private set; }

    /// <summary>Further Featured pages, served for page tokens "1", "2", …</summary>
    public List<List<SearchResult>> FeaturedPages { get; } = new();

    public List<(FeaturedCategory Category, string Region, string? Token)> FeaturedRequests { get; } = new();

    public Task<Result<PagedResult<SearchResult>>> GetFeaturedAsync(FeaturedCategory category, string region, string? pageToken = null, CancellationToken cancellationToken = default)
    {
        FeaturedRequests.Add((category, region, pageToken));
        if (pageToken is not null)
        {
            var index = int.Parse(pageToken);
            var items = index <= FeaturedPages.Count ? FeaturedPages[index - 1] : new List<SearchResult>();
            return Task.FromResult(Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult>
            {
                Items = items.ToList(),
                HasNextPage = index < FeaturedPages.Count,
                NextPageToken = index < FeaturedPages.Count ? (index + 1).ToString() : null
            }));
        }
        return Task.FromResult(Featured.Count > 0
            ? Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult>
            {
                Items = Featured.ToList(),
                HasNextPage = FeaturedPages.Count > 0,
                NextPageToken = FeaturedPages.Count > 0 ? "1" : null
            })
            : Result<PagedResult<SearchResult>>.Failure("EMPTY_FEED", "No featured videos."));
    }

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
