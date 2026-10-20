using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Common.Process;
using TimePlayer.Core.Configuration;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Core.Community;

/// <summary>How TimePlayer uses a YouTube sign-in, if there is one.</summary>
public enum AccountUse
{
    /// <summary>Never: TimePlayer works without an account (the default).</summary>
    Off,
    /// <summary>Only for videos YouTube won't play without signing in (age-restricted, members-only).</summary>
    RestrictedOnly,
    /// <summary>Also your YouTube home, subscriptions, history and liked videos, and likes and subscriptions sent to YouTube.</summary>
    Full
}

/// <summary>One cookie of a YouTube sign-in.</summary>
public sealed record SignInCookie(string Domain, bool IncludeSubdomains, string Path, bool Secure, long Expires, string Name, string Value);

public interface IYouTubeAccount
{
    bool IsSignedIn { get; }
    AccountUse Use { get; }
    /// <summary>Signed in, and allowed for restricted videos.</summary>
    bool ForRestrictedVideos => IsSignedIn && Use != AccountUse.Off;
    /// <summary>Signed in, and allowed for the account's pages and actions.</summary>
    bool ForAccountFeatures => IsSignedIn && Use == AccountUse.Full;

    /// <summary>Reads a cookies.txt file (from the Play in TimePlayer extension or another exporter).</summary>
    Task<Result<int>> ImportFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads the sign-in from an installed browser through yt-dlp ("firefox", "chrome", "edge"...).</summary>
    Task<Result<int>> ImportFromBrowserAsync(string browser, CancellationToken cancellationToken = default);

    void SignOut();

    /// <summary>Headers that make an InnerTube request signed in; empty when not signed in.</summary>
    IReadOnlyList<KeyValuePair<string, string>> RequestHeaders(DateTimeOffset now);

    /// <summary>A temporary cookies file for yt-dlp; delete it when done. Null when not signed in.</summary>
    string? WriteTemporaryCookiesFile();
}

/// <summary>
/// An optional YouTube sign-in, kept as browser cookies. They are stored encrypted for the Windows user
/// (DPAPI), only YouTube's and Google's cookies are kept, they are only ever sent to youtube.com, and they
/// never appear in logs or problem reports. Sign out erases them.
/// </summary>
public sealed class YouTubeAccount : IYouTubeAccount
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TimePlayer YouTube sign-in");
    private const string Origin = "https://www.youtube.com";

    private readonly IConfigurationService _config;
    private readonly IYtDlpManager _ytDlp;
    private readonly IProcessRunner _processes;
    private readonly ILogger<YouTubeAccount> _logger;
    private readonly string _storePath;
    private List<SignInCookie> _cookies = new();

    public YouTubeAccount(IConfigurationService config, IYtDlpManager ytDlp, IProcessRunner processes, ILogger<YouTubeAccount> logger)
        : this(config, ytDlp, processes, logger, Path.Combine(AppPaths.DataFolder, "youtube-signin.dat"))
    {
    }

    public YouTubeAccount(IConfigurationService config, IYtDlpManager ytDlp, IProcessRunner processes, ILogger<YouTubeAccount> logger, string storePath)
    {
        _config = config;
        _ytDlp = ytDlp;
        _processes = processes;
        _logger = logger;
        _storePath = storePath;
        Load();
        RemoveLeftoverCookieFiles();
    }

    /// <summary>
    /// The temporary cookie files are deleted right after use; if TimePlayer was closed in between, this
    /// removes them at the next start so a sign-in never stays in the temp folder unencrypted.
    /// </summary>
    private static void RemoveLeftoverCookieFiles()
    {
        try
        {
            foreach (var pattern in new[] { "timeplayer-yt-*.txt", "timeplayer-signin-*.txt" })
                foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), pattern))
                    TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public bool IsSignedIn => _cookies.Count > 0;

    public AccountUse Use => _config.CurrentConfig.Account.Use;

    public async Task<Result<int>> ImportFileAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            return Accept(Parse(text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<int>.Failure("SIGNIN_FILE", T("Could not read that file: {0}", ex.Message));
        }
    }

    public async Task<Result<int>> ImportFromBrowserAsync(string browser, CancellationToken cancellationToken = default)
    {
        if (!_ytDlp.IsInstalled)
        {
            var installed = await _ytDlp.EnsureInstalledOrUpdatedAsync(cancellationToken);
            if (!installed.IsSuccess)
                return Result<int>.Failure(installed.Error!);
        }
        var jar = Path.Combine(Path.GetTempPath(), $"timeplayer-signin-{Guid.NewGuid():N}.txt");
        try
        {
            // yt-dlp reads the browser's cookies and saves them to the jar when it finishes.
            var result = await _processes.RunAsync(_ytDlp.ExecutablePath,
                new[] { "--cookies-from-browser", browser, "--cookies", jar, "--skip-download", "--no-warnings", "--no-playlist",
                        "https://www.youtube.com/watch?v=jNQXAC9IVRw" },
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(90) }, cancellationToken: cancellationToken);
            if (!File.Exists(jar))
            {
                _logger.LogInformation("Reading the sign-in from {Browser} failed (exit {Code}).", browser, result.ExitCode);
                var chromium = browser is "chrome" or "edge" or "brave" or "opera" or "vivaldi";
                return Result<int>.Failure("SIGNIN_BROWSER", chromium
                    ? T("TimePlayer could not read the sign-in from that browser; Chrome-based browsers now lock their cookies. Use the Play in TimePlayer extension's \"Export my YouTube sign-in\" instead, or Firefox.")
                    : T("TimePlayer could not read the sign-in from that browser. Close the browser and try again."));
            }
            return Accept(Parse(await File.ReadAllTextAsync(jar, cancellationToken)));
        }
        finally
        {
            TryDelete(jar);
        }
    }

    private Result<int> Accept(List<SignInCookie> cookies)
    {
        var kept = cookies.Where(c => IsYouTubeOrGoogle(c.Domain)).ToList();
        if (!kept.Any(c => c.Name is "SAPISID" or "__Secure-3PAPISID") || !kept.Any(c => c.Name is "LOGIN_INFO" or "SID" or "__Secure-3PSID"))
            return Result<int>.Failure("SIGNIN_MISSING", T("That doesn't contain a YouTube sign-in. Sign in to YouTube in your browser first, then try again."));
        _cookies = kept;
        Save();
        _logger.LogInformation("Signed in to YouTube ({Count} cookies kept).", kept.Count);
        return Result<int>.Success(kept.Count);
    }

    public void SignOut()
    {
        _cookies = new List<SignInCookie>();
        TryDelete(_storePath);
        _logger.LogInformation("Signed out of YouTube.");
    }

    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders(DateTimeOffset now)
    {
        var youtube = _cookies.Where(c => IsYouTube(c.Domain)).GroupBy(c => c.Name).Select(g => g.First()).ToList();
        if (youtube.Count == 0)
            return Array.Empty<KeyValuePair<string, string>>();
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Cookie", string.Join("; ", youtube.Select(c => $"{c.Name}={c.Value}"))),
            new("Origin", Origin),
            new("X-Origin", Origin),
            new("X-Goog-AuthUser", "0"),
            new("X-Youtube-Bootstrap-Logged-In", "true")
        };
        if (Authorization(youtube, now) is { } authorization)
            headers.Add(new("Authorization", authorization));
        return headers;
    }

    /// <summary>YouTube's SAPISIDHASH: SHA-1 of "time SAPISID origin", for each of the session cookies there are.</summary>
    public static string? Authorization(IReadOnlyCollection<SignInCookie> cookies, DateTimeOffset now)
    {
        string? Value(string name) => cookies.FirstOrDefault(c => c.Name == name)?.Value;
        var time = now.ToUnixTimeSeconds();
        var parts = new List<string>();
        foreach (var (scheme, sid) in new[] { ("SAPISIDHASH", Value("SAPISID") ?? Value("__Secure-3PAPISID")),
                                             ("SAPISID1PHASH", Value("__Secure-1PAPISID")),
                                             ("SAPISID3PHASH", Value("__Secure-3PAPISID")) })
        {
            if (string.IsNullOrEmpty(sid))
                continue;
            var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{time} {sid} {Origin}")));
            parts.Add($"{scheme} {time}_{hash}");
        }
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    public string? WriteTemporaryCookiesFile()
    {
        if (!IsSignedIn)
            return null;
        var path = Path.Combine(Path.GetTempPath(), $"timeplayer-yt-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, Format(_cookies));
        return path;
    }

    // ----- cookies.txt -----

    /// <summary>The Netscape cookies.txt format browsers' exporters and yt-dlp use.</summary>
    public static List<SignInCookie> Parse(string text)
    {
        var cookies = new List<SignInCookie>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.StartsWith("#HttpOnly_", StringComparison.Ordinal) ? raw["#HttpOnly_".Length..] : raw;
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var f = line.Split('\t');
            if (f.Length < 7)
                continue;
            long.TryParse(f[4], out var expires);
            cookies.Add(new SignInCookie(f[0], f[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase), f[2],
                f[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase), expires, f[5], f[6].TrimEnd()));
        }
        return cookies;
    }

    public static string Format(IEnumerable<SignInCookie> cookies)
    {
        var text = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in cookies)
            text.Append(c.Domain).Append('\t').Append(c.IncludeSubdomains ? "TRUE" : "FALSE").Append('\t').Append(c.Path).Append('\t')
                .Append(c.Secure ? "TRUE" : "FALSE").Append('\t').Append(c.Expires).Append('\t').Append(c.Name).Append('\t').Append(c.Value).Append('\n');
        return text.ToString();
    }

    private static bool IsYouTube(string domain) =>
        domain.TrimStart('.').Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsYouTubeOrGoogle(string domain) =>
        IsYouTube(domain) ||
        domain.TrimStart('.').Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase);

    // ----- encrypted store -----

    private void Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_storePath))
            return;
        try
        {
            var bytes = Unprotect(File.ReadAllBytes(_storePath));
            _cookies = Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The saved YouTube sign-in could not be read; sign in again. ({Type})", ex.GetType().Name);
            _cookies = new List<SignInCookie>();
        }
    }

    private void Save()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        File.WriteAllBytes(_storePath, Protect(Encoding.UTF8.GetBytes(Format(_cookies))));
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
