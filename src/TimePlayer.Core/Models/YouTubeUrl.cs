using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using TimePlayer.Core.Common;

namespace TimePlayer.Core.Models;

/// <summary>
/// Parser and normalizer for YouTube URLs and identifiers.
/// Supports standard watch URLs, mobile URLs, youtu.be short links, embed/v/shorts/live paths,
/// and timestamp extraction (e.g. ?t=1m30s or ?t=90).
/// </summary>
public sealed record YouTubeUrl
{
    private static readonly Regex WatchPattern = new(@"[?&]v=([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);
    private static readonly Regex PathIdPattern = new(@"/(?:embed|v|shorts|live)/([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);
    private static readonly Regex ShortUrlPattern = new(@"youtu\.be/([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);
    private static readonly Regex TimeHmsPattern = new(@"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public YouTubeVideoId VideoId { get; }
    public TimeSpan? StartTime { get; }
    public string OriginalInput { get; }
    public string NormalizedWatchUrl => StartTime.HasValue && StartTime.Value > TimeSpan.Zero
        ? $"https://www.youtube.com/watch?v={VideoId.Value}&t={(int)StartTime.Value.TotalSeconds}s"
        : $"https://www.youtube.com/watch?v={VideoId.Value}";

    private YouTubeUrl(YouTubeVideoId videoId, TimeSpan? startTime, string originalInput)
    {
        VideoId = videoId;
        StartTime = startTime;
        OriginalInput = originalInput;
    }

    public static bool TryParse(string? input, [NotNullWhen(true)] out YouTubeUrl? youtubeUrl)
    {
        youtubeUrl = null;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();

        // 1. Direct 11-char Video ID
        if (YouTubeVideoId.TryParse(trimmed, out var directId))
        {
            youtubeUrl = new YouTubeUrl(directId, null, trimmed);
            return true;
        }

        // 2. Parse as URI or URL-like string
        string idString = string.Empty;
        TimeSpan? startTime = null;

        // Try extracting time query parameter
        startTime = ExtractTimestamp(trimmed);

        // Check youtu.be/ID
        var shortMatch = ShortUrlPattern.Match(trimmed);
        if (shortMatch.Success)
        {
            idString = shortMatch.Groups[1].Value;
        }
        else
        {
            // Check watch?v=ID
            var watchMatch = WatchPattern.Match(trimmed);
            if (watchMatch.Success)
            {
                idString = watchMatch.Groups[1].Value;
            }
            else
            {
                // Check /embed/ID, /shorts/ID, /live/ID, /v/ID
                var pathMatch = PathIdPattern.Match(trimmed);
                if (pathMatch.Success)
                {
                    idString = pathMatch.Groups[1].Value;
                }
            }
        }

        if (!string.IsNullOrEmpty(idString) && YouTubeVideoId.TryParse(idString, out var parsedId))
        {
            youtubeUrl = new YouTubeUrl(parsedId, startTime, trimmed);
            return true;
        }

        return false;
    }

    public static YouTubeUrl Parse(string input)
    {
        if (TryParse(input, out var url))
            return url;

        throw new FormatException($"'{input}' is not a valid YouTube URL or video ID.");
    }

    /// <summary>
    /// Whether typed or pasted text should be opened as a link rather than searched for: any YouTube link,
    /// or a bare video id that doesn't read like a word. "taylorswift" or "minecraft12" are 11 characters
    /// like an id, but they're searches; real ids nearly always mix capitals, small letters and digits.
    /// </summary>
    public static bool LooksLikeLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        if (trimmed.Contains('/') || trimmed.Contains('.') || trimmed.Contains('?'))
            return IsPlaylistLink(trimmed) || TryParse(trimmed, out _);
        return YouTubeVideoId.TryParse(trimmed, out _) &&
               trimmed.Any(char.IsUpper) && trimmed.Any(char.IsLower) &&
               trimmed.Any(c => char.IsDigit(c) || c is '-' or '_');
    }

    /// <summary>The id after "list=" in a YouTube link, for opening playlists.</summary>
    public static bool TryGetPlaylistId(string? input, [NotNullWhen(true)] out string? playlistId)
    {
        playlistId = null;
        if (string.IsNullOrWhiteSpace(input) || !input.Contains("youtu", StringComparison.OrdinalIgnoreCase))
            return false;
        var match = System.Text.RegularExpressions.Regex.Match(input, @"[?&]list=([A-Za-z0-9_-]{10,})");
        if (!match.Success)
            return false;
        playlistId = match.Groups[1].Value;
        return true;
    }

    /// <summary>A link to a playlist itself (not a video that happens to be in one).</summary>
    public static bool IsPlaylistLink(string? input) =>
        TryGetPlaylistId(input, out _) && !WatchPattern.IsMatch(input!.Trim());

    public static Result<YouTubeUrl> Validate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Result<YouTubeUrl>.Failure(AppError.InvalidUrl("Input cannot be empty. Please enter a video search term or paste a YouTube URL."));
        }

        var trimmed = input.Trim();

        // Check for playlists
        if (trimmed.Contains("playlist?list=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("&list=", StringComparison.OrdinalIgnoreCase))
        {
            // If it also has a v= parameter, it's a video within a playlist (which is playable as a video)
            if (WatchPattern.IsMatch(trimmed) && TryParse(trimmed, out var validVideoUrl))
            {
                return Result<YouTubeUrl>.Success(validVideoUrl);
            }
            return Result<YouTubeUrl>.Failure(AppError.UnsupportedUrl(
                "Playlists are not supported for single-video playback. Please provide an individual video link or use Search to find videos.",
                trimmed));
        }

        // Check for channels or user profiles
        if (trimmed.Contains("youtube.com/@", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("youtube.com/channel/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("youtube.com/c/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("youtube.com/user/", StringComparison.OrdinalIgnoreCase))
        {
            return Result<YouTubeUrl>.Failure(AppError.UnsupportedUrl(
                "Channel pages cannot be played directly. Use the Search box to search for videos from this channel.",
                trimmed));
        }

        // Attempt normal parsing
        if (TryParse(trimmed, out var parsed))
        {
            return Result<YouTubeUrl>.Success(parsed);
        }

        // General malformed or non-YouTube URL
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Result<YouTubeUrl>.Failure(AppError.UnsupportedUrl(
                "The link provided does not appear to be a supported YouTube video URL.",
                trimmed));
        }

        return Result<YouTubeUrl>.Failure(AppError.InvalidUrl($"'{trimmed}' is not a valid 11-character YouTube video ID or supported URL."));
    }

    public static string Normalize(string input)
    {
        var url = Parse(input);
        return url.NormalizedWatchUrl;
    }

    private static TimeSpan? ExtractTimestamp(string url)
    {
        // Look for ?t= or &t= or ?start= or &start=
        var timeMatch = Regex.Match(url, @"[?&](?:t|start)=([^&#]+)", RegexOptions.IgnoreCase);
        if (!timeMatch.Success)
            return null;

        var rawTime = timeMatch.Groups[1].Value.Trim().TrimEnd('s');

        // Numeric seconds
        if (double.TryParse(rawTime, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        // HMS format: e.g. 1h2m3s or 2m15s
        var hmsMatch = TimeHmsPattern.Match(timeMatch.Groups[1].Value.Trim());
        if (hmsMatch.Success)
        {
            int h = hmsMatch.Groups[1].Success ? int.Parse(hmsMatch.Groups[1].Value) : 0;
            int m = hmsMatch.Groups[2].Success ? int.Parse(hmsMatch.Groups[2].Value) : 0;
            int s = hmsMatch.Groups[3].Success ? int.Parse(hmsMatch.Groups[3].Value) : 0;
            return new TimeSpan(h, m, s);
        }

        return null;
    }

    public override string ToString() => NormalizedWatchUrl;
}
