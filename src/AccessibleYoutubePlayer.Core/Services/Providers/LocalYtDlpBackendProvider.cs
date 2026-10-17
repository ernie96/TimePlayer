using System.Diagnostics;
using System.Text.Json;
using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccessibleYoutubePlayer.Core.Services.Providers;

/// <summary>
/// Local extraction fallback using yt-dlp when all public API services (Piped, Invidious) fail.
/// Security rule: Executes yt-dlp via ProcessStartInfo.ArgumentList with strict separation;
/// never constructs or concatenates shell command strings.
/// </summary>
public class LocalYtDlpBackendProvider : IYouTubeBackendProvider
{
    public const string OfficialYtDlpDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    private readonly HttpClient _httpClient;
    private readonly ILogger<LocalYtDlpBackendProvider> _logger;
    private readonly string _toolsDirectory;

    public ProviderType ProviderType => ProviderType.LocalFallback;

    public LocalYtDlpBackendProvider(
        string? toolsDirectory = null,
        HttpClient? httpClient = null,
        ILogger<LocalYtDlpBackendProvider>? logger = null)
    {
        _toolsDirectory = toolsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibleYoutubePlayer", "tools");
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<LocalYtDlpBackendProvider>.Instance;
    }

    public async Task<string> EnsureBinaryAvailableAsync(CancellationToken cancellationToken = default)
    {
        // 1. Check tools directory
        var localExe = Path.Combine(_toolsDirectory, "yt-dlp.exe");
        if (File.Exists(localExe)) return localExe;

        // 2. Check application directory
        var appExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");
        if (File.Exists(appExe)) return appExe;

        // 3. Check system PATH
        var pathExe = FindExecutableInPath("yt-dlp.exe");
        if (!string.IsNullOrEmpty(pathExe) && File.Exists(pathExe)) return pathExe;

        // 4. Download standalone yt-dlp.exe automatically so user never has to configure it
        _logger.LogInformation("yt-dlp binary not found. Downloading standalone binary to {Path}...", localExe);
        Directory.CreateDirectory(_toolsDirectory);

        using var response = await _httpClient.GetAsync(OfficialYtDlpDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(localExe, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs, cancellationToken);

        _logger.LogInformation("yt-dlp binary downloaded successfully.");
        return localExe;
    }

    public async Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var binaryPath = await EnsureBinaryAvailableAsync(cancellationToken);

        var args = new[]
        {
            "--dump-json",
            "--default-search", "ytsearch10",
            "--no-playlist",
            "--skip-download",
            "--no-warnings",
            query
        };

        var output = await RunYtDlpAsync(binaryPath, args, cancellationToken);
        var items = new List<VideoMetadata>();

        // yt-dlp outputs one JSON object per line for multiple search results
        using var reader = new StringReader(output);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var meta = ParseSingleVideoJson(line);
                if (meta != null) items.Add(meta);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse yt-dlp line json.");
            }
        }

        return new SearchResult
        {
            Items = items,
            Query = query,
            ProviderUsed = ProviderType.LocalFallback,
            InstanceUsed = "Local yt-dlp"
        };
    }

    public async Task<VideoMetadata> GetVideoDetailsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var binaryPath = await EnsureBinaryAvailableAsync(cancellationToken);
        var targetUrl = $"https://www.youtube.com/watch?v={videoId}";

        var args = new[]
        {
            "--dump-json",
            "--skip-download",
            "--no-playlist",
            "--no-warnings",
            targetUrl
        };

        var output = await RunYtDlpAsync(binaryPath, args, cancellationToken);
        return ParseSingleVideoJson(output) ?? throw new InvalidOperationException("Failed to extract video details via yt-dlp.");
    }

    public async Task<IReadOnlyList<StreamInfo>> GetStreamsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var binaryPath = await EnsureBinaryAvailableAsync(cancellationToken);
        var targetUrl = $"https://www.youtube.com/watch?v={videoId}";

        var args = new[]
        {
            "--dump-json",
            "--skip-download",
            "--no-playlist",
            "--no-warnings",
            targetUrl
        };

        var output = await RunYtDlpAsync(binaryPath, args, cancellationToken);
        return ParseYtDlpStreamsJson(output);
    }

    public async Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var binaryPath = await EnsureBinaryAvailableAsync(cancellationToken);
        var targetUrl = $"https://www.youtube.com/watch?v={videoId}";

        var args = new[]
        {
            "--dump-json",
            "--skip-download",
            "--no-playlist",
            "--no-warnings",
            targetUrl
        };

        var output = await RunYtDlpAsync(binaryPath, args, cancellationToken);
        return ParseYtDlpSubtitlesJson(output);
    }

    public static VideoMetadata? ParseSingleVideoJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var uploader = root.TryGetProperty("uploader", out var u) ? u.GetString() ?? "" : "";
        var duration = root.TryGetProperty("duration", out var d) ? d.GetInt64() : 0;
        var views = root.TryGetProperty("view_count", out var v) ? v.GetInt64() : 0;
        var desc = root.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
        var thumb = root.TryGetProperty("thumbnail", out var th) ? th.GetString() ?? "" : "";
        var isLive = root.TryGetProperty("is_live", out var il) && il.GetBoolean();

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        return new VideoMetadata
        {
            Id = id,
            Title = title,
            Author = uploader,
            Duration = TimeSpan.FromSeconds(Math.Max(0, duration)),
            ViewCount = Math.Max(0, views),
            Description = desc,
            ThumbnailUrl = thumb,
            IsLive = isLive
        };
    }

    public static IReadOnlyList<StreamInfo> ParseYtDlpStreamsJson(string json)
    {
        var list = new List<StreamInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                var url = f.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var ext = f.TryGetProperty("ext", out var e) ? e.GetString() ?? "" : "";
                var vcodec = f.TryGetProperty("vcodec", out var vc) ? vc.GetString() ?? "" : "";
                var acodec = f.TryGetProperty("acodec", out var ac) ? ac.GetString() ?? "" : "";
                var tbr = f.TryGetProperty("tbr", out var tb) ? (int)(tb.GetDouble() * 1000) : 0;
                var height = f.TryGetProperty("height", out var h) ? h.GetInt32() : (int?)null;
                var formatNote = f.TryGetProperty("format_note", out var fn) ? fn.GetString() ?? "" : "";

                var isAudioOnly = vcodec == "none" && acodec != "none";

                if (!string.IsNullOrWhiteSpace(url))
                {
                    list.Add(new StreamInfo
                    {
                        Url = url,
                        Format = ext,
                        Quality = formatNote,
                        Bitrate = tbr,
                        Height = height,
                        IsAudioOnly = isAudioOnly,
                        Codec = isAudioOnly ? acodec : vcodec
                    });
                }
            }
        }

        return list;
    }

    public static IReadOnlyList<SubtitleTrack> ParseYtDlpSubtitlesJson(string json)
    {
        var list = new List<SubtitleTrack>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("subtitles", out var subs) && subs.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in subs.EnumerateObject())
            {
                var langCode = prop.Name;
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in prop.Value.EnumerateArray())
                    {
                        var url = s.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        var ext = s.TryGetProperty("ext", out var e) ? e.GetString() ?? "vtt" : "vtt";
                        var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? langCode : langCode;

                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            list.Add(new SubtitleTrack
                            {
                                Language = langCode,
                                LanguageName = name,
                                Url = url,
                                MimeType = ext == "vtt" ? "text/vtt" : "text/plain",
                                IsAutoGenerated = false
                            });
                            break;
                        }
                    }
                }
            }
        }

        return list;
    }

    private static async Task<string> RunYtDlpAsync(string binaryPath, string[] args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Strict argument separation (no shell concatenation!)
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException($"yt-dlp exited with code {process.ExitCode}: {error}");
        }

        return output;
    }

    private static string? FindExecutableInPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator);
        foreach (var dir in paths)
        {
            var fullPath = Path.Combine(dir, exeName);
            if (File.Exists(fullPath)) return fullPath;
        }

        return null;
    }
}
