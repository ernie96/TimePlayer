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

    public Task<Result<string>> DownloadAsync(string videoId, bool audioOnly, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!YouTubeVideoId.TryParse(videoId, out var id))
            return Task.FromResult(Result<string>.Failure("INVALID_ID", "This is not a YouTube video."));

        var options = new DownloadOptions
        {
            OutputDirectory = DownloadFolder,
            AudioOnly = audioOnly,
            QualityPreference = _config.CurrentConfig.Playback.PreferredQuality is QualityPreference.AudioOnly
                ? QualityPreference.Auto
                : _config.CurrentConfig.Playback.PreferredQuality,
            FfmpegLocation = _ffmpeg.Value,
            FileNamePattern = "%(title)s.%(ext)s"
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
