namespace TimePlayer.Core.Models;

/// <summary>
/// Detailed metadata for a YouTube channel / uploader.
/// </summary>
public record ChannelInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Url { get; init; }
    public long? SubscriberCount { get; init; }
    public string? AvatarUrl { get; init; }
    public bool IsVerified { get; init; }

    /// <summary>
    /// Screen reader friendly summary of channel name, subscriber count, and verified badge.
    /// </summary>
    public string AccessibleDescription
    {
        get
        {
            var parts = new List<string> { Name };

            if (SubscriberCount.HasValue)
            {
                var count = SubscriberCount.Value;
                var subStr = count switch
                {
                    >= 1_000_000_000 => $"{count / 1_000_000_000.0:0.#} billion subscribers",
                    >= 1_000_000 => $"{count / 1_000_000.0:0.#} million subscribers",
                    >= 1_000 => $"{count / 1_000.0:0.#} thousand subscribers",
                    1 => "1 subscriber",
                    _ => $"{count} subscribers"
                };
                parts.Add(subStr);
            }

            if (IsVerified)
            {
                parts.Add("verified channel");
            }

            return string.Join(", ", parts);
        }
    }
}
