namespace TimePlayer.Core.Models;

/// <summary>
/// Resolved stream setup ready for player engine loading.
/// Handles both single progressive streams and separate DASH video+audio tracks.
/// </summary>
public record StreamResolutionResult
{
    public required YouTubeVideoId VideoId { get; init; }
    public required StreamCandidate SelectedVideoStream { get; init; }
    public StreamCandidate? SelectedAudioStream { get; init; }
    public required string DirectPlaybackUrl { get; init; }
    public string? AudioTrackUrl { get; init; }
    public IReadOnlyList<StreamCandidate> AlternativeStreams { get; init; } = Array.Empty<StreamCandidate>();
    public IReadOnlyList<CaptionTrack> Captions { get; init; } = Array.Empty<CaptionTrack>();
    public bool IsLiveStream { get; init; }
    public BackendProviderType SourceProvider { get; init; }
}
