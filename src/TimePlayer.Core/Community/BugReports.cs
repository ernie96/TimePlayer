using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Core.Community;

/// <summary>What a person sends from Report a problem (Control+Shift+B) or Suggest a feature (Control+Shift+F).</summary>
public sealed record BugReport
{
    public const string Problem = "bug";
    public const string Feature = "feature";

    /// <summary><see cref="Problem"/> or <see cref="Feature"/>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = Problem;
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("contact")] public string? Contact { get; init; }
    [JsonPropertyName("version")] public string Version { get; init; } = AppPaths.Version;
    [JsonPropertyName("language")] public string Language { get; init; } = Localization.Loc.Current;
    [JsonPropertyName("windows")] public string Windows { get; init; } = RuntimeInformation.OSDescription;
    [JsonPropertyName("portable")] public bool Portable { get; init; } = AppPaths.IsPortable;
    [JsonPropertyName("log")] public string? Log { get; init; }
}

public interface IBugReportService
{
    /// <summary>The end of TimePlayer's recent logs, with the Windows user name taken out.</summary>
    string ReadRecentLog();

    /// <summary>Sends the report. Its reference number when it arrived; otherwise an error to show.</summary>
    Task<Result<int>> SendAsync(BugReport report, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends reports to TimePlayer's report relay (a small Cloudflare Worker, in report-relay/), which files each
/// one as an issue in a private GitHub repository. Nobody needs an account, and logs aren't public.
/// </summary>
public sealed class BugReportService : IBugReportService
{
    /// <summary>Where reports go; see report-relay/README.md.</summary>
    public const string RelayUrl = "https://timeplayer-reports.ernie96.workers.dev/report";

    public const int MaxDescriptionLength = 5000;
    public const int MaxContactLength = 200;
    /// <summary>About the last day of normal use; well under the relay's limit even if every character takes two bytes.</summary>
    public const int MaxLogCharacters = 200_000;

    // Titles in other alphabets are sent as they are; the default writes each letter as a six-character escape.
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly ILogger<BugReportService> _logger;
    private readonly string _logsFolder;

    public BugReportService(HttpClient http, ILogger<BugReportService> logger)
        : this(http, logger, AppPaths.LogsFolder)
    {
    }

    public BugReportService(HttpClient http, ILogger<BugReportService> logger, string logsFolder)
    {
        _http = http;
        _logger = logger;
        _logsFolder = logsFolder;
    }

    public string ReadRecentLog()
    {
        try
        {
            if (!Directory.Exists(_logsFolder))
                return string.Empty;
            // Newest last, so the end of the text is the most recent.
            var files = new DirectoryInfo(_logsFolder).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(2).Reverse();
            var text = new StringBuilder();
            foreach (var file in files)
            {
                // The log writer keeps today's file open; share it.
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text.Append("===== ").Append(file.Name).AppendLine(" =====").Append(reader.ReadToEnd()).AppendLine();
            }
            var log = text.Length > MaxLogCharacters ? text.ToString(text.Length - MaxLogCharacters, MaxLogCharacters) : text.ToString();
            return Anonymize(log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the log for a bug report.");
            return string.Empty;
        }
    }

    /// <summary>
    /// Paths in the log contain the Windows user name, which is often a real name: C:\Users\Ana\... becomes
    /// C:\Users\&lt;user&gt;\.... The name on its own is taken out as a whole word only, so a short name
    /// doesn't cut pieces out of other words.
    /// </summary>
    public static string Anonymize(string log)
    {
        log = Regex.Replace(log, @"(?i)(\\Users\\)[^\\/:*?""<>|\r\n]+", "$1<user>");
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user) || user.Length < 3)
            return log;
        return Regex.Replace(log, $@"(?i)(?<!\w){Regex.Escape(user)}(?!\w)", "<user>");
    }

    public async Task<Result<int>> SendAsync(BugReport report, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(report.Description))
            return Result<int>.Failure("INVALID_REPORT", T("Please describe the problem."));
        report = report with
        {
            Description = Clip(report.Description.Trim(), MaxDescriptionLength),
            Contact = string.IsNullOrWhiteSpace(report.Contact) ? null : Clip(report.Contact.Trim(), MaxContactLength),
            Log = report.Log is null ? null : report.Log.Length > MaxLogCharacters ? report.Log[^MaxLogCharacters..] : report.Log
        };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            // A string body has a known length, which the relay checks before reading it.
            using var request = new HttpRequestMessage(HttpMethod.Post, RelayUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(report, Json), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-TimePlayer-Report", "1");
            using var response = await _http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.IsSuccessStatusCode && ParseNumber(body) is { } number)
            {
                _logger.LogInformation("Bug report sent as number {Number}.", number);
                return Result<int>.Success(number);
            }
            _logger.LogWarning("The report relay answered {Status}: {Body}", (int)response.StatusCode, Clip(body, 300));
            return Result<int>.Failure(AppError.NetworkError((int)response.StatusCode == 429
                ? T("Too many reports were sent from this connection just now. Wait a minute and try again.")
                : T("The report service answered with an error ({0}).", (int)response.StatusCode)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not send a bug report.");
            return Result<int>.Failure(AppError.NetworkError(T("Could not reach the report service. Check your internet connection and try again.")));
        }
    }

    /// <summary>The relay answers {"number": 12}.</summary>
    public static int? ParseNumber(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("number", out var n) && n.TryGetInt32(out var number) ? number : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
