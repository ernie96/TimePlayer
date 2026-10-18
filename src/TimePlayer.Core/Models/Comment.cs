namespace TimePlayer.Core.Models;

/// <summary>Where a comment came from; reply tokens only work with the backend that issued them.</summary>
public enum CommentSource
{
    InnerTube,
    Invidious
}

public sealed record Comment
{
    public required string Id { get; init; }
    public required string Author { get; init; }
    public required string Text { get; init; }
    public string? AuthorChannelId { get; init; }
    public string? PublishedText { get; init; }
    /// <summary>Spoken form, e.g. "104K likes".</summary>
    public string? LikesText { get; init; }
    public int ReplyCount { get; init; }
    public bool IsPinned { get; init; }
    /// <summary>Written by the video's own channel.</summary>
    public bool IsCreator { get; init; }
    public bool IsReply { get; init; }
    public string? RepliesToken { get; init; }
    public CommentSource Source { get; init; }

    public bool HasReplies => ReplyCount > 0 && !string.IsNullOrEmpty(RepliesToken);

    /// <summary>
    /// What a screen reader says on the row: who, then what they said, then the details.
    /// Kept to one sentence of metadata so arrowing through comments stays quick.
    /// </summary>
    public string AccessibleSummary
    {
        get
        {
            var who = Author;
            if (IsCreator) who += ", channel owner";
            if (IsReply) who = "Reply from " + who;
            if (IsPinned) who = "Pinned. " + who;

            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(PublishedText)) details.Add(PublishedText);
            if (!string.IsNullOrWhiteSpace(LikesText)) details.Add(LikesText);
            if (ReplyCount > 0) details.Add(ReplyCount == 1 ? "1 reply" : $"{ReplyCount} replies");

            return details.Count > 0
                ? $"{who}: {Text} ({string.Join(", ", details)})"
                : $"{who}: {Text}";
        }
    }

    public override string ToString() => AccessibleSummary;
}

public sealed record CommentPage(IReadOnlyList<Comment> Comments, string? NextPageToken, CommentSource Source);

/// <summary>Related videos for the watch page, plus what is needed to load its comments.</summary>
public sealed record WatchNextInfo(IReadOnlyList<SearchResult> Related, string? CommentsToken, string? Description);
