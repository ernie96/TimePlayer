using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;

namespace TimePlayer.Wpf.ViewModels;

public enum PlayerPanel
{
    UpNext = 0,
    Comments = 1,
    Description = 2,
    Queue = 3
}

/// <summary>
/// The player's side panel: Up next (related videos and autoplay), comments, description and queue.
/// Everything loads quietly in the background; only results the user asked for are spoken.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly IWatchPageService _watchPage;
    private CancellationTokenSource? _watchPageCts;
    private string? _watchPageVideoId;
    private CommentPage? _lastCommentPage;
    private bool _commentsRequested;
    private bool _speakNextCommentsLoad;

    public ObservableCollection<SearchResult> UpNext { get; } = new();
    public ObservableCollection<CommentNode> Comments { get; } = new();

    [ObservableProperty] private SearchResult? _selectedUpNext;
    [ObservableProperty] private CommentNode? _selectedComment;
    [ObservableProperty] private bool _isLoadingComments;
    [ObservableProperty] private string _commentsStatus = "Open this tab to load comments.";
    [ObservableProperty] private string _videoDescription = string.Empty;
    [ObservableProperty] private PlayerPanel _selectedPlayerPanel = PlayerPanel.UpNext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoplayButtonText))]
    private bool _autoplay = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeakCaptionsButtonText))]
    private bool _speakCaptions;

    public string AutoplayButtonText => Autoplay ? "Autoplay: on" : "Autoplay: off";
    public string SpeakCaptionsButtonText => SpeakCaptions ? "Read captions: on" : "Read captions: off";
    public bool CanLoadMoreComments => _lastCommentPage is { NextPageToken: { Length: > 0 } };

    private void InitializePlayerPreferences()
    {
        _autoplay = _configService.CurrentConfig.Library.Autoplay;
        _speakCaptions = _configService.CurrentConfig.Accessibility.SpeakCaptions;
    }

    private static SearchResult ToSearchResult(PlaybackQueueItem item) => new()
    {
        Id = item.VideoId.Value,
        Title = item.Title,
        ChannelTitle = item.ChannelTitle,
        Duration = item.Duration > TimeSpan.Zero ? item.Duration : null
    };

    // --- Loading ---

    /// <summary>Called whenever the player switches to another video.</summary>
    private void ResetWatchPage(string? videoId)
    {
        if (videoId == _watchPageVideoId)
            return;

        _watchPageCts?.Cancel();
        _watchPageCts?.Dispose();
        _watchPageCts = new CancellationTokenSource();
        _watchPageVideoId = videoId;
        _lastCommentPage = null;
        _commentsRequested = false;
        UpNext.Clear();
        Comments.Clear();
        VideoDescription = string.Empty;
        CommentsStatus = "Open this tab to load comments.";
        OnPropertyChanged(nameof(CanLoadMoreComments));

        ResetCommunityData(videoId, _watchPageCts.Token);
        if (videoId is null)
            return;

        _ = LoadUpNextAsync(videoId, _watchPageCts.Token);
        if (SelectedPlayerPanel == PlayerPanel.Comments)
            _ = LoadCommentsAsync(announce: false);
    }

    private async Task LoadUpNextAsync(string videoId, CancellationToken token)
    {
        try
        {
            var result = await _watchPage.GetWatchNextAsync(videoId, token);
            if (token.IsCancellationRequested || !result.IsSuccess)
                return;

            UpNext.Clear();
            foreach (var video in result.Value.Related.Where(v => v.Id != videoId))
                UpNext.Add(Decorate(video));
            SelectedUpNext = UpNext.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(result.Value.Description) && string.IsNullOrWhiteSpace(VideoDescription))
                VideoDescription = result.Value.Description;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load related videos for {Id}.", videoId);
        }
    }

    partial void OnSelectedPlayerPanelChanged(PlayerPanel value)
    {
        OnPropertyChanged(nameof(SelectedPlayerPanelIndex));
        if (value == PlayerPanel.Comments && !_commentsRequested)
            _ = LoadCommentsAsync(announce: true);
    }

    [RelayCommand]
    public Task LoadMoreCommentsAsync() => LoadCommentsAsync(announce: false);

    private async Task LoadCommentsAsync(bool announce)
    {
        var videoId = _watchPageVideoId;
        if (videoId is null || IsLoadingComments)
            return;
        if (_commentsRequested && !CanLoadMoreComments)
            return;

        var token = _watchPageCts?.Token ?? CancellationToken.None;
        bool firstPage = _lastCommentPage is null;
        _commentsRequested = true;
        IsLoadingComments = true;
        if (firstPage)
            CommentsStatus = "Loading comments...";

        try
        {
            var result = await _watchPage.GetCommentsAsync(videoId, _lastCommentPage, token);
            if (token.IsCancellationRequested || videoId != _watchPageVideoId)
                return;

            if (!result.IsSuccess)
            {
                if (firstPage)
                {
                    CommentsStatus = "Comments could not be loaded. They may be turned off for this video.";
                    if (announce) _accessibility.Announce(CommentsStatus, AnnouncementPriority.Medium);
                }
                return;
            }

            _lastCommentPage = result.Value;
            var existing = Comments.Where(n => n.Comment is not null).Select(n => n.Comment!.Id).ToHashSet();
            int added = 0;
            foreach (var comment in result.Value.Comments)
            {
                if (existing.Add(comment.Id))
                {
                    Comments.Add(CommentNode.ForComment(comment with { IsReply = false }, level: 1, OnCommentExpanded));
                    added++;
                }
            }

            if (firstPage)
            {
                SelectedComment = Comments.FirstOrDefault();
                CommentsStatus = Comments.Count == 0 ? "No comments yet." : string.Empty;
                if (announce)
                    _accessibility.Announce(Comments.Count == 0 ? "No comments yet." : $"{Comments.Count} comments loaded.", AnnouncementPriority.Medium);
            }
            else if (_speakNextCommentsLoad && added > 0)
            {
                _accessibility.Announce($"{added} more comments.", AnnouncementPriority.Low);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load comments for {Id}.", videoId);
        }
        finally
        {
            _speakNextCommentsLoad = false;
            IsLoadingComments = false;
            OnPropertyChanged(nameof(CanLoadMoreComments));
        }
    }

    partial void OnSelectedCommentChanged(CommentNode? value)
    {
        // Only top-level comments page; replies are inside their parent.
        if (value is null || value.Level != 1) return;
        var index = Comments.IndexOf(value);
        if (index >= Comments.Count - 5 && CanLoadMoreComments)
        {
            // Only speak about it if the user actually reached the end while it loads.
            if (index == Comments.Count - 1)
                _speakNextCommentsLoad = true;
            _ = LoadCommentsAsync(announce: false);
        }
    }

    /// <summary>Right arrow (or Enter) on a comment expands it: its replies load under it, one level deeper.</summary>
    private void OnCommentExpanded(CommentNode node)
    {
        if (!node.RepliesLoaded)
            _ = LoadRepliesAsync(node, replace: node.Replies.FirstOrDefault(r => r.Kind == CommentNodeKind.Loading));
    }

    private async Task LoadRepliesAsync(CommentNode parent, CommentNode? replace, string? pageToken = null)
    {
        if (_watchPageVideoId is not { } videoId || parent.Comment is not { } comment)
            return;
        parent.RepliesLoaded = true;

        var source = pageToken is null ? comment : comment with { RepliesToken = pageToken };
        var result = await _watchPage.GetRepliesAsync(videoId, source, _watchPageCts?.Token ?? CancellationToken.None);
        if (videoId != _watchPageVideoId)
            return;

        int insertAt = replace is null ? parent.Replies.Count : parent.Replies.IndexOf(replace);
        if (replace is not null)
            parent.Replies.Remove(replace);
        if (insertAt < 0) insertAt = parent.Replies.Count;

        if (!result.IsSuccess || result.Value.Comments.Count == 0)
        {
            if (pageToken is null)
            {
                parent.RepliesLoaded = false;
                parent.IsExpanded = false;
                _accessibility.Announce("Replies could not be loaded.", AnnouncementPriority.Medium);
            }
            return;
        }

        foreach (var reply in result.Value.Comments)
            parent.Replies.Insert(insertAt++, CommentNode.ForComment(reply with { IsReply = true }, parent.Level + 1, null));
        if (!string.IsNullOrEmpty(result.Value.NextPageToken))
            parent.Replies.Insert(insertAt, CommentNode.MoreReplies(comment, result.Value.NextPageToken, parent.Level + 1));

        int count = result.Value.Comments.Count;
        if (pageToken is null)
            _accessibility.Announce(count == 1 ? "1 reply" : $"{count} replies", AnnouncementPriority.Low);
    }

    /// <summary>Enter: expands or collapses a comment, or loads the next page of replies.</summary>
    [RelayCommand]
    public async Task ToggleRepliesAsync(CommentNode? node)
    {
        node ??= SelectedComment;
        if (node is null)
            return;

        if (node.Kind == CommentNodeKind.MoreReplies)
        {
            var parent = Comments.FirstOrDefault(c => c.Replies.Contains(node));
            if (parent is not null && node.MoreToken is { } token)
                await LoadRepliesAsync(parent, node, token);
            return;
        }

        if (node.Kind != CommentNodeKind.Comment)
            return;
        if (node.Replies.Count == 0)
        {
            _accessibility.Announce("No replies.", AnnouncementPriority.Low);
            return;
        }
        node.IsExpanded = !node.IsExpanded;
    }

    [RelayCommand]
    public void CopyComment(CommentNode? node)
    {
        node ??= SelectedComment;
        if (node?.Comment is not { } comment || node.Kind != CommentNodeKind.Comment) return;
        if (CopyToClipboard($"{comment.Author}: {comment.Text}"))
            _accessibility.Announce("Comment copied.", AnnouncementPriority.Low);
    }

    // --- Autoplay ---

    private SearchResult? NextAutoplayCandidate() =>
        UpNext.FirstOrDefault(v =>
            v.Kind == SearchResultKind.Video &&
            !v.IsLive &&
            v.Id != _currentLibraryVideo?.Id &&
            _library.GetHistory(v.Id) is not { IsFinished: true });

    private void OnCoordinatorQueueEnded(object? sender, EventArgs e)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnCoordinatorQueueEnded(sender, e), null);
            return;
        }

        if (Autoplay && IsPlayerDialogOpen && NextAutoplayCandidate() is { } next)
        {
            _accessibility.Announce($"Up next: {next.Title}", AnnouncementPriority.Medium);
            _ = ContinueWithAsync(next);
        }
        else
        {
            _accessibility.Announce("Video ended.", AnnouncementPriority.Medium);
        }
    }

    /// <summary>Next in the queue, or else the first Up next video.</summary>
    [RelayCommand]
    public async Task PlayNextOrUpNextAsync()
    {
        if (CanPlayNext)
        {
            await PlayNextAsync();
        }
        else if (NextAutoplayCandidate() is { } next)
        {
            await ContinueWithAsync(next);
        }
        else
        {
            _accessibility.Announce("Nothing up next yet.", AnnouncementPriority.Medium);
        }
    }

    [RelayCommand]
    public void ToggleAutoplay()
    {
        Autoplay = !Autoplay;
        _accessibility.Announce(Autoplay ? "Autoplay on." : "Autoplay off.", AnnouncementPriority.Medium);
        _ = PersistPlayerPreferencesAsync();
    }

    [RelayCommand]
    public void ToggleSpeakCaptions()
    {
        SpeakCaptions = !SpeakCaptions;
        bool lookingUp = _playbackCoordinator is TimePlayer.Core.Playback.PlaybackCoordinator { CaptionsLookupInProgress: true };
        string message = !SpeakCaptions ? "Captions will not be read aloud."
            : AvailableSubtitleTracks.Count > 1 || !IsPlayerDialogOpen ? "Captions will be read aloud."
            : lookingUp ? "Captions will be read aloud. Still looking for this video's captions."
            : "Captions will be read aloud, but this video has no captions.";
        _accessibility.Announce(message, AnnouncementPriority.Medium);
        _ = PersistPlayerPreferencesAsync();
    }

    /// <summary>Captions found after playback started (the stream source had none).</summary>
    private void OnCoordinatorCaptionsChanged(object? sender, EventArgs e)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnCoordinatorCaptionsChanged(sender, e), null);
            return;
        }
        PopulateSubtitleTracks();
        if (SpeakCaptions && IsPlayerDialogOpen && AvailableSubtitleTracks.Count <= 1)
            _accessibility.Announce("This video has no captions.", AnnouncementPriority.Low);
    }

    /// <summary>
    /// Every play command starts a fresh viewing: the resume check runs again even for the video
    /// that was just closed, and the video being left saves where it was.
    /// </summary>
    private void BeginNewPlayback()
    {
        if (IsPlayerDialogOpen && PlaybackState is PlaybackState.Playing or PlaybackState.Paused)
            RecordCurrentProgress(CurrentTime, flush: true);
        _resumeHandledForId = null;
        _segments.Rewind();
    }

    /// <summary>U in the player: what autoplay would play next, without moving focus.</summary>
    [RelayCommand]
    public void AnnounceUpNext()
    {
        var next = PlaybackQueue.Count > 0 && CurrentQueueItem is { } current && PlaybackQueue.IndexOf(current) is var i && i >= 0 && i + 1 < PlaybackQueue.Count
            ? $"Next in queue: {PlaybackQueue[i + 1].Title}"
            : NextAutoplayCandidate() is { } candidate
                ? $"Up next{(Autoplay ? "" : ", autoplay off")}: {candidate.Title}, by {candidate.ChannelTitle}"
                : "Nothing up next yet.";
        _accessibility.Announce(next, AnnouncementPriority.High);
    }

    private async Task PersistPlayerPreferencesAsync()
    {
        try
        {
            var current = _configService.CurrentConfig;
            await _configService.UpdateConfigAsync(current with
            {
                Library = current.Library with { Autoplay = Autoplay },
                Accessibility = current.Accessibility with { SpeakCaptions = SpeakCaptions }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save player preferences.");
        }
    }

    private async Task PersistVolumeAsync()
    {
        try
        {
            var current = _configService.CurrentConfig;
            if (Math.Abs(current.Playback.DefaultVolumePercent - VolumePercent) < 0.5)
                return;
            await _configService.UpdateConfigAsync(current with
            {
                Playback = current.Playback with { DefaultVolumePercent = VolumePercent }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save volume.");
        }
    }
}
