using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>Downloads (audio or video) and share links.</summary>
public sealed partial class MainViewModel
{
    private readonly IDownloadService _downloads;
    private CancellationTokenSource _downloadCts = new();
    private string? _lastDownloadedFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveDownloads), nameof(DownloadStatusText))]
    private int _activeDownloads;

    public bool HasActiveDownloads => ActiveDownloads > 0;
    public string DownloadStatusText => ActiveDownloads switch
    {
        0 => string.Empty,
        1 => "Downloading 1 file",
        _ => $"Downloading {ActiveDownloads} files"
    };

    public string DownloadFolder => _downloads.DownloadFolder;
    public bool CanConvertDownloads => _downloads.CanConvert;

    // --- Downloads ---

    [RelayCommand]
    public Task DownloadAudioForItemAsync(SearchResult? item) => DownloadAsync(item ?? SelectedSearchResult, audioOnly: true);

    [RelayCommand]
    public Task DownloadVideoForItemAsync(SearchResult? item) => DownloadAsync(item ?? SelectedSearchResult, audioOnly: false);

    [RelayCommand]
    public Task DownloadAudioCurrentAsync() => DownloadAsync(CurrentVideoForSharing(), audioOnly: true);

    [RelayCommand]
    public Task DownloadVideoCurrentAsync() => DownloadAsync(CurrentVideoForSharing(), audioOnly: false);

    private SearchResult? CurrentVideoForSharing() =>
        CurrentVideo is { } details && details.Id.Value == _currentLibraryVideo?.Id ? details.ToSearchResult() : _currentLibraryVideo;

    public async Task DownloadAsync(SearchResult? item, bool audioOnly)
    {
        if (item is null || item.Kind != SearchResultKind.Video)
        {
            _accessibility.Announce("Choose a video to download.", AnnouncementPriority.Medium);
            return;
        }
        if (item.IsLive)
        {
            _accessibility.Announce("Live streams cannot be downloaded.", AnnouncementPriority.Medium);
            return;
        }

        var title = string.IsNullOrWhiteSpace(item.Title) ? "video" : item.Title;
        var format = audioOnly
            ? (_downloads.CanConvert ? "audio, MP3" : "audio, M4A")
            : "video";
        _accessibility.Announce($"Downloading {format}: {title}", AnnouncementPriority.Medium);

        ActiveDownloads++;
        int lastMilestone = 0;
        // Only the halfway point is spoken; more would talk over the video.
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Percent >= 50 && lastMilestone < 50 && p.Percent < 100)
            {
                lastMilestone = 50;
                _accessibility.Announce($"Download halfway: {title}", AnnouncementPriority.Low);
            }
        });

        try
        {
            var result = await _downloads.DownloadAsync(item.Id, audioOnly, progress, _downloadCts.Token);
            if (result.IsSuccess)
            {
                _lastDownloadedFile = result.Value;
                _accessibility.Announce($"Downloaded: {title}. Saved in {ShortFolderName()}.", AnnouncementPriority.Medium);
            }
            else if (result.Error?.Code == "DOWNLOAD_CANCELED")
            {
                _accessibility.Announce($"Download canceled: {title}", AnnouncementPriority.Medium);
            }
            else
            {
                _accessibility.Announce($"Download failed: {title}. {result.Error?.Message}", AnnouncementPriority.High);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download failed for {Id}.", item.Id);
            _accessibility.Announce($"Download failed: {title}.", AnnouncementPriority.High);
        }
        finally
        {
            ActiveDownloads--;
        }
    }

    private string ShortFolderName()
    {
        var folder = _downloads.DownloadFolder.TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetFileName(Path.GetDirectoryName(folder) ?? string.Empty);
        var name = Path.GetFileName(folder);
        return string.IsNullOrEmpty(parent) ? name : $"{parent}, {name}";
    }

    [RelayCommand]
    public void CancelDownloads()
    {
        if (!HasActiveDownloads)
        {
            _accessibility.Announce("No downloads are running.", AnnouncementPriority.Low);
            return;
        }
        _downloadCts.Cancel();
        _downloadCts.Dispose();
        _downloadCts = new CancellationTokenSource();
    }

    [ObservableProperty]
    private bool _checkForAppUpdatesAtStartup = true;

    partial void OnCheckForAppUpdatesAtStartupChanged(bool value)
    {
        var current = _configService.CurrentConfig;
        if (current.Update.CheckAtStartup != value)
            _ = _configService.UpdateConfigAsync(current with { Update = current.Update with { CheckAtStartup = value } });
    }

    public string VersionText => $"TimePlayer {AppPaths.Version}" + (AppPaths.IsPortable ? ", portable" : string.Empty);

    [RelayCommand]
    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the log folder.");
        }
    }

    [RelayCommand]
    public void OpenDownloadsFolder()
    {
        try
        {
            Directory.CreateDirectory(_downloads.DownloadFolder);
            // Select the newest download when there is one, so it is the first thing read in Explorer.
            var arguments = _lastDownloadedFile is { } file && File.Exists(file)
                ? $"/select,\"{file}\""
                : $"\"{_downloads.DownloadFolder}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the downloads folder.");
            _accessibility.Announce($"Could not open {_downloads.DownloadFolder}.", AnnouncementPriority.Medium);
        }
    }

    // --- Share links ---

    public static string ShareLink(SearchResult item, TimeSpan? startAt = null)
    {
        if (item.Kind == SearchResultKind.Channel)
            return $"https://www.youtube.com/channel/{item.Id}";
        if (item.Kind == SearchResultKind.Playlist)
            return $"https://www.youtube.com/playlist?list={item.Id}";
        var link = $"https://youtu.be/{item.Id}";
        return startAt is { TotalSeconds: >= 1 } t ? $"{link}?t={(int)t.TotalSeconds}" : link;
    }

    /// <summary>Copies the short youtu.be link, like YouTube's Share button.</summary>
    [RelayCommand]
    public void CopyLink(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        if (item is null) return;
        if (item.Kind == SearchResultKind.Playlist)
        {
            CopyPlaylistLink(item.LibraryPlaylistId, item.Id);
            return;
        }
        if (CopyToClipboard(ShareLink(item)))
            _accessibility.Announce(item.Kind == SearchResultKind.Channel ? "Channel link copied." : "Share link copied.", AnnouncementPriority.Medium);
    }

    /// <summary>The open playlist's link (toolbar button on playlist pages).</summary>
    [RelayCommand]
    public void CopyOpenPlaylistLink() => CopyPlaylistLink(_openLibraryPlaylistId, _openYouTubePlaylistId);

    /// <summary>
    /// YouTube playlists share their own link. Your own playlists have none on YouTube, so the link
    /// is YouTube's "watch_videos" list of up to 50 videos, which opens as an unnamed playlist.
    /// </summary>
    private void CopyPlaylistLink(string? libraryId, string? youTubeId)
    {
        var playlist = libraryId is null ? null : _library.GetPlaylist(libraryId);
        var ytId = playlist?.YouTubeId ?? (playlist is null ? youTubeId : null);
        if (ytId is not null)
        {
            if (CopyToClipboard($"https://www.youtube.com/playlist?list={ytId}"))
                _accessibility.Announce("Playlist link copied.", AnnouncementPriority.Medium);
            return;
        }
        if (playlist is null || playlist.Videos.Count == 0)
        {
            _accessibility.Announce("This playlist is empty; there is nothing to share.", AnnouncementPriority.Medium);
            return;
        }
        var ids = string.Join(",", playlist.Videos.Take(50).Select(v => v.VideoId));
        if (CopyToClipboard($"https://www.youtube.com/watch_videos?video_ids={ids}"))
        {
            _accessibility.Announce(playlist.Videos.Count > 50
                ? "Link copied with the first 50 videos; YouTube allows no more in a shared list."
                : "Playlist link copied.", AnnouncementPriority.Medium);
        }
    }

    /// <summary>In the player: a link that starts where you are now.</summary>
    [RelayCommand]
    public void CopyShareLinkAtCurrentTime()
    {
        if (CurrentVideoForSharing() is not { } video) return;
        var at = CurrentTime >= TimeSpan.FromSeconds(1) ? CurrentTime : (TimeSpan?)null;
        if (CopyToClipboard(ShareLink(video, at)))
        {
            _accessibility.Announce(at is { } t
                ? $"Share link copied, starts at {AccessibleTextFormatter.FormatTimeSpoken(t)}."
                : "Share link copied.", AnnouncementPriority.Medium);
        }
    }

    [RelayCommand]
    public void CopyShareLinkCurrent()
    {
        if (CurrentVideoForSharing() is not { } video) return;
        if (CopyToClipboard(ShareLink(video)))
            _accessibility.Announce("Share link copied, from the beginning.", AnnouncementPriority.Medium);
    }

    private bool CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy to the clipboard.");
            _accessibility.Announce("Could not copy; the clipboard is busy. Try again.", AnnouncementPriority.Medium);
            return false;
        }
    }
}
