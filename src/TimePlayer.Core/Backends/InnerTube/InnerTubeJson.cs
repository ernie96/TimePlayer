using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends.InnerTube;

/// <summary>
/// Shape-tolerant readers for InnerTube responses. YouTube moves renderers around often, so these
/// search the tree for the renderer they need instead of following one exact path.
/// </summary>
internal static partial class InnerTubeJson
{
    public static IEnumerable<JsonElement> FindAll(JsonElement element, string propertyName)
    {
        // Depth-first, document order.
        var found = new List<JsonElement>();
        Walk(element, propertyName, found);
        return found;
    }

    private static void Walk(JsonElement element, string propertyName, List<JsonElement> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(propertyName))
                        found.Add(property.Value);
                    Walk(property.Value, propertyName, found);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Walk(item, propertyName, found);
                break;
        }
    }

    public static JsonElement? Path(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current;
    }

    public static string? Str(JsonElement element, params string[] path) =>
        Path(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>Text from either {"simpleText": ...} or {"runs": [{"text": ...}]} or {"content": ...}.</summary>
    public static string? Text(JsonElement? element)
    {
        if (element is not { } e || e.ValueKind != JsonValueKind.Object)
            return null;
        if (e.TryGetProperty("simpleText", out var simple))
            return simple.GetString();
        if (e.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (e.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            return string.Concat(runs.EnumerateArray().Select(r => r.TryGetProperty("text", out var t) ? t.GetString() : null));
        return null;
    }

    public static string? FirstContinuationToken(JsonElement element) =>
        FindAll(element, "continuationCommand")
            .Select(c => Str(c, "token"))
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));

    public static string? LastContinuationToken(JsonElement element) =>
        FindAll(element, "continuationCommand")
            .Select(c => Str(c, "token"))
            .LastOrDefault(t => !string.IsNullOrEmpty(t));

    public static TimeSpan? ParseClock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        try
        {
            return parts.Length switch
            {
                2 => TimeSpan.FromMinutes(int.Parse(parts[0], CultureInfo.InvariantCulture)) + TimeSpan.FromSeconds(int.Parse(parts[1], CultureInfo.InvariantCulture)),
                3 => new TimeSpan(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture)),
                _ => null
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>"1.2M views", "4M", "889,722 views", "No views" → count.</summary>
    public static long? ParseCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = CountRegex().Match(text.Replace(",", ""));
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => 1_000d,
            "M" => 1_000_000d,
            "B" => 1_000_000_000d,
            _ => 1d
        };
        return (long)(value * multiplier);
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*([KMB])?\b", RegexOptions.IgnoreCase)]
    private static partial Regex CountRegex();

    /// <summary>"104K likes" → "104 thousand likes"; screen readers spell out "K" otherwise.</summary>
    public static string ExpandCounts(string text) =>
        AbbreviatedCountRegex().Replace(text, m => m.Groups[1].Value + (m.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => " thousand",
            "M" => " million",
            _ => " billion"
        }));

    [GeneratedRegex(@"(\d+(?:\.\d+)?)([KMB])(?![A-Za-z])")]
    private static partial Regex AbbreviatedCountRegex();

    /// <summary>"2w ago" → "2 weeks ago"; screen readers say "2 w" otherwise.</summary>
    public static string? ExpandAge(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return ShortAgeRegex().Replace(text, m =>
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "y" => "year",
                "mo" => "month",
                "w" => "week",
                "d" => "day",
                "h" => "hour",
                "m" => "minute",
                "s" => "second",
                _ => m.Groups[2].Value
            };
            return n == 1 ? $"1 {unit} ago" : $"{n} {unit}s ago";
        });
    }

    [GeneratedRegex(@"\b(\d+)\s*(y|mo|w|d|h|m|s)\s+ago\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShortAgeRegex();

    /// <summary>Classic search/watch renderers: videoRenderer, compactVideoRenderer, gridVideoRenderer.</summary>
    public static SearchResult? ParseVideoRenderer(JsonElement vr)
    {
        var videoId = Str(vr, "videoId");
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        var title = Text(Path(vr, "title")) ?? "Untitled video";
        var owner = Path(vr, "ownerText") ?? Path(vr, "shortBylineText") ?? Path(vr, "longBylineText");
        var channel = Text(owner) ?? "Unknown channel";
        var channelId = owner is { } o
            ? FindAll(o, "browseId").Select(b => b.GetString()).FirstOrDefault(id => id?.StartsWith("UC", StringComparison.Ordinal) == true)
            : null;

        var lengthText = Text(Path(vr, "lengthText"));
        var viewText = Text(Path(vr, "viewCountText"));
        bool isLive = FindAll(vr, "style").Any(s => s.ValueKind == JsonValueKind.String && s.GetString() is "BADGE_STYLE_TYPE_LIVE_NOW" or "LIVE")
            || viewText?.Contains("watching", StringComparison.OrdinalIgnoreCase) == true;
        var thumbs = Path(vr, "thumbnail", "thumbnails");

        return new SearchResult
        {
            Id = videoId,
            Title = title,
            ChannelTitle = channel,
            ChannelId = channelId,
            Duration = isLive ? null : ParseClock(lengthText),
            ViewCount = isLive ? null : ParseCount(viewText),
            IsLive = isLive,
            UploadDateText = ExpandAge(Text(Path(vr, "publishedTimeText"))),
            ThumbnailUrl = thumbs is { ValueKind: JsonValueKind.Array } t && t.GetArrayLength() > 0
                ? Str(t[t.GetArrayLength() - 1], "url")
                : $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg",
            DescriptionSnippet = Path(vr, "detailedMetadataSnippets") is { ValueKind: JsonValueKind.Array } s && s.GetArrayLength() > 0
                ? Text(Path(s[0], "snippetText"))
                : null
        };
    }

    /// <summary>The 2024+ "lockup" format used for related videos on the watch page.</summary>
    public static SearchResult? ParseLockup(JsonElement lockup)
    {
        if (Str(lockup, "contentType") != "LOCKUP_CONTENT_TYPE_VIDEO")
            return null; // playlists and mixes
        var videoId = Str(lockup, "contentId");
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        var meta = Path(lockup, "metadata", "lockupMetadataViewModel");
        if (meta is not { } m)
            return null;

        var title = Str(m, "title", "content") ?? "Untitled video";
        var rows = Path(m, "metadata", "contentMetadataViewModel", "metadataRows");
        var parts = new List<List<string>>();
        if (rows is { ValueKind: JsonValueKind.Array } r)
        {
            foreach (var row in r.EnumerateArray())
            {
                var rowParts = new List<string>();
                if (Path(row, "metadataParts") is { ValueKind: JsonValueKind.Array } mp)
                {
                    foreach (var part in mp.EnumerateArray())
                    {
                        if (Str(part, "text", "content") is { Length: > 0 } text)
                            rowParts.Add(text);
                    }
                }
                parts.Add(rowParts);
            }
        }

        var channel = parts.ElementAtOrDefault(0)?.FirstOrDefault() ?? "Unknown channel";
        var stats = parts.ElementAtOrDefault(1) ?? new List<string>();
        var badge = FindAll(lockup, "thumbnailBadgeViewModel").Select(b => Str(b, "text")).FirstOrDefault(t => t is not null);
        bool isLive = string.Equals(badge, "LIVE", StringComparison.OrdinalIgnoreCase)
            || stats.Any(s => s.Contains("watching", StringComparison.OrdinalIgnoreCase));
        var channelId = FindAll(m, "browseId").Select(b => b.GetString()).FirstOrDefault(id => id?.StartsWith("UC", StringComparison.Ordinal) == true);

        return new SearchResult
        {
            Id = videoId,
            Title = title,
            ChannelTitle = channel,
            ChannelId = channelId,
            Duration = isLive ? null : ParseClock(badge),
            ViewCount = isLive ? null : ParseCount(stats.FirstOrDefault()),
            IsLive = isLive,
            UploadDateText = ExpandAge(stats.Skip(1).FirstOrDefault()),
            ThumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg"
        };
    }

    public static WatchNextInfo ParseWatchNext(JsonElement root)
    {
        var related = new List<SearchResult>();
        var seen = new HashSet<string>();
        var secondary = Path(root, "contents", "twoColumnWatchNextResults", "secondaryResults") ?? root;

        foreach (var lockup in FindAll(secondary, "lockupViewModel"))
        {
            if (ParseLockup(lockup) is { } video && seen.Add(video.Id))
                related.Add(video);
        }
        foreach (var compact in FindAll(secondary, "compactVideoRenderer"))
        {
            if (ParseVideoRenderer(compact) is { } video && seen.Add(video.Id))
                related.Add(video);
        }

        string? commentsToken = null;
        foreach (var section in FindAll(root, "itemSectionRenderer"))
        {
            if (Str(section, "sectionIdentifier") == "comment-item-section")
            {
                commentsToken = FirstContinuationToken(section);
                break;
            }
        }

        var description = FindAll(root, "attributedDescription").Select(d => Str(d, "content")).FirstOrDefault(s => s is not null)
            ?? FindAll(root, "videoSecondaryInfoRenderer").Select(v => Text(Path(v, "description"))).FirstOrDefault(s => s is not null);

        return new WatchNextInfo(related, commentsToken, description);
    }

    /// <summary>
    /// Comments come as view models (order, reply tokens) that point at entity payloads (text, author, likes).
    /// </summary>
    public static CommentPage ParseComments(JsonElement root, bool isReplyPage)
    {
        var entities = new Dictionary<string, JsonElement>();
        foreach (var payload in FindAll(root, "commentEntityPayload"))
        {
            if (Str(payload, "key") is { } key)
                entities[key] = payload;
        }

        var comments = new List<Comment>();
        string? nextToken = null;

        foreach (var endpoint in FindAll(root, "continuationItems"))
        {
            if (endpoint.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in endpoint.EnumerateArray())
            {
                if (Path(item, "commentThreadRenderer") is { } thread)
                {
                    var vm = Path(thread, "commentViewModel", "commentViewModel");
                    var repliesToken = Path(thread, "replies") is { } replies ? FirstContinuationToken(replies) : null;
                    if (vm is { } v && ToComment(v, entities, repliesToken, isReply: false) is { } comment)
                        comments.Add(comment);
                }
                else if (Path(item, "commentViewModel") is { } directVm)
                {
                    if (ToComment(directVm, entities, null, isReply: isReplyPage) is { } comment)
                        comments.Add(comment);
                }
                else if (Path(item, "continuationItemRenderer") is { } more)
                {
                    nextToken = FirstContinuationToken(more) ?? nextToken;
                }
            }
        }

        return new CommentPage(comments, nextToken, CommentSource.InnerTube);
    }

    private static Comment? ToComment(JsonElement viewModel, Dictionary<string, JsonElement> entities, string? repliesToken, bool isReply)
    {
        var key = Str(viewModel, "commentKey");
        if (key is null || !entities.TryGetValue(key, out var entity))
            return null;

        var text = Str(entity, "properties", "content", "content");
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var replyCount = (int)(ParseCount(Str(entity, "toolbar", "replyCount")) ?? 0);
        var likes = Str(entity, "toolbar", "likeCountA11y");
        return new Comment
        {
            Id = Str(entity, "properties", "commentId") ?? key,
            Author = Str(entity, "author", "displayName") ?? "Unknown",
            AuthorChannelId = Str(entity, "author", "channelId"),
            Text = text.Trim(),
            PublishedText = ExpandAge(Str(entity, "properties", "publishedTime")),
            LikesText = string.IsNullOrWhiteSpace(likes) || likes.StartsWith("0 ", StringComparison.Ordinal) ? null : ExpandCounts(likes),
            ReplyCount = replyCount,
            IsCreator = Path(entity, "author", "isCreator") is { ValueKind: JsonValueKind.True },
            IsPinned = Path(viewModel, "pinnedText") is not null || Str(entity, "properties", "pinnedText") is not null,
            IsReply = isReply || Path(entity, "properties", "replyLevel") is { ValueKind: JsonValueKind.Number } level && level.GetInt32() > 0,
            RepliesToken = repliesToken,
            Source = CommentSource.InnerTube
        };
    }
}
