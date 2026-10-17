namespace TimePlayer.Core.Models;

/// <summary>
/// Represents a single YouTube search result or queue item.
/// </summary>
public record SearchResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string ChannelTitle { get; init; }
    public string? ChannelId { get; init; }
    public TimeSpan? Duration { get; init; }
    public long? ViewCount { get; init; }
    public string? UploadDateText { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? DescriptionSnippet { get; init; }

    /// <summary>
    /// Gets a fully composed, natural speech summary designed specifically for screen readers.
    /// Follows accessibility guidelines: Title, Channel, Duration in words, and Views.
    /// </summary>
    public string AccessibleSummary
    {
        get
        {
            var parts = new List<string> { Title, $"by {ChannelTitle}" };

            if (Duration.HasValue)
            {
                var d = Duration.Value;
                if (d.TotalHours >= 1)
                {
                    parts.Add($"{(int)d.TotalHours} hour{( (int)d.TotalHours == 1 ? "" : "s" )}, {d.Minutes} minute{( d.Minutes == 1 ? "" : "s" )}, {d.Seconds} second{( d.Seconds == 1 ? "" : "s" )}");
                }
                else if (d.TotalMinutes >= 1)
                {
                    parts.Add($"{d.Minutes} minute{( d.Minutes == 1 ? "" : "s" )}, {d.Seconds} second{( d.Seconds == 1 ? "" : "s" )}");
                }
                else
                {
                    parts.Add($"{d.Seconds} second{( d.Seconds == 1 ? "" : "s" )}");
                }
            }

            if (ViewCount.HasValue)
            {
                parts.Add(FormatViewCount(ViewCount.Value));
            }

            if (!string.IsNullOrWhiteSpace(UploadDateText))
            {
                parts.Add(UploadDateText);
            }

            return string.Join(". ", parts);
        }
    }

    private static string FormatViewCount(long count)
    {
        return count switch
        {
            >= 1_000_000_000 => $"{count / 1_000_000_000.0:0.#} billion views",
            >= 1_000_000 => $"{count / 1_000_000.0:0.#} million views",
            >= 1_000 => $"{count / 1_000.0:0.#} thousand views",
            1 => "1 view",
            _ => $"{count} views"
        };
    }
}
