using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Community;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>
/// Chapters (from the description's timestamps) and the transcript (the captions as a document).
/// Shift+Left and Shift+Right move between chapters, Alt+H lists them, Alt+T opens the transcript.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly ITranscriptService? _transcripts;

    // ----- Chapters -----

    public ObservableCollection<Chapter> Chapters { get; } = new();
    [ObservableProperty] private Chapter? _selectedChapter;
    [ObservableProperty] private bool _announceChapters;

    private IReadOnlyList<TimeSpan> _chapterStarts = Array.Empty<TimeSpan>();
    private Chapter? _lastChapter;

    private void InitializeChapters()
    {
        _announceChapters = _configService.CurrentConfig.Accessibility.AnnounceChapters;
    }

    partial void OnAnnounceChaptersChanged(bool value)
    {
        var current = _configService.CurrentConfig;
        if (current.Accessibility.AnnounceChapters != value)
            _ = _configService.UpdateConfigAsync(current with { Accessibility = current.Accessibility with { AnnounceChapters = value } });
    }

    /// <summary>Reads the chapters again when the description arrives.</summary>
    private void RefreshChapters()
    {
        var duration = Duration > TimeSpan.Zero ? Duration : (CurrentVideo?.Duration ?? TimeSpan.Zero);
        var chapters = TimePlayer.Core.Models.Chapters.FromDescription(VideoDescription, duration);
        Chapters.Clear();
        foreach (var chapter in chapters)
            Chapters.Add(chapter);
        SelectedChapter = Chapters.FirstOrDefault();
        _chapterStarts = chapters.Select(c => c.Start).ToList();
        _lastChapter = null;
        UpdateBrailleDisplayLine();
    }

    /// <summary>Each position update: says a new chapter's name, if asked to in Settings.</summary>
    private void OnChapterPosition(TimeSpan position)
    {
        if (Chapters.Count == 0)
            return;
        var chapter = TimePlayer.Core.Models.Chapters.At(Chapters, position);
        if (chapter is null || chapter == _lastChapter)
            return;
        var first = _lastChapter is null;
        _lastChapter = chapter;
        if (AnnounceChapters && !first)
            _accessibility.Announce(T("Chapter {0}: {1}", chapter.Number, chapter.Title), AnnouncementPriority.Low);
    }

    /// <summary>"Chapter 3 of 8: Setting up", for the time announcement (T).</summary>
    private string? CurrentChapterSpoken()
    {
        var chapter = TimePlayer.Core.Models.Chapters.At(Chapters, CurrentTime);
        return chapter is null ? null : T("Chapter {0} of {1}: {2}.", chapter.Number, Chapters.Count, chapter.Title);
    }

    [RelayCommand]
    public Task NextChapterAsync() => GoToChapterAsync(TimePlayer.Core.Models.Chapters.Next(Chapters, CurrentTime), T("This is the last chapter."));

    [RelayCommand]
    public Task PreviousChapterAsync() => GoToChapterAsync(TimePlayer.Core.Models.Chapters.Previous(Chapters, CurrentTime), null);

    /// <summary>Enter in the chapter list.</summary>
    [RelayCommand]
    public Task PlayChapterAsync(Chapter? chapter) => GoToChapterAsync(chapter ?? SelectedChapter, null);

    private async Task GoToChapterAsync(Chapter? chapter, string? whenNone)
    {
        if (Chapters.Count == 0)
        {
            _accessibility.Announce(T("This video has no chapters."), AnnouncementPriority.Medium);
            return;
        }
        if (chapter is null)
        {
            if (whenNone is not null)
                _accessibility.Announce(whenNone, AnnouncementPriority.Medium);
            return;
        }
        if (!IsEngineInitialized)
            return;
        _lastChapter = chapter; // spoken here, not again by the position update
        _accessibility.Announce(T("Chapter {0} of {1}: {2}.", chapter.Number, Chapters.Count, chapter.Title), AnnouncementPriority.High);
        CurrentTime = chapter.Start;
        UpdateBrailleDisplayLine();
        try
        {
            await _playerEngine.SeekAsync(chapter.Start);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not go to chapter {Number}.", chapter.Number);
        }
    }

    /// <summary>A braille routing key on a chapter mark goes to that chapter's start.</summary>
    private TimeSpan SnapToChapter(int cellIndex, int cellCount, TimeSpan duration, TimeSpan target)
    {
        if (cellCount < 2 || duration <= TimeSpan.Zero)
            return target;
        foreach (var start in _chapterStarts)
        {
            if (start > TimeSpan.Zero && (int)Math.Round(start / duration * (cellCount - 1), MidpointRounding.AwayFromZero) == cellIndex)
                return start;
        }
        return target;
    }

    /// <summary>Chapter starts as fractions of the video, for the NVDA add-on's state file.</summary>
    private string ChapterFractions(TimeSpan duration) =>
        duration <= TimeSpan.Zero || _chapterStarts.Count == 0
            ? string.Empty
            : string.Join(",", _chapterStarts.Where(s => s > TimeSpan.Zero)
                .Select(s => (s / duration).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));

    // ----- Transcript -----

    public ObservableCollection<TranscriptLine> TranscriptLines { get; } = new();
    [ObservableProperty] private TranscriptLine? _selectedTranscriptLine;
    [ObservableProperty] private string _transcriptStatus = string.Empty;

    private string? _transcriptFor;

    private void ResetTranscript()
    {
        TranscriptLines.Clear();
        SelectedTranscriptLine = null;
        TranscriptStatus = T("Open this tab to load the transcript.");
        _transcriptFor = null;
    }

    /// <summary>Loads the transcript the first time its tab is opened for a video.</summary>
    private async Task LoadTranscriptAsync()
    {
        var videoId = CurrentVideo?.Id.Value;
        if (videoId is null || _transcriptFor == videoId || _transcripts is null)
            return;
        _transcriptFor = videoId;

        var track = ChooseTranscriptTrack();
        if (track is null)
        {
            TranscriptStatus = T("This video has no captions, so there is no transcript.");
            _accessibility.Announce(TranscriptStatus, AnnouncementPriority.Medium);
            return;
        }

        TranscriptStatus = T("Loading the transcript...");
        var result = await _transcripts.LoadAsync(track);
        if (CurrentVideo?.Id.Value != videoId)
            return;
        if (!result.IsSuccess)
        {
            _transcriptFor = null; // try again next time
            TranscriptStatus = result.Error?.Message ?? T("The captions could not be loaded.");
            _accessibility.Announce(TranscriptStatus, AnnouncementPriority.Medium);
            return;
        }

        TranscriptLines.Clear();
        foreach (var line in result.Value)
            TranscriptLines.Add(line);
        // Start where the video is, so Down reads on from here.
        SelectedTranscriptLine = result.Value.LastOrDefault(l => l.Start <= CurrentTime) ?? TranscriptLines.FirstOrDefault();
        TranscriptStatus = string.Empty;
        _accessibility.Announce(T("Transcript, {0}.", Plural(TranscriptLines.Count, "{0} line", "{0} lines")), AnnouncementPriority.Medium);
        RequestFocusTranscript?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? RequestFocusTranscript;

    /// <summary>The captions chosen in the player, or the best match for the language TimePlayer speaks.</summary>
    private CaptionTrack? ChooseTranscriptTrack()
    {
        if (SelectedSubtitleTrack?.Track is { } chosen)
            return chosen;
        var tracks = (_playbackCoordinator as PlaybackCoordinator)?.CurrentCaptions ?? Array.Empty<CaptionTrack>();
        bool Speaks(CaptionTrack t, string lang) => t.LanguageCode.StartsWith(lang, StringComparison.OrdinalIgnoreCase);
        return tracks.FirstOrDefault(t => Speaks(t, Current) && !t.IsAutoGenerated)
            ?? tracks.FirstOrDefault(t => Speaks(t, "en") && !t.IsAutoGenerated)
            ?? tracks.FirstOrDefault(t => !t.IsAutoGenerated)
            ?? tracks.FirstOrDefault(t => Speaks(t, Current))
            ?? tracks.FirstOrDefault();
    }

    /// <summary>Enter on a transcript line: play from there.</summary>
    [RelayCommand]
    public async Task PlayFromTranscriptLineAsync(TranscriptLine? line)
    {
        line ??= SelectedTranscriptLine;
        if (line is null || !IsEngineInitialized)
            return;
        CurrentTime = line.Start;
        UpdateBrailleDisplayLine();
        _accessibility.Announce(T("Playing from {0}.", AccessibleTextFormatter.FormatTimeSpoken(line.Start)), AnnouncementPriority.Medium);
        try
        {
            await _playerEngine.SeekAsync(line.Start);
            if (PlaybackState == Core.Models.PlaybackState.Paused)
                await _playbackCoordinator.TogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not play from the transcript line.");
        }
    }
}
