using TimePlayer.Core.Common;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Contracts;

/// <summary>
/// High-level coordinator that manages queue progression, resolves video media with failover,
/// controls the underlying IPlaybackEngine, and reports unified state changes to UI / ViewModels.
/// </summary>
public interface IPlaybackCoordinator
{
    PlaybackState State { get; }
    VideoDetails? CurrentVideo { get; }
    IReadOnlyList<PlaybackQueueItem> Queue { get; }
    PlaybackQueueItem? CurrentQueueItem { get; }

    event EventHandler<PlaybackState>? StateChanged;
    event EventHandler<VideoDetails?>? CurrentVideoChanged;
    event EventHandler<PlaybackError>? ErrorOccurred;

    Task<Result<PagedResult<SearchResult>>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<Result<bool>> PlayUrlOrIdAsync(string urlOrId, CancellationToken cancellationToken = default);
    Task<Result<bool>> PlayQueueItemAsync(Guid queueItemId, CancellationToken cancellationToken = default);
    Task EnqueueAsync(SearchResult item);
    Task PlayNextAsync(CancellationToken cancellationToken = default);
    Task PlayPreviousAsync(CancellationToken cancellationToken = default);
    Task TogglePlayPauseAsync(CancellationToken cancellationToken = default);
    Task SeekRelativeAsync(double seconds, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default);
    Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default);
}
