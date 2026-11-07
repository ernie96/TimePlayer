using System.Text.Json;
using System.Text.Json.Nodes;
using TimePlayer.Core.Common;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Core.Backends.InnerTube;

/// <summary>A YouTube account's own lists, read while signed in (instead of a Google Takeout zip).</summary>
public sealed record AccountData(
    IReadOnlyList<ChannelSubscription> Channels,
    IReadOnlyList<SearchResult> History,
    IReadOnlyList<SearchResult> Liked,
    IReadOnlyList<SearchResult> WatchLater,
    IReadOnlyList<(string Name, IReadOnlyList<SearchResult> Videos)> Playlists);

public sealed partial class InnerTubeBackendProvider
{
    // Enough for large accounts without running for ever: a page holds about 30 to 100 items.
    private const int MaxImportPages = 40;

    /// <summary>
    /// Reads the signed-in account's subscriptions, watch history, liked videos, Watch later and own
    /// playlists. Works whatever the account setting is, since the person just chose to sign in for this.
    /// </summary>
    public async Task<Result<AccountData>> ReadAccountAsync(IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        if (_account?.IsSignedIn != true)
            return Result<AccountData>.Failure("NOT_SIGNED_IN", T("Sign in to YouTube first."));

        progress?.Report(T("Reading your subscriptions..."));
        var channels = new List<ChannelSubscription>();
        var seenChannels = new HashSet<string>();
        var read = await BrowseAllAsync("FEchannels", root =>
        {
            var before = channels.Count;
            foreach (var name in new[] { "channelRenderer", "gridChannelRenderer" })
                foreach (var renderer in InnerTubeJson.FindAll(root, name))
                    if (InnerTubeJson.Str(renderer, "channelId") is { Length: > 0 } id && seenChannels.Add(id))
                        channels.Add(new ChannelSubscription(id, InnerTubeJson.Text(InnerTubeJson.Path(renderer, "title")) ?? id));
            return channels.Count - before;
        }, cancellationToken);
        if (read.IsFailure)
            return Result<AccountData>.Failure(read.Error);

        progress?.Report(T("Reading your watch history..."));
        var history = await BrowseVideosAsync("FEhistory", cancellationToken);

        progress?.Report(T("Reading your likes and Watch later..."));
        var liked = await BrowseVideosAsync("VLLL", cancellationToken);
        var watchLater = await BrowseVideosAsync("VLWL", cancellationToken);

        progress?.Report(T("Reading your playlists..."));
        var playlistIds = new List<(string Id, string Title)>();
        await BrowseAllAsync("FEplaylist_aggregation", root =>
        {
            var before = playlistIds.Count;
            foreach (var lockup in InnerTubeJson.FindAll(root, "lockupViewModel"))
                if (InnerTubeJson.ParsePlaylistLockup(lockup) is { } playlist && playlist.Id is not ("LL" or "WL") && playlistIds.All(p => p.Id != playlist.Id))
                    playlistIds.Add((playlist.Id, playlist.Title));
            foreach (var renderer in InnerTubeJson.FindAll(root, "gridPlaylistRenderer"))
                if (InnerTubeJson.Str(renderer, "playlistId") is { Length: > 0 } id && id is not ("LL" or "WL") && playlistIds.All(p => p.Id != id))
                    playlistIds.Add((id, InnerTubeJson.Text(InnerTubeJson.Path(renderer, "title")) ?? id));
            return playlistIds.Count - before;
        }, cancellationToken);

        var playlists = new List<(string, IReadOnlyList<SearchResult>)>();
        foreach (var (id, title) in playlistIds)
            playlists.Add((title, await BrowseVideosAsync("VL" + id, cancellationToken)));

        return Result<AccountData>.Success(new AccountData(channels, history, liked, watchLater, playlists));
    }

    /// <summary>Every video on a signed-in page, following its pages.</summary>
    private async Task<IReadOnlyList<SearchResult>> BrowseVideosAsync(string browseId, CancellationToken cancellationToken)
    {
        var videos = new List<SearchResult>();
        var seen = new HashSet<string>();
        await BrowseAllAsync(browseId, root =>
        {
            var before = videos.Count;
            foreach (var video in ParseAccountVideos(root))
                if (seen.Add(video.Id))
                    videos.Add(video);
            return videos.Count - before;
        }, cancellationToken);
        return videos;
    }

    public static IEnumerable<SearchResult> ParseAccountVideos(JsonElement root)
    {
        foreach (var lockup in InnerTubeJson.FindAll(root, "lockupViewModel"))
            if (InnerTubeJson.ParseLockup(lockup) is { } video)
                yield return video;
        foreach (var name in new[] { "videoRenderer", "gridVideoRenderer", "playlistVideoRenderer" })
            foreach (var renderer in InnerTubeJson.FindAll(root, name))
                if (InnerTubeJson.ParseVideoRenderer(renderer) is { } video)
                    yield return video;
    }

    /// <summary>
    /// A signed-in browse page and the pages after it. <paramref name="collect"/> takes each page and
    /// says how many new items it found; a page with none ends the list.
    /// </summary>
    private async Task<Result<bool>> BrowseAllAsync(string browseId, Func<JsonElement, int> collect, CancellationToken cancellationToken)
    {
        JsonObject body = new() { ["browseId"] = browseId };
        for (var page = 0; page < MaxImportPages; page++)
        {
            var response = await PostAsync("browse", body, cancellationToken, signedIn: true, forImport: true);
            if (response.IsFailure)
                return page == 0 ? Result<bool>.Failure(response.Error) : Result<bool>.Success(true);
            try
            {
                using var doc = JsonDocument.Parse(response.Value);
                if (SignedOut(doc.RootElement))
                    return Result<bool>.Failure("SIGNED_OUT", T("YouTube says you are signed out. Sign in again."));
                if (collect(doc.RootElement) == 0)
                    return Result<bool>.Success(true);
                var next = InnerTubeJson.ListContinuationToken(doc.RootElement);
                if (string.IsNullOrEmpty(next))
                    return Result<bool>.Success(true);
                body = new JsonObject { ["continuation"] = next };
            }
            catch (JsonException ex)
            {
                return page == 0 ? Result<bool>.Failure("INNERTUBE_ACCOUNT_FAILED", ex.Message, ex.ToString(), ex) : Result<bool>.Success(true);
            }
        }
        return Result<bool>.Success(true);
    }
}
