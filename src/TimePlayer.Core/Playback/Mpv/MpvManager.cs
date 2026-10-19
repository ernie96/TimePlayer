using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Common.Process;
using TimePlayer.Core.Configuration;

namespace TimePlayer.Core.Playback.Mpv;

/// <summary>
/// Manages the application-bundled mpv standalone media player binary lifecycle,
/// keeping executables, documentation, and licenses in the managed dependency directory.
/// </summary>
public sealed class MpvManager : IMpvManager
{
    /// <summary>SHA-256 of that archive, as GitHub publishes it; a download that differs is refused.</summary>
    private const string DefaultDownloadSha256 = "2821d9eef879215e99975fae9616e1777f5e3747168edd3a571c41e21b8abbbc";
    private const string DefaultDownloadUrl = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261017/mpv-x86_64-20261017-git-dde0671072.7z";

    private readonly IProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configService;
    private readonly ILogger<MpvManager> _logger;

    public MpvManager(
        IProcessRunner processRunner,
        HttpClient httpClient,
        IConfigurationService configService,
        ILogger<MpvManager> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ExecutablePath
    {
        get
        {
            var customPath = _configService.CurrentConfig.Playback.MpvCustomPath;
            if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            {
                return customPath;
            }

            var baseDir = AppContext.BaseDirectory;
            var appBinMpv = Path.Combine(baseDir, "bin", "mpv", "mpv.exe");
            if (File.Exists(appBinMpv)) return appBinMpv;

            var appRootMpv = Path.Combine(baseDir, "mpv.exe");
            if (File.Exists(appRootMpv)) return appRootMpv;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var standardPath = Path.Combine(localAppData, "TimePlayer", "bin", "mpv", "mpv.exe");
            if (File.Exists(standardPath))
            {
                return standardPath;
            }

            var comPath = Path.Combine(localAppData, "TimePlayer", "bin", "mpv", "mpv.com");
            if (File.Exists(comPath))
            {
                return comPath;
            }

            return standardPath;
        }
    }

    public bool IsInstalled => File.Exists(ExecutablePath);

    public async Task<Result<string>> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
        {
            return Result<string>.Success(ExecutablePath);
        }

        _logger.LogInformation("mpv is not installed in managed directory. Beginning download and installation...");

        var targetFile = ExecutablePath;
        var dir = Path.GetDirectoryName(targetFile)!;
        Directory.CreateDirectory(dir);

        var tempArchive = Path.Combine(dir, $"mpv-{Guid.NewGuid():N}.7z");

        try
        {
            _logger.LogInformation("Downloading standalone mpv distribution from {Url}...", DefaultDownloadUrl);
            using (var response = await _httpClient.GetAsync(DefaultDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var fileStream = new FileStream(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fileStream, cancellationToken);
            }

            string actualSha256;
            await using (var check = File.OpenRead(tempArchive))
                actualSha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(check, cancellationToken)).ToLowerInvariant();
            if (actualSha256 != DefaultDownloadSha256)
            {
                _logger.LogError("The mpv download failed its integrity check (got {Actual}).", actualSha256);
                return Result<string>.Failure("MPV_INTEGRITY", "The downloaded mpv didn't match its published checksum, so it was not installed.");
            }

            // Extract archive using 7-Zip or system tar
            var extracted = await ExtractArchiveAsync(tempArchive, dir, cancellationToken);
            if (!extracted)
            {
                return Result<string>.Failure(
                    "MPV_EXTRACTION_FAILED",
                    "Failed to extract the mpv archive using available system archivers (7-Zip or tar).");
            }

            if (!IsInstalled)
            {
                return Result<string>.Failure(
                    "MPV_MISSING_AFTER_EXTRACTION",
                    $"Extraction succeeded but mpv.exe was not found at {targetFile}.");
            }

            _logger.LogInformation("mpv successfully installed at {Path}.", targetFile);
            return Result<string>.Success(targetFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download and install mpv.");
            return Result<string>.Failure("MPV_DOWNLOAD_ERROR", ex.Message, ex.ToString(), ex);
        }
        finally
        {
            if (File.Exists(tempArchive))
            {
                try { File.Delete(tempArchive); } catch { /* ignore cleanup error */ }
            }
        }
    }

    private async Task<bool> ExtractArchiveAsync(string archivePath, string destinationDir, CancellationToken cancellationToken)
    {
        // 1. Try 7-Zip if installed
        var sevenZipPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
        if (File.Exists(sevenZipPath))
        {
            _logger.LogInformation("Extracting mpv using 7-Zip at {Path}...", sevenZipPath);
            var result = await _processRunner.RunAsync(
                sevenZipPath,
                new[] { "x", archivePath, $"-o{destinationDir}", "-y" },
                new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(2) },
                cancellationToken: cancellationToken);

            if (result.IsSuccess)
                return true;
        }

        // 2. Try tar.exe (built-in on Windows 10/11)
        var tarPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (File.Exists(tarPath))
        {
            _logger.LogInformation("Extracting mpv using system tar at {Path}...", tarPath);
            var result = await _processRunner.RunAsync(
                tarPath,
                new[] { "-xf", archivePath, "-C", destinationDir },
                new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(2) },
                cancellationToken: cancellationToken);

            if (result.IsSuccess)
                return true;
        }

        return false;
    }
}
