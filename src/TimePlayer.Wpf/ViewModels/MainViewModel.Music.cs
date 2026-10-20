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
/// Radio (songs like this one, without end) and lyrics (Alt+L), read aloud as they're sung when timed (L).
/// </summary>
public sealed partial class MainViewModel
{
    private readonly ILyricsService? _lyrics;

    // ----- Radio -----

    // The songs a radio queued, so it carries on only while the radio is what's playing.
    private readonly HashSet<string> _radioSongs = new();
    private bool _radioRefilling;

    /// <summary>R in the player, or Start radio on a song: songs like it, and more as they run out.</summary>
    [RelayCommand]
    public async Task StartRadioAsync(SearchResult? item)
    {
        var seed = item ?? CurrentLibraryVideoForActions();
        if (seed is null || !seed.IsVideo)
        {
            _accessibility.Announce(T("Choose a song first."), AnnouncementPriority.Medium);
            return;
        }
        if (_innerTubeDirect is null || _playbackCoordinator is not PlaybackCoordinator coordinator)
            return;

        _accessibility.Announce(T("Starting a radio from {0}...", seed.Title), AnnouncementPriority.Medium);
        var radio = await _innerTubeDirect.GetRadioAsync(seed.Id);
        if (!radio.IsSuccess)
        {
            _accessibility.Announce(radio.Error?.Message ?? T("YouTube has no radio for this song."), AnnouncementPriority.High);
            return;
        }

        var playingSeed = item is null && CurrentVideo?.Id.Value == seed.Id;
        var songs = radio.Value.Where(s => s.IsVideo && !(playingSeed && s.Id == seed.Id)).Select(s => s with { StatusPrefix = null }).ToList();
        _radioSongs.Clear();
        _radioSongs.Add(seed.Id);
        foreach (var song in songs)
            _radioSongs.Add(song.Id);

        await coordinator.ReplaceQueueAsync(songs);
        _accessibility.Announce(T("Radio: {0} queued, and more follow.", Plural(songs.Count, "{0} song", "{0} songs")), AnnouncementPriority.Medium);
        // From the player, the song playing goes on and the radio follows it; from a list, the radio starts now.
        if (!playingSeed && songs.Count > 0)
            await PlaySearchResultAsync(songs[0]);
    }

    /// <summary>The queue ran out: a radio that is playing fetches more songs like the last one.</summary>
    private bool ContinueRadio()
    {
        var lastId = CurrentVideo?.Id.Value;
        if (lastId is null || !_radioSongs.Contains(lastId) || _innerTubeDirect is null || _radioRefilling)
            return false;
        _ = RefillRadioAsync(lastId);
        return true;
    }

    private async Task RefillRadioAsync(string lastId)
    {
        _radioRefilling = true;
        try
        {
            var more = await _innerTubeDirect!.GetRadioAsync(lastId);
            var fresh = more.IsSuccess ? more.Value.Where(s => s.IsVideo && _radioSongs.Add(s.Id)).Select(s => s with { StatusPrefix = null }).ToList() : new();
            if (fresh.Count == 0 || _playbackCoordinator is not PlaybackCoordinator coordinator)
            {
                _accessibility.Announce(T("The radio has run out of songs."), AnnouncementPriority.Medium);
                return;
            }
            await coordinator.ReplaceQueueAsync(fresh);
            await PlaySearchResultAsync(fresh[0]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The radio could not get more songs.");
        }
        finally
        {
            _radioRefilling = false;
        }
    }

    // ----- Lyrics -----

    public ObservableCollection<LyricLine> LyricLines { get; } = new();
    [ObservableProperty] private LyricLine? _selectedLyricLine;
    [ObservableProperty] private string _lyricsStatus = string.Empty;
    [ObservableProperty] private bool _speakLyrics;

    private string? _lyricsFor;
    private bool _lyricsLoading;
    // A lookup that failed for a network reason is tried again only when the Lyrics tab is opened,
    // never from position updates (four a second).
    private bool _lyricsFailed;
    private LyricLine? _lastSungLine;

    private void InitializeLyrics()
    {
        _speakLyrics = _configService.CurrentConfig.Accessibility.SpeakLyrics;
        ResetLyrics();
    }

    private void ResetLyrics()
    {
        LyricLines.Clear();
        SelectedLyricLine = null;
        LyricsStatus = T("Open this tab to look up the lyrics.");
        _lyricsFor = null;
        _lyricsFailed = false;
        _lastSungLine = null;
    }

    partial void OnSpeakLyricsChanged(bool value)
    {
        var current = _configService.CurrentConfig;
        if (current.Accessibility.SpeakLyrics != value)
            _ = _configService.UpdateConfigAsync(current with { Accessibility = current.Accessibility with { SpeakLyrics = value } });
    }

    /// <summary>L in the player: read the lyrics aloud as they're sung, or stop.</summary>
    [RelayCommand]
    public async Task ToggleSpeakLyricsAsync()
    {
        SpeakLyrics = !SpeakLyrics;
        if (!SpeakLyrics)
        {
            _accessibility.Announce(T("Lyrics will not be read aloud."), AnnouncementPriority.High);
            return;
        }
        _accessibility.Announce(T("Lyrics will be read aloud as they're sung."), AnnouncementPriority.High);
        await LoadLyricsAsync(announce: false);
        if (LyricLines.Count > 0 && LyricLines.Any(l => l.Start is null))
            _accessibility.Announce(T("These lyrics have no timing, so they can't be read along; Alt+L shows them."), AnnouncementPriority.Medium);
    }

    /// <summary>Looks the song's lyrics up once per song: when the tab opens, or for reading along.</summary>
    private async Task LoadLyricsAsync(bool announce)
    {
        var video = CurrentVideo;
        if (video is null || _lyrics is null || _lyricsLoading)
            return;
        if (_lyricsFor == video.Id.Value && !(announce && _lyricsFailed))
            return;
        _lyricsFailed = false;
        _lyricsLoading = true;
        _lyricsFor = video.Id.Value;
        LyricsStatus = T("Looking up the lyrics...");
        try
        {
            var duration = Duration > TimeSpan.Zero ? Duration : video.Duration;
            var result = await _lyrics.FindAsync(video.Title, video.Channel.Name, duration);
            if (CurrentVideo?.Id.Value != video.Id.Value)
                return;
            if (!result.IsSuccess)
            {
                _lyricsFailed = result.Error?.Code != "NO_LYRICS"; // a network problem: the tab can try again
                LyricsStatus = result.Error?.Message ?? T("No lyrics were found for this song.");
                if (announce)
                    _accessibility.Announce(LyricsStatus, AnnouncementPriority.Medium);
                return;
            }
            LyricLines.Clear();
            foreach (var line in result.Value.Lines)
                LyricLines.Add(line);
            SelectedLyricLine = result.Value.Lines.LastOrDefault(l => l.Start <= CurrentTime) ?? LyricLines.FirstOrDefault();
            LyricsStatus = string.Empty;
            if (announce)
                _accessibility.Announce(T("Lyrics: {0} by {1}.", result.Value.Track, result.Value.Artist), AnnouncementPriority.Medium);
            RequestFocusLyrics?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lyricsLoading = false;
        }
    }

    public event EventHandler? RequestFocusLyrics;

    /// <summary>Each position update: the line being sung, read aloud when asked for.</summary>
    private void OnLyricsPosition(TimeSpan position)
    {
        if (!SpeakLyrics || PlaybackState != PlaybackState.Playing)
            return;
        if (LyricLines.Count == 0)
        {
            if (CurrentVideo is { } video && _lyricsFor != video.Id.Value)
                _ = LoadLyricsAsync(announce: false);
            return;
        }
        var line = LyricLines.LastOrDefault(l => l.Start is { } start && start <= position + TimeSpan.FromMilliseconds(150));
        if (line is null || line == _lastSungLine)
            return;
        _lastSungLine = line;
        // Like captions: a new line interrupts the old one, so speech keeps up with the song.
        _accessibility.AnnounceCaption(line.Text);
    }

    /// <summary>Enter on a timed lyric line: play from there.</summary>
    [RelayCommand]
    public async Task PlayFromLyricLineAsync(LyricLine? line)
    {
        line ??= SelectedLyricLine;
        if (line?.Start is not { } start || !IsEngineInitialized)
            return;
        CurrentTime = start;
        _lastSungLine = line;
        UpdateBrailleDisplayLine();
        try
        {
            await _playerEngine.SeekAsync(start);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not play from the lyric line.");
        }
    }
}
