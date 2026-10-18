using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;

namespace TimePlayer.Core.Updates;

public enum InstallKind
{
    /// <summary>Installed with TimePlayer_setup.exe (an uninstaller sits next to the exe).</summary>
    Installer,
    /// <summary>Extracted from TimePlayer.zip; updated by replacing the files in place.</summary>
    Portable
}

public sealed record UpdateInfo(Version Version, string ReleaseNotes, string AssetName, string DownloadUrl, string SignatureUrl);

public interface IUpdateService
{
    Version CurrentVersion { get; }
    InstallKind InstallKind { get; }

    /// <summary>The newer release for this install kind, or null when up to date.</summary>
    Task<Result<UpdateInfo?>> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads the update and verifies its signature. Returns the verified file.</summary>
    Task<Result<string>> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a hidden helper that waits for TimePlayer to exit, installs the update and starts
    /// TimePlayer again. The caller must then shut the app down.
    /// </summary>
    Result StartInstall(string verifiedFile);
}

/// <summary>
/// Updates from GitHub Releases, after the ship-shape design: stable channel by version tag,
/// fixed asset names (TimePlayer_setup.exe, TimePlayer.zip) each with an Ed25519 ".sig",
/// and a PowerShell helper that applies the update after the app exits and relaunches it.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    /// <summary>The release signing public key. Its private half is the UPDATE_SIGNING_KEY secret.</summary>
    public const string PublicKey = "e0cquDjbkNHNi/BFw0vG25dCmRpi/GutstuyMoTPCYI=";

    public const string InstallerAssetName = "TimePlayer_setup.exe";
    public const string PortableAssetName = "TimePlayer.zip";

    private readonly HttpClient _http;
    private readonly IConfigurationService _config;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(HttpClient http, IConfigurationService config, ILogger<UpdateService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public Version CurrentVersion => ParseVersion(AppPaths.Version) ?? new Version(0, 0, 0);

    public InstallKind InstallKind =>
        File.Exists(Path.Combine(AppPaths.ProgramFolder, "unins000.exe")) ? InstallKind.Installer : InstallKind.Portable;

    private string Repository => EffectiveRepository(_config.CurrentConfig.Update.Repository);

    /// <summary>
    /// The repository to check. Builds made before TimePlayer's repository existed used the placeholder
    /// "OWNER/TimePlayer", and saving settings in those builds stored it in settings.json, where it would
    /// still override the right value after an update. A placeholder or malformed value means the official one.
    /// </summary>
    internal static string EffectiveRepository(string? configured)
    {
        var value = configured?.Trim() ?? string.Empty;
        var parts = value.Split('/');
        bool wellFormed = parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
        return !wellFormed || parts[0].Equals("OWNER", StringComparison.OrdinalIgnoreCase)
            ? UpdateConfig.OfficialRepository
            : value;
    }

    public async Task<Result<UpdateInfo?>> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var repository = Repository;
            var response = await GetLatestReleaseAsync(repository, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound && repository != UpdateConfig.OfficialRepository)
            {
                // A custom repository that is gone shouldn't cut the user off from official updates.
                _logger.LogWarning("Update repository {Repository} was not found; checking {Official} instead.", repository, UpdateConfig.OfficialRepository);
                response.Dispose();
                repository = UpdateConfig.OfficialRepository;
                response = await GetLatestReleaseAsync(repository, timeout.Token);
            }
            using var _ = response;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Update check of {Repository} got HTTP {Status}.", repository, (int)response.StatusCode);
                return Result<UpdateInfo?>.Failure("UPDATE_HTTP", $"GitHub answered {(int)response.StatusCode} when checking for updates.");
            }

            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (release is null || ParseVersion(release.TagName) is not { } latest)
                return Result<UpdateInfo?>.Failure("UPDATE_BAD_RELEASE", "The latest release has no usable version number.");

            _logger.LogInformation("Update check: running {Current}, latest release {Latest}.", CurrentVersion, latest);
            if (latest <= CurrentVersion)
                return Result<UpdateInfo?>.Success(null);

            return PickAssets(release, latest, InstallKind) is { } info
                ? Result<UpdateInfo?>.Success(info)
                : Result<UpdateInfo?>.Failure("UPDATE_NO_ASSET", $"TimePlayer {latest} is out, but its download for this kind of install is missing.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<UpdateInfo?>.Failure("UPDATE_TIMEOUT", "Checking for updates timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return Result<UpdateInfo?>.Failure("UPDATE_NETWORK", $"Could not check for updates: {ex.Message}");
        }
    }

    private Task<HttpResponseMessage> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TimePlayer", AppPaths.Version));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return _http.SendAsync(request, cancellationToken);
    }

    internal static UpdateInfo? PickAssets(GitHubRelease release, Version version, InstallKind kind)
    {
        var name = kind == InstallKind.Installer ? InstallerAssetName : PortableAssetName;
        var assets = release.Assets ?? new List<GitHubAsset>();
        var file = assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var signature = assets.FirstOrDefault(a => a.Name.Equals(name + ".sig", StringComparison.OrdinalIgnoreCase));
        return file is null || signature is null
            ? null
            : new UpdateInfo(version, release.Body ?? string.Empty, name, file.DownloadUrl, signature.DownloadUrl);
    }

    /// <summary>"v1.2.0", "1.2", "1.2.0-beta" → 1.2.0.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+', ' ']);
        if (cut > 0) core = core[..cut];
        return Version.TryParse(core, out var v)
            ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
            : null;
    }

    public async Task<Result<string>> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "TimePlayerUpdate");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, update.AssetName);
        try
        {
            var signature = await _http.GetStringAsync(update.SignatureUrl, cancellationToken);

            using (var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var file = File.Create(target);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (total > 0) progress?.Report((double)received / total);
                }
            }

            if (!UpdateSignature.Verify(PublicKey, target, update.AssetName, signature))
            {
                File.Delete(target);
                _logger.LogError("Update {Version} failed signature verification; discarded.", update.Version);
                return Result<string>.Failure("UPDATE_BAD_SIGNATURE", "The downloaded update is not signed by TimePlayer's publisher, so it was not installed.");
            }
            _logger.LogInformation("Update {Version} downloaded and verified: {File}", update.Version, target);
            return Result<string>.Success(target);
        }
        catch (OperationCanceledException)
        {
            TryDelete(target);
            return Result<string>.Failure("UPDATE_CANCELED", "The update download was canceled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(target);
            return Result<string>.Failure("UPDATE_DOWNLOAD", $"Could not download the update: {ex.Message}");
        }
    }

    public Result StartInstall(string verifiedFile)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppPaths.ProgramFolder, "TimePlayer.exe");
        var script = InstallKind == InstallKind.Installer
            ? InstallerScript(Environment.ProcessId, verifiedFile, exe)
            : PortableScript(Environment.ProcessId, verifiedFile, AppPaths.ProgramFolder, exe);
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-Command", script },
                CreateNoWindow = true,
                UseShellExecute = false
            });
            _logger.LogInformation("Update helper started ({Kind}).", InstallKind);
            return Result.Success();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Result.Failure("UPDATE_INSTALL", $"Could not start the update: {ex.Message}");
        }
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Wait for TimePlayer to exit, run the installer silently, start TimePlayer again.</summary>
    internal static string InstallerScript(int pid, string installer, string exe) =>
        $"Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; " +
        $"Start-Process -FilePath {Quote(installer)} -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS' -Wait; " +
        $"Remove-Item -LiteralPath {Quote(installer)} -Force -ErrorAction SilentlyContinue; " +
        $"Start-Process -FilePath {Quote(exe)}";

    /// <summary>
    /// Wait for TimePlayer to exit, unpack the zip next to it, copy the new files over the old ones
    /// (the zip holds a "TimePlayer" folder) and start TimePlayer again. Settings, library and logs
    /// are not in the zip, so they are left alone.
    /// </summary>
    internal static string PortableScript(int pid, string zip, string programFolder, string exe)
    {
        var staging = Path.Combine(Path.GetDirectoryName(zip)!, "TimePlayerUpdate-unpacked");
        return
            $"Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; " +
            $"Remove-Item -LiteralPath {Quote(staging)} -Recurse -Force -ErrorAction SilentlyContinue; " +
            $"Expand-Archive -LiteralPath {Quote(zip)} -DestinationPath {Quote(staging)} -Force; " +
            $"$source = Join-Path {Quote(staging)} 'TimePlayer'; if (-not (Test-Path $source)) {{ $source = {Quote(staging)} }}; " +
            // mpv may take a moment to close after TimePlayer; retry while its files are in use.
            $"for ($i = 0; $i -lt 15; $i++) {{ try {{ Copy-Item -Path (Join-Path $source '*') -Destination {Quote(programFolder)} -Recurse -Force -ErrorAction Stop; break }} catch {{ Start-Sleep -Seconds 1 }} }}; " +
            $"Remove-Item -LiteralPath {Quote(staging)} -Recurse -Force -ErrorAction SilentlyContinue; " +
            $"Remove-Item -LiteralPath {Quote(zip)} -Force -ErrorAction SilentlyContinue; " +
            $"Start-Process -FilePath {Quote(exe)}";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = string.Empty;
}
