using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using TimePlayer.Core.Common;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends.InnerTube;

/// <summary>A channel's tabs (Videos, Shorts, Live, Playlists) and searching for Shorts.</summary>
public sealed partial class InnerTubeBackendProvider
{
    // YouTube's own tab list per channel: "Shorts" → its browse params. Read once per channel.
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _channelTabs = new();

    /// <summary>The tabs YouTube shows for a channel, by their English names ("Videos", "Shorts", "Live", "Playlists").</summary>
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetChannelTabsAsync(string channelId, CancellationToken cancellationToken = default)
    {
        if (_channelTabs.TryGetValue(channelId, out var cached))
            return Result<IReadOnlyDictionary<string, string>>.Success(cached);
        var response = await PostAsync("browse", new JsonObject { ["browseId"] = channelId }, cancellationToken);
        if (response.IsFailure)
            return Result<IReadOnlyDictionary<string, string>>.Failure(response.Error);
        try
        {
            using var doc = JsonDocument.Parse(response.Value);
            var tabs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tab in InnerTubeJson.FindAll(doc.RootElement, "tabRenderer"))
            {
                var title = InnerTubeJson.Str(tab, "title");
                var parameters = InnerTubeJson.Str(tab, "endpoint", "browseEndpoint", "params");
                if (title is not null && parameters is not null)
                    tabs[title] = Uri.UnescapeDataString(parameters);
            }
            _channelTabs[channelId] = tabs;
            return Result<IReadOnlyDictionary<string, string>>.Success(tabs);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyDictionary<string, string>>.Failure("INNERTUBE_CHANNEL_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    /// <summary>One page of a channel tab: the first when <paramref name="continuation"/> is null.</summary>
    public async Task<Result<PagedResult<SearchResult>>> GetChannelTabPageAsync(
        string channelId, string tabParams, string? continuation, CancellationToken cancellationToken = default)
    {
        var body = string.IsNullOrEmpty(continuation)
            ? new JsonObject { ["browseId"] = channelId, ["params"] = tabParams }
            : new JsonObject { ["continuation"] = continuation };
        var response = await PostAsync("browse", body, cancellationToken);
        return response.IsFailure ? Result<PagedResult<SearchResult>>.Failure(response.Error) : ParseItemsPage(response.Value);
    }

    /// <summary>Searches Shorts only (YouTube's "Type: Shorts" filter).</summary>
    public async Task<Result<PagedResult<SearchResult>>> SearchShortsAsync(string query, string? pageToken, CancellationToken cancellationToken = default)
    {
        var body = string.IsNullOrWhiteSpace(pageToken)
            ? new JsonObject { ["query"] = query, ["params"] = "EgIQCQ==" }
            : new JsonObject { ["continuation"] = pageToken };
        var response = await PostAsync("search", body, cancellationToken);
        return response.IsFailure ? Result<PagedResult<SearchResult>>.Failure(response.Error) : ParseItemsPage(response.Value);
    }

    /// <summary>Videos, Shorts and playlists on a page, in order, with the token for the next page.</summary>
    private static Result<PagedResult<SearchResult>> ParseItemsPage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = new List<SearchResult>();
            var seen = new HashSet<string>();
            foreach (var short_ in InnerTubeJson.FindAll(doc.RootElement, "shortsLockupViewModel"))
                if (InnerTubeJson.ParseShortsLockup(short_) is { } s && seen.Add(s.Id))
                    items.Add(s);
            foreach (var lockup in InnerTubeJson.FindAll(doc.RootElement, "lockupViewModel"))
            {
                var item = InnerTubeJson.ParseLockup(lockup) ?? InnerTubeJson.ParsePlaylistLockup(lockup);
                if (item is not null && seen.Add(item.Id))
                    items.Add(item);
            }
            foreach (var renderer in InnerTubeJson.FindAll(doc.RootElement, "videoRenderer"))
                if (InnerTubeJson.ParseVideoRenderer(renderer) is { } v && seen.Add(v.Id))
                    items.Add(v);
            var next = items.Count > 0 ? InnerTubeJson.ListContinuationToken(doc.RootElement) : null;
            return Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult>
            {
                Items = items,
                PageIndex = 1,
                HasNextPage = next is not null,
                NextPageToken = next,
                TotalEstimatedResults = items.Count
            });
        }
        catch (JsonException ex)
        {
            return Result<PagedResult<SearchResult>>.Failure("INNERTUBE_PAGE_FAILED", ex.Message, ex.ToString(), ex);
        }
    }
}
