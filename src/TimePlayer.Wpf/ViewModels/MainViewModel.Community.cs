using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Community;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>A SponsorBlock category's row in Settings.</summary>
public sealed partial class SegmentCategorySetting : ObservableObject
{
    private readonly Action _changed;

    public SegmentCategorySetting(SegmentCategory category, string label, SegmentAction action, Action changed)
    {
        Category = category;
        Label = label;
        _action = action;
        _changed = changed;
    }

    public SegmentCategory Category { get; }
    /// <summary>With an access-key underscore, for the visible label.</summary>
    public string Label { get; }
    /// <summary>For screen readers: the label without its access-key marker.</summary>
    public string SpokenLabel => Label.Replace("_", string.Empty);

    [ObservableProperty]
    private SegmentAction _action;

    partial void OnActionChanged(SegmentAction value) => _changed();
}

public sealed record ChoiceOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Community data: SponsorBlock segments (skipped or announced as the video plays) and
/// Return YouTube Dislike counts (in the description and the I key's summary).
/// </summary>
public sealed partial class MainViewModel
{
    private readonly ISponsorBlockService _sponsorBlock;
    private readonly IDislikeService _dislikes;
    private readonly SegmentTracker _segments = new();
    private bool _isSkippingSegment;
    /// <summary>After a resume, positions from before the jump still arrive; skipping on them would undo the resume.</summary>
    private DateTimeOffset _segmentsQuietUntil;

    /// <summary>Called when the player jumps on its own (resume), so segment handling waits for the new position.</summary>
    private void PauseSegmentHandlingBriefly() => _segmentsQuietUntil = DateTimeOffset.UtcNow.AddSeconds(2);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoDescriptionText))]
    private VideoVotes? _currentVotes;

    public IReadOnlyList<ChoiceOption<SegmentAction>> SegmentActionChoices { get; } = new[]
    {
        new ChoiceOption<SegmentAction>(SegmentAction.Skip, "Skip automatically"),
        new ChoiceOption<SegmentAction>(SegmentAction.Announce, "Tell me (S skips it)"),
        new ChoiceOption<SegmentAction>(SegmentAction.Off, "Do nothing")
    };

    public IReadOnlyList<SegmentCategorySetting> SponsorBlockCategories { get; private set; } = Array.Empty<SegmentCategorySetting>();

    [ObservableProperty] private bool _sponsorBlockEnabled = true;
    [ObservableProperty] private bool _announceSegmentSkips = true;
    [ObservableProperty] private bool _announceHighlight = true;
    [ObservableProperty] private bool _announceWholeVideoLabels = true;
    [ObservableProperty] private bool _showDislikes = true;

    private bool _communitySettingsLoaded;

    private void InitializeCommunitySettings()
    {
        var sb = _configService.CurrentConfig.SponsorBlock;
        _sponsorBlockEnabled = sb.Enabled;
        _announceSegmentSkips = sb.AnnounceSkips;
        _announceHighlight = sb.AnnounceHighlight;
        _announceWholeVideoLabels = sb.AnnounceWholeVideoLabels;
        _showDislikes = _configService.CurrentConfig.Dislikes.Enabled;

        void Changed() => _ = PersistCommunitySettingsAsync();
        SponsorBlockCategories = new[]
        {
            new SegmentCategorySetting(SegmentCategory.Sponsor, "_Sponsor (paid promotion)", sb.Sponsor, Changed),
            new SegmentCategorySetting(SegmentCategory.SelfPromotion, "Unpaid or _self-promotion", sb.SelfPromotion, Changed),
            new SegmentCategorySetting(SegmentCategory.Interaction, "Reminders to _like or subscribe", sb.Interaction, Changed),
            new SegmentCategorySetting(SegmentCategory.Intro, "_Intro animation or opening", sb.Intro, Changed),
            new SegmentCategorySetting(SegmentCategory.Outro, "_Outro and end cards", sb.Outro, Changed),
            new SegmentCategorySetting(SegmentCategory.Preview, "_Preview or recap", sb.Preview, Changed),
            new SegmentCategorySetting(SegmentCategory.Filler, "_Filler and tangents", sb.Filler, Changed),
            new SegmentCategorySetting(SegmentCategory.NonMusic, "_Non-music parts of music videos", sb.NonMusic, Changed),
        };
        _communitySettingsLoaded = true;
    }

    partial void OnSponsorBlockEnabledChanged(bool value) => _ = PersistCommunitySettingsAsync();
    partial void OnAnnounceSegmentSkipsChanged(bool value) => _ = PersistCommunitySettingsAsync();
    partial void OnAnnounceHighlightChanged(bool value) => _ = PersistCommunitySettingsAsync();
    partial void OnAnnounceWholeVideoLabelsChanged(bool value) => _ = PersistCommunitySettingsAsync();
    partial void OnShowDislikesChanged(bool value)
    {
        if (!value)
            CurrentVotes = null;
        _ = PersistCommunitySettingsAsync();
    }

    private SponsorBlockConfig SponsorBlockSettings
    {
        get
        {
            var settings = _configService.CurrentConfig.SponsorBlock with
            {
                Enabled = SponsorBlockEnabled,
                AnnounceSkips = AnnounceSegmentSkips,
                AnnounceHighlight = AnnounceHighlight,
                AnnounceWholeVideoLabels = AnnounceWholeVideoLabels
            };
            SegmentAction Of(SegmentCategory c) => SponsorBlockCategories.FirstOrDefault(s => s.Category == c)?.Action ?? SegmentTracker.ActionFor(settings, c);
            return settings with
            {
                Sponsor = Of(SegmentCategory.Sponsor),
                SelfPromotion = Of(SegmentCategory.SelfPromotion),
                Interaction = Of(SegmentCategory.Interaction),
                Intro = Of(SegmentCategory.Intro),
                Outro = Of(SegmentCategory.Outro),
                Preview = Of(SegmentCategory.Preview),
                Filler = Of(SegmentCategory.Filler),
                NonMusic = Of(SegmentCategory.NonMusic)
            };
        }
    }

    private async Task PersistCommunitySettingsAsync()
    {
        if (!_communitySettingsLoaded)
            return;
        var current = _configService.CurrentConfig;
        try
        {
            await _configService.UpdateConfigAsync(current with
            {
                SponsorBlock = SponsorBlockSettings,
                Dislikes = current.Dislikes with { Enabled = ShowDislikes }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save SponsorBlock and dislike settings.");
        }
    }

    // --- Loading for each video ---

    private void ResetCommunityData(string? videoId, CancellationToken token)
    {
        _segments.Reset(videoId);
        CurrentVotes = null;
        if (videoId is null)
            return;
        _ = LoadSegmentsAsync(videoId, token);
        _ = LoadVotesAsync(videoId, token);
    }

    private async Task LoadSegmentsAsync(string videoId, CancellationToken token)
    {
        try
        {
            var segments = await _sponsorBlock.GetSegmentsAsync(videoId, token);
            if (!token.IsCancellationRequested && _segments.VideoId == videoId)
                _segments.Reset(videoId, segments);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "SponsorBlock segments unavailable for {Id}.", videoId);
        }
    }

    private async Task LoadVotesAsync(string videoId, CancellationToken token)
    {
        try
        {
            var votes = ShowDislikes ? await _dislikes.GetVotesAsync(videoId, token) : null;
            if (!token.IsCancellationRequested && CurrentVideoId == videoId)
                CurrentVotes = votes;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Vote counts unavailable for {Id}.", videoId);
        }
    }

    // --- As the video plays ---

    private void OnSegmentPosition(TimeSpan position)
    {
        if (_isSkippingSegment || PlaybackState != PlaybackState.Playing ||
            _segments.VideoId is null || _segments.VideoId != CurrentVideoId ||
            // Wait for the resume decision, or a skip at 0:00 could land after the jump to the resume point.
            _resumeHandledForId != CurrentVideoId || DateTimeOffset.UtcNow < _segmentsQuietUntil)
            return;

        foreach (var e in _segments.Update(position.TotalSeconds, SponsorBlockSettings))
        {
            switch (e)
            {
                case SegmentEvent.Skip skip:
                    _ = SkipSegmentAsync(skip.Segment, automatic: true);
                    break;
                case SegmentEvent.Announce announce:
                    _accessibility.Announce(
                        $"{Capitalize(announce.Segment.Name)}, {AccessibleTextFormatter.FormatTimeSpoken(announce.Segment.Length)}. S skips it.",
                        AnnouncementPriority.Medium);
                    break;
                case SegmentEvent.WholeVideo label:
                    _accessibility.Announce($"This whole video is labeled {label.Segment.Name}.", AnnouncementPriority.Low);
                    break;
                case SegmentEvent.HighlightAvailable highlight:
                    _accessibility.Announce(
                        $"Highlight at {AccessibleTextFormatter.FormatTimeSpoken(TimeSpan.FromSeconds(highlight.Segment.Start))}. H jumps to it.",
                        AnnouncementPriority.Low);
                    break;
            }
        }
    }

    private async Task SkipSegmentAsync(SkipSegment segment, bool automatic)
    {
        if (!IsEngineInitialized)
            return;
        _isSkippingSegment = true;
        try
        {
            var target = TimeSpan.FromSeconds(segment.End);
            CurrentTime = ClampToDuration(target);
            UpdateBrailleDisplayLine();
            await _playerEngine.SeekAsync(target);
            if (!automatic || AnnounceSegmentSkips)
            {
                _accessibility.Announce($"Skipped {segment.Name}, {AccessibleTextFormatter.FormatTimeSpoken(segment.Length)}.",
                    automatic ? AnnouncementPriority.Low : AnnouncementPriority.Medium);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not skip a SponsorBlock segment.");
        }
        finally
        {
            _isSkippingSegment = false;
        }
    }

    /// <summary>S in the player: skip the SponsorBlock segment playing now.</summary>
    [RelayCommand]
    public async Task SkipCurrentSegmentAsync()
    {
        if (!SponsorBlockEnabled)
        {
            _accessibility.Announce("SponsorBlock is turned off in Settings.", AnnouncementPriority.Medium);
            return;
        }
        if (_segments.SegmentAt(CurrentTime.TotalSeconds, SponsorBlockSettings) is { } segment)
            await SkipSegmentAsync(segment, automatic: false);
        else
            _accessibility.Announce(_segments.Segments.Count == 0 ? "No SponsorBlock segments for this video." : "No segment playing now.", AnnouncementPriority.Medium);
    }

    /// <summary>H in the player: jump to the highlight SponsorBlock users marked.</summary>
    [RelayCommand]
    public async Task JumpToHighlightAsync()
    {
        if (!SponsorBlockEnabled || _segments.Highlight is not { } highlight)
        {
            _accessibility.Announce("This video has no highlight.", AnnouncementPriority.Medium);
            return;
        }
        if (!IsEngineInitialized)
            return;
        var target = TimeSpan.FromSeconds(highlight.Start);
        CurrentTime = target;
        UpdateBrailleDisplayLine();
        await _playerEngine.SeekAsync(target);
        _accessibility.Announce($"Highlight, {AccessibleTextFormatter.FormatTimeSpoken(target)}.", AnnouncementPriority.Medium);
    }

    /// <summary>What SponsorBlock knows about this video, for the I key.</summary>
    private string? SegmentSummary()
    {
        if (!SponsorBlockEnabled || _segments.VideoId != CurrentVideoId)
            return null;
        var skippable = _segments.Segments.Where(s => s.Kind == SegmentKind.Skip).ToList();
        if (skippable.Count == 0)
            return null;
        return skippable.Count == 1
            ? $"1 SponsorBlock segment: {skippable[0].Name}"
            : $"{skippable.Count} SponsorBlock segments";
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}
