using System.Text.Json;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends.InnerTube;

internal static partial class InnerTubeJson
{
    /// <summary>
    /// A Short in a channel's Shorts tab or a Shorts search: the video id is in its tap command, the title
    /// and views in the overlay ("38 years on...", "45K views").
    /// </summary>
    public static SearchResult? ParseShortsLockup(JsonElement lockup)
    {
        var videoId = FindAll(lockup, "reelWatchEndpoint").Select(e => Str(e, "videoId")).FirstOrDefault(id => !string.IsNullOrEmpty(id));
        if (videoId is null)
        {
            var entity = Str(lockup, "entityId");
            const string prefix = "shorts-shelf-item-";
            if (entity?.StartsWith(prefix, StringComparison.Ordinal) == true)
                videoId = entity[prefix.Length..];
        }
        if (string.IsNullOrEmpty(videoId))
            return null;

        var title = Str(lockup, "overlayMetadata", "primaryText", "content");
        var views = Str(lockup, "overlayMetadata", "secondaryText", "content");
        if (string.IsNullOrWhiteSpace(title))
        {
            // "Title, 45 thousand views - play Short"
            var spoken = Str(lockup, "accessibilityText") ?? string.Empty;
            var cut = spoken.LastIndexOf(", ", StringComparison.Ordinal);
            title = cut > 0 ? spoken[..cut] : spoken;
        }
        return new SearchResult
        {
            Id = videoId,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled Short" : title.Trim(),
            ChannelTitle = string.Empty,
            IsShort = true,
            ViewCount = ParseCount(views),
            ThumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg"
        };
    }
}
