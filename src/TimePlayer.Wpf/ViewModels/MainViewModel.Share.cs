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

using static TimePlayer.Core.Localization.Loc;

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
        _ => T("Downloading {0}", Plural(ActiveDownloads, "{0} file", "{0} files"))
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
            _accessibility.Announce(T("Choose a video to download."), AnnouncementPriority.Medium);
            return;
        }
        if (item.IsLive)
        {
            _accessibility.Announce(T("Live streams cannot be downloaded."), AnnouncementPriority.Medium);
            return;
        }

        var title = string.IsNullOrWhiteSpace(item.Title) ? "video" : item.Title;
        var format = audioOnly
            ? (_downloads.CanConvert ? "audio, MP3" : "audio, M4A")
            : "video";
        _accessibility.Announce(T("Downloading {0}: {1}", format, title), AnnouncementPriority.Medium);

        ActiveDownloads++;
        int lastMilestone = 0;
        // Only the halfway point is spoken; more would talk over the video.
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Percent >= 50 && lastMilestone < 50 && p.Percent < 100)
            {
                lastMilestone = 50;
                _accessibility.Announce(T("Download halfway: {0}", title), AnnouncementPriority.Low);
            }
        });

        try
        {
            var result = await _downloads.DownloadAsync(item.Id, audioOnly, progress, _downloadCts.Token);
            if (result.IsSuccess)
            {
                _lastDownloadedFile = result.Value;
                _accessibility.Announce(T("Downloaded: {0}. Saved in {1}.", title, ShortFolderName()), AnnouncementPriority.Medium);
            }
            else if (result.Error?.Code == "DOWNLOAD_CANCELED")
            {
                _accessibility.Announce(T("Download canceled: {0}", title), AnnouncementPriority.Medium);
            }
            else
            {
                _accessibility.Announce(T("Download failed: {0}. {1}", title, result.Error?.Message), AnnouncementPriority.High);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download failed for {Id}.", item.Id);
            _accessibility.Announce(T("Download failed: {0}.", title), AnnouncementPriority.High);
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
            _accessibility.Announce(T("No downloads are running."), AnnouncementPriority.Low);
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

    [ObservableProperty]
    private bool _announceRunCount = true;

    [ObservableProperty]
    private bool _shareRunCount;

    partial void OnAnnounceRunCountChanged(bool value)
    {
        var current = _configService.CurrentConfig;
        if (current.Runs.Announce != value)
            _ = _configService.UpdateConfigAsync(current with { Runs = current.Runs with { Announce = value } });
    }

    partial void OnShareRunCountChanged(bool value)
    {
        var current = _configService.CurrentConfig;
        if (current.Runs.Share != value)
            _ = _configService.UpdateConfigAsync(current with { Runs = current.Runs with { Share = value } });
    }

    [ObservableProperty]
    private bool _openLinksFromOtherApps = true;

    partial void OnOpenLinksFromOtherAppsChanged(bool value)
    {
        try
        {
            LinkProtocol.Apply(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Could not change the timeplayer:// link registration.");
        }
        var current = _configService.CurrentConfig;
        if (current.OpenLinksFromOtherApps != value)
            _ = _configService.UpdateConfigAsync(current with { OpenLinksFromOtherApps = value });
    }

    /// <summary>A bookmark that sends the YouTube page you're on to TimePlayer.</summary>
    public const string BrowserBookmark =
        "javascript:location.href='timeplayer://open?url='+encodeURIComponent(location.href)";

    [RelayCommand]
    public void CopyBrowserBookmark()
    {
        try
        {
            Clipboard.SetText(BrowserBookmark);
            _accessibility.Announce(T("Bookmark copied. In your browser, add a new bookmark named Play in TimePlayer and paste this as its address. On a YouTube page, choose that bookmark to play the page in TimePlayer."), AnnouncementPriority.High);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            _logger.LogWarning(ex, "Could not copy the bookmark.");
            _accessibility.Announce(T("Could not copy to the clipboard. Try again."), AnnouncementPriority.High);
        }
    }

    [RelayCommand]
    public void OpenBrowserExtensionFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "browser-extension");
        if (!Directory.Exists(folder))
        {
            _accessibility.Announce(T("The browser extension isn't included in this copy of TimePlayer. It's on the TimePlayer releases page."), AnnouncementPriority.High);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the browser extension folder.");
        }
    }

    public string VersionText => T("TimePlayer {0}", AppPaths.Version) + (AppPaths.IsPortable ? T(", portable") : string.Empty);

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
            _accessibility.Announce(T("Could not open {0}.", _downloads.DownloadFolder), AnnouncementPriority.Medium);
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
        if (item.Kind == SearchResultKind.Channel)
        {
            if (CopyToClipboard(ShareLink(item)))
                _accessibility.Announce(T("Channel link copied."), AnnouncementPriority.Medium);
            return;
        }
        ShareVideoLink(ShareLink(item), T("Share link copied."), T("TimePlayer link copied."));
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
            ShareVideoLink($"https://www.youtube.com/playlist?list={ytId}", T("Playlist link copied."), T("TimePlayer playlist link copied."));
            return;
        }
        if (playlist is null || playlist.Videos.Count == 0)
        {
            _accessibility.Announce(T("This playlist is empty; there is nothing to share."), AnnouncementPriority.Medium);
            return;
        }
        var ids = string.Join(",", playlist.Videos.Take(50).Select(v => v.VideoId));
        if (CopyToClipboard($"https://www.youtube.com/watch_videos?video_ids={ids}"))
        {
            _accessibility.Announce(playlist.Videos.Count > 50
                ? T("Link copied with the first 50 videos; YouTube allows no more in a shared list.")
                : T("Playlist link copied."), AnnouncementPriority.Medium);
        }
    }

    /// <summary>In the player: a link that starts where you are now.</summary>
    [RelayCommand]
    public void CopyShareLinkAtCurrentTime()
    {
        if (CurrentVideoForSharing() is not { } video) return;
        var at = CurrentTime >= TimeSpan.FromSeconds(1) ? CurrentTime : (TimeSpan?)null;
        if (at is { } t)
        {
            var spoken = AccessibleTextFormatter.FormatTimeSpoken(t);
            ShareVideoLink(ShareLink(video, at), T("Share link copied, starts at {0}.", spoken), T("TimePlayer link copied, starts at {0}.", spoken));
        }
        else
        {
            ShareVideoLink(ShareLink(video), T("Share link copied."), T("TimePlayer link copied."));
        }
    }

    [RelayCommand]
    public void CopyShareLinkCurrent()
    {
        if (CurrentVideoForSharing() is not { } video) return;
        ShareVideoLink(ShareLink(video), T("Share link copied, from the beginning."), T("TimePlayer link copied, from the beginning."));
    }

    // --- YouTube or TimePlayer link ---

    public const string ShareAsk = "ask";
    public const string ShareYouTube = "youtube";
    public const string ShareTimePlayer = "timeplayer";

    public IReadOnlyList<ChoiceOption<string>> ShareLinkChoices { get; } = new[]
    {
        new ChoiceOption<string>(ShareAsk, T("Ask each time")),
        new ChoiceOption<string>(ShareYouTube, T("YouTube link")),
        new ChoiceOption<string>(ShareTimePlayer, T("TimePlayer link")),
    };

    [ObservableProperty]
    private string _shareLinkKind = ShareAsk;

    partial void OnShareLinkKindChanged(string value)
    {
        var current = _configService.CurrentConfig;
        if (current.ShareLinks != value)
            _ = _configService.UpdateConfigAsync(current with { ShareLinks = value });
    }

    /// <summary>Raised when the person should choose between a YouTube and a TimePlayer link.</summary>
    public event EventHandler<ShareLinkRequest>? RequestShareChoice;

    /// <summary>
    /// Shares a video or playlist link: as it is, as a timeplayer:// link that opens straight in
    /// TimePlayer (for people who have it), or after asking which, as set in Settings.
    /// </summary>
    private void ShareVideoLink(string youTubeLink, string youTubeMessage, string timePlayerMessage)
    {
        var request = new ShareLinkRequest(youTubeLink, youTubeMessage, timePlayerMessage);
        switch (ShareLinkKind)
        {
            case ShareYouTube:
                CopyChosenLink(request, timePlayer: false);
                break;
            case ShareTimePlayer:
                CopyChosenLink(request, timePlayer: true);
                break;
            default:
                if (RequestShareChoice is { } ask)
                    ask(this, request);
                else
                    CopyChosenLink(request, timePlayer: false);
                break;
        }
    }

    public void CopyChosenLink(ShareLinkRequest request, bool timePlayer)
    {
        var link = timePlayer ? IncomingLink.ToProtocolLink(request.YouTubeLink) : request.YouTubeLink;
        if (CopyToClipboard(link))
            _accessibility.Announce(timePlayer ? request.TimePlayerMessage : request.YouTubeMessage, AnnouncementPriority.Medium);
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
            _accessibility.Announce(T("Could not copy; the clipboard is busy. Try again."), AnnouncementPriority.Medium);
            return false;
        }
    }
}

/// <summary>A link waiting for the person to choose YouTube or TimePlayer, with what to say for each.</summary>
public sealed record ShareLinkRequest(string YouTubeLink, string YouTubeMessage, string TimePlayerMessage);
