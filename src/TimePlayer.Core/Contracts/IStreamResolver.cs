using TimePlayer.Core.Common;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Contracts;

/// <summary>
/// Contract for resolving playable media streams from candidate formats.
/// Supports quality preferences, progressive streams, and separate DASH video+audio tracks.
/// </summary>
public interface IStreamResolver
{
    Task<Result<StreamResolutionResult>> ResolveStreamsAsync(
        YouTubeVideoId videoId,
        QualityPreference qualityPreference = QualityPreference.Auto,
        bool audioOnly = false,
        CancellationToken cancellationToken = default);
}
