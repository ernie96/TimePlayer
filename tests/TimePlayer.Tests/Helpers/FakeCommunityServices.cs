using TimePlayer.Core.Community;

namespace TimePlayer.Tests.Helpers;

public sealed class FakeSponsorBlockService : ISponsorBlockService
{
    public Dictionary<string, List<SkipSegment>> Segments { get; } = new();

    public Task<IReadOnlyList<SkipSegment>> GetSegmentsAsync(string videoId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SkipSegment>>(Segments.TryGetValue(videoId, out var list) ? list : new List<SkipSegment>());
}

public sealed class FakeDislikeService : IDislikeService
{
    public Dictionary<string, VideoVotes> Votes { get; } = new();

    public Task<VideoVotes?> GetVotesAsync(string videoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Votes.TryGetValue(videoId, out var votes) ? votes : null);
}
