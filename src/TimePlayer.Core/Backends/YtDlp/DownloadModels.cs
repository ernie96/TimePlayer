using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends.YtDlp;

/// <summary>
/// Configuration options for downloading a YouTube video or audio track via yt-dlp.
/// </summary>
public record DownloadOptions
{
    /// <summary>
    /// Target destination directory on disk where the media will be saved.
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// If true, extracts and saves only the audio stream (e.g. mp3).
    /// </summary>
    public bool AudioOnly { get; init; }

    /// <summary>
    /// Preferred video resolution (e.g. 1080p, 720p). Ignored if AudioOnly is true.
    /// </summary>
    public QualityPreference QualityPreference { get; init; } = QualityPreference.Auto;

    /// <summary>
    /// Output template pattern for yt-dlp filenames. Defaults to "%(title)s [%(id)s].%(ext)s".
    /// </summary>
    public string FileNamePattern { get; init; } = "%(title)s [%(id)s].%(ext)s";

    /// <summary>
    /// Target audio format when AudioOnly is true. Default: "mp3".
    /// </summary>
    public string AudioFormat { get; init; } = "mp3";

    /// <summary>
    /// Optional browser from which to load session cookies (e.g., "chrome", "firefox", "edge").
    /// </summary>
    public string? BrowserCookies { get; init; }

    /// <summary>
    /// Folder containing ffmpeg.exe. Without it, audio is saved in YouTube's own format (m4a)
    /// and video as a single pre-merged file, because converting and merging need ffmpeg.
    /// </summary>
    public string? FfmpegLocation { get; init; }
}

/// <summary>
/// Progress notification reported during yt-dlp media downloads.
/// </summary>
public record DownloadProgress
{
    public double Percent { get; init; }
    public string? TotalSize { get; init; }
    public string? Speed { get; init; }
    public string? Eta { get; init; }
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Screen reader friendly speech representation of the current download status.
    /// </summary>
    public string AccessibleSummary
    {
        get
        {
            if (Percent >= 100)
                return "Download complete.";

            var parts = new List<string> { $"Downloading, {(int)Percent} percent" };

            if (!string.IsNullOrWhiteSpace(Speed))
            {
                parts.Add($"at {Speed}");
            }

            if (!string.IsNullOrWhiteSpace(Eta))
            {
                parts.Add($"ETA {Eta}");
            }

            return string.Join(", ", parts);
        }
    }
}
