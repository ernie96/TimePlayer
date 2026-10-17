using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Common.Process;
using TimePlayer.Core.Configuration;

namespace TimePlayer.Core.Backends.YtDlp;

/// <summary>
/// Version tracking metadata persisted to yt-dlp-version.json.
/// </summary>
public record YtDlpVersionMetadata
{
    public string? Version { get; init; }
    public DateTimeOffset LastCheckedUtc { get; init; }
    public string? Sha256 { get; init; }
}

/// <summary>
/// Manages installation, SHA256 integrity verification, and automatic updates of the
/// bundled standalone yt-dlp binary without requiring Python on the host system.
/// </summary>
public sealed class YtDlpManager : IYtDlpManager
{
    private const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string ChecksumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";

    private readonly IProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configService;
    private readonly ILogger<YtDlpManager> _logger;

    public YtDlpManager(
        IProcessRunner processRunner,
        HttpClient httpClient,
        IConfigurationService configService,
        ILogger<YtDlpManager> logger)
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
            var customPath = _configService.CurrentConfig.Backend.YtDlpCustomPath;
            if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            {
                return customPath;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "TimePlayer", "bin", "yt-dlp.exe");
        }
    }

    private string MetadataPath => Path.Combine(Path.GetDirectoryName(ExecutablePath)!, "yt-dlp-version.json");

    public bool IsInstalled => File.Exists(ExecutablePath);

    public async Task<Result<string>> EnsureInstalledOrUpdatedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsInstalled)
            {
                _logger.LogInformation("yt-dlp is not installed. Initiating verified standalone download...");
                var downloadResult = await DownloadAndVerifyAsync(cancellationToken);
                if (!downloadResult.IsSuccess)
                    return Result<string>.Failure(downloadResult.Error!);
            }
            else if (_configService.CurrentConfig.Backend.YtDlpAutoUpdateOnStartup)
            {
                _logger.LogInformation("Checking for yt-dlp updates on startup...");
                await CheckAndUpdateAsync(cancellationToken);
            }

            return await GetVersionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure yt-dlp is installed and updated.");
            return Result<string>.Failure("YTDLP_INIT_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    public async Task<Result<string>> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
            return Result<string>.Failure(AppError.DependencyNotFound("yt-dlp", $"File not found at {ExecutablePath}"));

        try
        {
            var result = await _processRunner.RunAsync(
                ExecutablePath,
                new[] { "--version" },
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(10) },
                cancellationToken: cancellationToken);

            if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var version = result.StandardOutput.Trim();
                return Result<string>.Success(version);
            }

            // Fallback to metadata file if execution failed
            if (File.Exists(MetadataPath))
            {
                var json = await File.ReadAllTextAsync(MetadataPath, cancellationToken);
                var meta = JsonSerializer.Deserialize<YtDlpVersionMetadata>(json);
                if (!string.IsNullOrWhiteSpace(meta?.Version))
                    return Result<string>.Success(meta.Version);
            }

            return Result<string>.Failure("YTDLP_VERSION_UNKNOWN", "Could not query yt-dlp version.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query yt-dlp version.");
            return Result<string>.Failure("YTDLP_VERSION_ERROR", ex.Message, ex.ToString(), ex);
        }
    }

    public async Task<Result<bool>> CheckAndUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
        {
            var installResult = await DownloadAndVerifyAsync(cancellationToken);
            return installResult.IsSuccess
                ? Result<bool>.Success(true)
                : Result<bool>.Failure(installResult.Error!);
        }

        try
        {
            _logger.LogInformation("Running yt-dlp --update...");
            var updateResult = await _processRunner.RunAsync(
                ExecutablePath,
                new[] { "--update" },
                new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(2) },
                cancellationToken: cancellationToken);

            var stdout = updateResult.StandardOutput;
            _logger.LogInformation("yt-dlp update response: {Response}", stdout.Trim());

            var updated = stdout.Contains("Updating to version", StringComparison.OrdinalIgnoreCase) ||
                          stdout.Contains("Updated yt-dlp", StringComparison.OrdinalIgnoreCase);

            var versionResult = await GetVersionAsync(cancellationToken);
            if (versionResult.IsSuccess)
            {
                await SaveMetadataAsync(versionResult.Value!, null, cancellationToken);
            }

            return Result<bool>.Success(updated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp self-update failed, falling back to release verification download.");
            var fallback = await DownloadAndVerifyAsync(cancellationToken);
            return fallback.IsSuccess
                ? Result<bool>.Success(true)
                : Result<bool>.Failure(fallback.Error!);
        }
    }

    private async Task<Result<bool>> DownloadAndVerifyAsync(CancellationToken cancellationToken)
    {
        var targetFile = ExecutablePath;
        var dir = Path.GetDirectoryName(targetFile)!;
        Directory.CreateDirectory(dir);

        var tempFile = Path.Combine(dir, $"yt-dlp.{Guid.NewGuid():N}.tmp");

        try
        {
            _logger.LogInformation("Downloading yt-dlp executable from {Url}...", DownloadUrl);
            using (var response = await _httpClient.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fileStream, cancellationToken);
            }

            _logger.LogInformation("Computing SHA256 checksum of downloaded binary...");
            string computedHash;
            using (var sha256 = SHA256.Create())
            await using (var fileStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var hashBytes = await sha256.ComputeHashAsync(fileStream, cancellationToken);
                computedHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            // Attempt to verify with official SHA2-256SUMS file
            string? expectedHash = null;
            try
            {
                _logger.LogInformation("Fetching official checksums from {Url}...", ChecksumsUrl);
                var sumsContent = await _httpClient.GetStringAsync(ChecksumsUrl, cancellationToken);
                expectedHash = ParseChecksumForFile(sumsContent, "yt-dlp.exe");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download SHA2-256SUMS for verification. Proceeding with caution.");
            }

            if (!string.IsNullOrEmpty(expectedHash))
            {
                if (!string.Equals(computedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Checksum mismatch for downloaded yt-dlp. Expected {Expected}, computed {Computed}", expectedHash, computedHash);
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    return Result<bool>.Failure(
                        "CHECKSUM_MISMATCH",
                        "Downloaded yt-dlp binary failed SHA256 integrity verification.",
                        $"Expected: {expectedHash}, Computed: {computedHash}");
                }

                _logger.LogInformation("SHA256 checksum verified successfully: {Hash}", computedHash);
            }

            // Atomic replacement
            if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
            }
            File.Move(tempFile, targetFile);

            // Fetch and save version
            var versionResult = await GetVersionAsync(cancellationToken);
            var versionStr = versionResult.IsSuccess ? versionResult.Value : "latest";
            await SaveMetadataAsync(versionStr, computedHash, cancellationToken);

            _logger.LogInformation("yt-dlp installed successfully at {Path} (Version {Version}).", targetFile, versionStr);
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { /* ignore cleanup error */ }
            }
            _logger.LogError(ex, "Failed to download and verify yt-dlp.");
            return Result<bool>.Failure("YTDLP_DOWNLOAD_ERROR", ex.Message, ex.ToString(), ex);
        }
    }

    private static string? ParseChecksumForFile(string sumsContent, string fileName)
    {
        var lines = sumsContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var hash = parts[0].Trim();
                var name = parts[1].Trim().TrimStart('*');
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return hash.ToLowerInvariant();
                }
            }
        }
        return null;
    }

    private async Task SaveMetadataAsync(string version, string? sha256, CancellationToken cancellationToken)
    {
        try
        {
            var meta = new YtDlpVersionMetadata
            {
                Version = version,
                LastCheckedUtc = DateTimeOffset.UtcNow,
                Sha256 = sha256
            };
            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(MetadataPath, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist yt-dlp version metadata.");
        }
    }
}
