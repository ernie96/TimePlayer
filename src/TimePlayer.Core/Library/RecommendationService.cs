using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Library;

public sealed record RecommendationOptions(string Region = "US", bool HideShorts = false, bool Refresh = false);

/// <param name="IsPersonal">False when there was nothing to go on yet and these are trending videos.</param>
public sealed record Recommendations(IReadOnlyList<SearchResult> Videos, DateTimeOffset FetchedUtc, bool IsPersonal);

public interface IRecommendationService
{
    /// <summary>Cached recommendations, or fresh ones when the cache is old or <see cref="RecommendationOptions.Refresh"/>.</summary>
    Task<Recommendations> GetAsync(RecommendationOptions options, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>True when <see cref="GetAsync"/> can answer from its cache without going online.</summary>
    bool IsCacheFresh { get; }

    /// <summary>Drops videos from the cached list (after Not interested, or once watched).</summary>
    void Forget(Func<SearchResult, bool> predicate);
}

/// <summary>
/// "For You" without an account. Seeds are what the user watched, liked and subscribed to; each
/// seed's "Up next" list from YouTube is pooled and scored by how often and how high a video shows
/// up. Videos already watched, liked or marked Not interested are left out, and no channel gets more
/// than two spots so the list stays varied. Everything stays on this computer.
/// </summary>
public sealed class RecommendationService : IRecommendationService
{
    private const int MaxResults = 60;
    private const int MaxPerChannel = 2;
    private const int MaxParallelSeeds = 4;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(3);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILibraryService _library;
    private readonly IWatchPageService _watchPage;
    private readonly IDiscoveryService _discovery;
    private readonly ILogger<RecommendationService> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CacheFile? _cache;

    public RecommendationService(ILibraryService library, IWatchPageService watchPage, IDiscoveryService discovery, ILogger<RecommendationService> logger)
        : this(library, watchPage, discovery, logger, () => DateTimeOffset.UtcNow)
    {
    }

    public RecommendationService(ILibraryService library, IWatchPageService watchPage, IDiscoveryService discovery,
        ILogger<RecommendationService> logger, Func<DateTimeOffset> clock)
    {
        _library = library;
        _watchPage = watchPage;
        _discovery = discovery;
        _logger = logger;
        _clock = clock;
    }

    public bool IsCacheFresh
    {
        get
        {
            _cache ??= LoadCache();
            return _cache is { Videos.Count: > 0 } c && _clock() - c.FetchedUtc < CacheLifetime;
        }
    }

    private string CacheFilePath => Path.Combine(_library.DataDirectory, "recommendations.json");

    public async Task<Recommendations> GetAsync(RecommendationOptions options, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cache ??= LoadCache();
            if (!options.Refresh && _cache is { } cached && _clock() - cached.FetchedUtc < CacheLifetime && cached.Videos.Count > 0)
                return new Recommendations(Filter(cached.Videos, options).ToList(), cached.FetchedUtc, cached.IsPersonal);

            var fresh = await BuildAsync(options, progress, cancellationToken);
            _cache = new CacheFile { FetchedUtc = fresh.FetchedUtc, IsPersonal = fresh.IsPersonal, Videos = fresh.Videos.ToList() };
            SaveCache();
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Forget(Func<SearchResult, bool> predicate)
    {
        _gate.Wait();
        try
        {
            _cache ??= LoadCache();
            if (_cache is null || _cache.Videos.RemoveAll(v => predicate(v)) == 0)
                return;
            SaveCache();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Recommendations> BuildAsync(RecommendationOptions options, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var seeds = PickSeeds();
        var subscribedIds = _library.Subscriptions.Select(s => s.ChannelId).ToHashSet();
        var subscribedNames = _library.Subscriptions.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Subscriptions with no feed cached yet: their newest uploads become seeds too.
        if (subscribedIds.Count > 0 && !seeds.Any(s => s.Source == SeedSource.Subscription))
            seeds.AddRange(await SubscriptionSeedsFromRssAsync(cancellationToken));

        if (seeds.Count == 0)
            return await TrendingAsync(options, cancellationToken);

        var scores = new Dictionary<string, (SearchResult Video, double Score)>();
        int done = 0;
        await Parallel.ForEachAsync(seeds, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSeeds, CancellationToken = cancellationToken },
            async (seed, ct) =>
            {
                var next = await _watchPage.GetWatchNextAsync(seed.VideoId, ct);
                if (next.IsSuccess)
                {
                    lock (scores)
                    {
                        var rank = 0;
                        foreach (var video in next.Value.Related.Where(v => v.IsVideo))
                        {
                            // Higher in Up next = closer to the seed.
                            var points = seed.Weight / (1 + rank * 0.15);
                            scores[video.Id] = scores.TryGetValue(video.Id, out var existing)
                                ? (existing.Video, existing.Score + points)
                                : (video, points);
                            rank++;
                        }
                    }
                }
                progress?.Report(Interlocked.Increment(ref done) * 100 / seeds.Count);
            });

        // Unwatched uploads from subscriptions belong in For You as well.
        if (_library.CachedFeed is { } feed)
        {
            var cutoff = _clock() - TimeSpan.FromDays(7);
            foreach (var video in feed.Videos.Where(v => v.PublishedAt is null || v.PublishedAt >= cutoff).Take(30))
            {
                if (!scores.ContainsKey(video.Id))
                    scores[video.Id] = (video, 0.9);
            }
        }

        var excluded = new HashSet<string>(_library.History.Select(h => h.VideoId));
        excluded.UnionWith(_library.LikedVideos.Select(v => v.VideoId));
        excluded.UnionWith(seeds.Select(s => s.VideoId));

        var familiarChannels = _library.History.Select(h => h.ChannelTitle)
            .Concat(_library.LikedVideos.Select(v => v.ChannelTitle))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A little randomness so a refresh shows something new.
        var random = new Random();
        var ranked = scores.Values
            .Where(x => !excluded.Contains(x.Video.Id))
            .Select(x =>
            {
                var score = x.Score;
                if ((x.Video.ChannelId is { } id && subscribedIds.Contains(id)) || subscribedNames.Contains(x.Video.ChannelTitle))
                    score *= 1.3;
                else if (familiarChannels.Contains(x.Video.ChannelTitle))
                    score *= 1.15;
                return (x.Video, Score: score * (0.85 + random.NextDouble() * 0.3));
            })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Video);

        var videos = Diversify(Filter(ranked, options)).Take(MaxResults).ToList();
        _logger.LogInformation("Recommendations: {Count} videos from {Seeds} seeds.", videos.Count, seeds.Count);
        if (videos.Count == 0)
            return await TrendingAsync(options, cancellationToken);
        return new Recommendations(videos, _clock(), IsPersonal: true);
    }

    private enum SeedSource { History, Like, Subscription }

    private sealed record Seed(string VideoId, double Weight, SeedSource Source);

    private List<Seed> PickSeeds()
    {
        var seeds = new List<Seed>();
        var random = new Random();

        // Recently watched: finished or rewatched videos say more than ones left after a minute.
        foreach (var h in _library.History.Take(25)
                     .OrderByDescending(h => (h.IsFinished ? 2 : 1) + Math.Min(h.WatchCount, 3) * 0.5 + random.NextDouble())
                     .Take(6))
        {
            var weight = h.IsFinished || h.WatchCount > 1 ? 1.2 : 0.8;
            seeds.Add(new Seed(h.VideoId, weight, SeedSource.History));
        }

        foreach (var like in _library.LikedVideos.Take(30).OrderBy(_ => random.Next()).Take(4))
        {
            if (seeds.All(s => s.VideoId != like.VideoId))
                seeds.Add(new Seed(like.VideoId, 1.5, SeedSource.Like));
        }

        if (_library.CachedFeed is { } feed)
        {
            foreach (var upload in feed.Videos
                         .GroupBy(v => v.ChannelId ?? v.ChannelTitle)
                         .Select(g => g.First())
                         .OrderBy(_ => random.Next())
                         .Take(4))
            {
                if (seeds.All(s => s.VideoId != upload.Id))
                    seeds.Add(new Seed(upload.Id, 1.0, SeedSource.Subscription));
            }
        }
        return seeds;
    }

    private async Task<List<Seed>> SubscriptionSeedsFromRssAsync(CancellationToken cancellationToken)
    {
        var channels = _library.Subscriptions.OrderBy(_ => Random.Shared.Next()).Take(4).ToList();
        var tasks = channels.Select(c => _discovery.GetChannelUploadsAsync(c.ChannelId, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results
            .Where(r => r.IsSuccess)
            .Select(r => r.Value!.Videos.FirstOrDefault())
            .OfType<SearchResult>()
            .Select(v => new Seed(v.Id, 1.0, SeedSource.Subscription))
            .ToList();
    }

    private async Task<Recommendations> TrendingAsync(RecommendationOptions options, CancellationToken cancellationToken)
    {
        var trending = await _discovery.GetFeaturedAsync(FeaturedCategory.Trending, options.Region, cancellationToken);
        var videos = trending.IsSuccess ? Filter(trending.Value, options).Take(MaxResults).ToList() : new List<SearchResult>();
        return new Recommendations(videos, _clock(), IsPersonal: false);
    }

    private IEnumerable<SearchResult> Filter(IEnumerable<SearchResult> videos, RecommendationOptions options) =>
        videos.Where(v => v.IsVideo && !(options.HideShorts && v.IsShort) && !_library.IsHidden(v));

    private static IEnumerable<SearchResult> Diversify(IEnumerable<SearchResult> ranked)
    {
        var perChannel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in ranked)
        {
            var key = video.ChannelId ?? video.ChannelTitle;
            perChannel.TryGetValue(key, out var count);
            if (count >= MaxPerChannel)
                continue;
            perChannel[key] = count + 1;
            yield return video;
        }
    }

    private CacheFile? LoadCache()
    {
        try
        {
            if (File.Exists(CacheFilePath))
                return JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CacheFilePath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Only a cache; it is rebuilt on the next refresh.
        }
        return null;
    }

    private void SaveCache()
    {
        if (_cache is null)
            return;
        try
        {
            Directory.CreateDirectory(_library.DataDirectory);
            var temp = CacheFilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cache, JsonOptions));
            File.Move(temp, CacheFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class CacheFile
    {
        public DateTimeOffset FetchedUtc { get; set; }
        public bool IsPersonal { get; set; }
        public List<SearchResult> Videos { get; set; } = new();
    }
}
