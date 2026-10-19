namespace TimePlayer.Core.Models;

/// <summary>
/// Detailed metadata for a YouTube channel / uploader.
/// </summary>
public record ChannelInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Url { get; init; }
    public string? AvatarUrl { get; init; }
    public bool IsVerified { get; init; }

}
