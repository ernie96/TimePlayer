using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Library;

public interface IDownloadService
{
    /// <summary>Where downloads are saved (created on first download).</summary>
    string DownloadFolder { get; }

    /// <summary>True when ffmpeg is available, so audio can become MP3 and video can be full quality.</summary>
    bool CanConvert { get; }

    Task<Result<string>> DownloadAsync(string videoId, bool audioOnly, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// One video of a whole list: saved in a folder named after the list, as "003 - Title" so the files
    /// keep the list's order.
    /// </summary>
    Task<Result<string>> DownloadIntoFolderAsync(string videoId, bool audioOnly, string folderName, int number,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken) =>
        DownloadAsync(videoId, audioOnly, progress, cancellationToken);
}

/// <summary>Saves videos or their audio with yt-dlp, using ffmpeg when it is installed.</summary>
public sealed class DownloadService : IDownloadService
{
    private readonly YtDlpBackendProvider _ytDlp;
    private readonly IConfigurationService _config;
    private readonly Lazy<string?> _ffmpeg = new(FindFfmpegFolder);

    public DownloadService(YtDlpBackendProvider ytDlp, IConfigurationService config)
    {
        _ytDlp = ytDlp;
        _config = config;
    }

    public string DownloadFolder =>
        string.IsNullOrWhiteSpace(_config.CurrentConfig.Backend.YtDlpDownloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TimePlayer")
            : _config.CurrentConfig.Backend.YtDlpDownloadDirectory!;

    public bool CanConvert => _ffmpeg.Value is not null;

    public Task<Result<string>> DownloadAsync(string videoId, bool audioOnly, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken) =>
        Download(videoId, audioOnly, DownloadFolder, "%(title)s.%(ext)s", progress, cancellationToken);

    public Task<Result<string>> DownloadIntoFolderAsync(string videoId, bool audioOnly, string folderName, int number,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken) =>
        Download(videoId, audioOnly, Path.Combine(DownloadFolder, SafeFolderName(folderName)), $"{number:D3} - %(title)s.%(ext)s", progress, cancellationToken);

    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>A playlist or channel name as a folder name Windows accepts.</summary>
    public static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (safe.Length > 80)
            safe = safe[..80].TrimEnd('.', ' ');
        if (safe.Length == 0)
            return "TimePlayer list";
        // Names Windows keeps for devices (a playlist called "Con" or "Aux").
        var stem = safe.Split('.')[0];
        return ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase) ? "_" + safe : safe;
    }

    private Task<Result<string>> Download(string videoId, bool audioOnly, string folder, string pattern,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!YouTubeVideoId.TryParse(videoId, out var id))
            return Task.FromResult(Result<string>.Failure("INVALID_ID", "This is not a YouTube video."));

        var options = new DownloadOptions
        {
            OutputDirectory = folder,
            AudioOnly = audioOnly,
            QualityPreference = _config.CurrentConfig.Playback.PreferredQuality is QualityPreference.AudioOnly
                ? QualityPreference.Auto
                : _config.CurrentConfig.Playback.PreferredQuality,
            FfmpegLocation = _ffmpeg.Value,
            FileNamePattern = pattern
        };
        return _ytDlp.DownloadAsync(id, options, progress, cancellationToken);
    }

    /// <summary>ffmpeg next to TimePlayer, in TimePlayer's tools folder, or anywhere on PATH.</summary>
    internal static string? FindFfmpegFolder()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "bin"),
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimePlayer", "bin")
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return candidates.FirstOrDefault(dir =>
        {
            try { return File.Exists(Path.Combine(dir, "ffmpeg.exe")); }
            catch (ArgumentException) { return false; }
        });
    }
}
